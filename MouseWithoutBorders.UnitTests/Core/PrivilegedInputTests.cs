using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class PrivilegedInputTests
{
    private Dictionary<long, NativeMethods.INPUT64> originalDebt = null!;
    private object? originalNeedsDrain, originalRecovering;
    private HashSet<string> originalFailures = null!;
    private static HashSet<string> ReportedFailures => (HashSet<string>)Field("reportedFailures").GetValue(null)!;
    private static Dictionary<long, NativeMethods.INPUT64> ClientDebt => (Dictionary<long, NativeMethods.INPUT64>)Field("clientHeldInput").GetValue(null)!;
    [TestInitialize]
    public void SaveDebt()
    {
        originalDebt = new(ClientDebt); ClientDebt.Clear();
        originalFailures = new(ReportedFailures); ReportedFailures.Clear();
        originalNeedsDrain = Field("clientNeedsDrain").GetValue(null); originalRecovering = Field("recoveringInput").GetValue(null);
        Field("clientNeedsDrain").SetValue(null, false); Field("recoveringInput").SetValue(null, false);
    }
    [TestCleanup]
    public void RestoreDebt()
    {
        ClientDebt.Clear(); foreach (var entry in originalDebt) ClientDebt.Add(entry.Key, entry.Value);
        ReportedFailures.Clear(); ReportedFailures.UnionWith(originalFailures);
        Field("clientNeedsDrain").SetValue(null, originalNeedsDrain); Field("recoveringInput").SetValue(null, originalRecovering);
    }
    private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private static FieldInfo Field(string name) => typeof(PrivilegedInput).GetField(name, Private)!;
    private static uint Exchange(NativeMethods.INPUT64 input) => (uint)typeof(PrivilegedInput).GetMethod("ExchangeInput", Private)!.Invoke(null, [input])!;

    [TestMethod]
    public void NativeWatchdogDistinguishesStalledCallsFromOngoingInputAndQuiescence()
    {
        var progress = new InputSimulation.NativeInputProgress();
        Assert.IsFalse(progress.ObserveStall());
        progress.Enter(); Assert.IsFalse(progress.ObserveStall());
        progress.Exit(); progress.Enter(); Assert.IsFalse(progress.ObserveStall(), "Ongoing input completions are progress.");
        Assert.IsTrue(progress.ObserveStall()); Assert.IsFalse(progress.ObserveStall(), "A stall is reported once.");
        progress.Exit(); Assert.IsFalse(progress.ObserveStall()); Assert.IsFalse(progress.ObserveStall(), "An idle receiver is healthy.");
        progress.Enter(); Assert.IsFalse(progress.ObserveStall()); Assert.IsTrue(progress.ObserveStall(), "A later independent stall must remain observable."); progress.Exit();
    }

    [DataTestMethod]
    [DataRow(1, 5)]
    [DataRow(2, 170)]
    [DataRow(3, 0)]
    [DataRow(4, 6)]
    public void BrokerReplyPreservesFailureAndHeldOwnership(int stage, int error)
    {
        byte[] reply = new byte[PrivilegedInput.ReplySize];
        var failure = new PrivilegedInput.InjectionFailure((PrivilegedInput.InjectionStage)stage, error);
        PrivilegedInput.WriteReply(reply, 0, 37, failure);
        Assert.AreEqual(74u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        Assert.AreEqual(failure, PrivilegedInput.ReadFailure(reply));
        PrivilegedInput.WriteReply(reply, 1, 0, default);
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        Assert.AreEqual(default(PrivilegedInput.InjectionFailure), PrivilegedInput.ReadFailure(reply), "A successful reply must clear the prior native failure.");
    }

    [TestMethod]
    public async Task ARejectedBrokerInputCarriesItsNativeFailureWithoutManufacturingAHold()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-rejection-diagnostic-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync(1000); await connected;
        var worker = Task.Run(async () =>
        {
            await server.ReadExactlyAsync(new byte[Marshal.SizeOf<NativeMethods.INPUT64>()]);
            byte[] reply = new byte[PrivilegedInput.ReplySize];
            PrivilegedInput.WriteReply(reply, 0, 0, new(PrivilegedInput.InjectionStage.AttachDesktop, 170));
            // An overlapped pipe read may split the status from its diagnostic fields.
            await server.WriteAsync(reply.AsMemory(0, 4));
            await server.WriteAsync(reply.AsMemory(4));
        });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 input = default; input.type = 1; input.ki.wVk = 0x87;
            Assert.AreEqual(0u, Exchange(input)); await worker;
            Assert.AreEqual(0, ClientDebt.Count); Assert.IsFalse(PrivilegedInput.HasHeldInput);
            Assert.AreEqual(true, Field("recoveringInput").GetValue(null), "A live worker rejecting injection is a failure, even without an exception.");
        }
        finally { Field("inputPipe").SetValue(null, oldPipe); Field("brokerOwnsInput").SetValue(null, oldOwns); }
    }

    [TestMethod]
    public async Task ANewInjectionFailureIsLoggedOnceEvenWhenAlreadyRecovering()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-failure-stages-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); await client.ConnectAsync(1000); await connected;
        int Logged(PrivilegedInput.InjectionStage stage) => ((string[])typeof(Logger).GetField("AllLogs", Private)!.GetValue(null)!)
            .Count(line => line?.Contains("Protected input rejected: stage=" + stage + ";", StringComparison.Ordinal) == true);
        int beforeOpen = Logged(PrivilegedInput.InjectionStage.OpenDesktop), beforeAttach = Logged(PrivilegedInput.InjectionStage.AttachDesktop);
        var worker = Task.Run(async () =>
        {
            foreach (var stage in new[] { PrivilegedInput.InjectionStage.OpenDesktop, PrivilegedInput.InjectionStage.OpenDesktop, PrivilegedInput.InjectionStage.AttachDesktop, PrivilegedInput.InjectionStage.AttachDesktop })
            {
                await server.ReadExactlyAsync(new byte[Marshal.SizeOf<NativeMethods.INPUT64>()]);
                byte[] reply = new byte[PrivilegedInput.ReplySize];
                PrivilegedInput.WriteReply(reply, 0, 0, new(stage, 5)); await server.WriteAsync(reply);
            }
        });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("brokerOwnsInput").SetValue(null, false);
            Field("recoveringInput").SetValue(null, true); // A startup connection failure must not hide later injection evidence.
            for (int i = 0; i < 4; i++) Assert.AreEqual(0u, Exchange(default));
            await worker;
            Assert.AreEqual(beforeOpen + 1, Logged(PrivilegedInput.InjectionStage.OpenDesktop));
            Assert.AreEqual(beforeAttach + 1, Logged(PrivilegedInput.InjectionStage.AttachDesktop));
        }
        finally { Field("inputPipe").SetValue(null, oldPipe); Field("brokerOwnsInput").SetValue(null, oldOwns); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PublicMouseMovementDoesNotWaitForAnUnrelatedStateChange(bool wheel)
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldAgent = Field("inputAgent").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        bool oldVisible = Common.MainFormVisible;
        int oldPaint = Common.PaintCount;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Exception? ownerError = null, mouseError = null;
        uint sent = uint.MaxValue;
        var owner = new System.Threading.Thread(() =>
        {
            try { Common.DoSomethingInTheInputSimulationThread(() => { entered.Set(); release.Wait(); }); }
            catch (Exception error) { ownerError = error; }
        }) { IsBackground = true };
        var mouse = new System.Threading.Thread(() =>
        {
            try
            {
                if (wheel) sent = InputSimulation.SendMouse(new MOUSEDATA { dwFlags = WM.WM_MOUSEWHEEL });
                else InputSimulation.MoveMouseRelative(0, 0);
            }
            catch (Exception error) { mouseError = error; }
        }) { IsBackground = true };
        bool completedBeforeRelease = false;
        try
        {
            Field("inputPipe").SetValue(null, null); Field("inputAgent").SetValue(null, null); Field("brokerOwnsInput").SetValue(null, true);
            Common.MainFormVisible = false;
            NativeMethods.INPUT64 input = default; input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE;
            Assert.AreEqual(1u, (uint)typeof(InputSimulation).GetMethod("SendInputEx", Private)!.Invoke(null, [input])!,
                "This canary requires successful zero-delta native movement on the current desktop.");
            owner.Start();
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)), "The state-changing input did not acquire its actual simulation lock.");
            mouse.Start();
            completedBeforeRelease = mouse.Join(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            try
            {
                if ((owner.ThreadState & System.Threading.ThreadState.Unstarted) == 0) Assert.IsTrue(owner.Join(TimeSpan.FromSeconds(3)));
                if ((mouse.ThreadState & System.Threading.ThreadState.Unstarted) == 0) Assert.IsTrue(mouse.Join(TimeSpan.FromSeconds(3)));
                Assert.IsTrue(PrivilegedInput.HasHeldInput, "Independent movement must not release or forget the broker-owned input.");
            }
            finally
            {
                Field("inputPipe").SetValue(null, oldPipe); Field("inputAgent").SetValue(null, oldAgent); Field("brokerOwnsInput").SetValue(null, oldOwns);
                Common.MainFormVisible = oldVisible; Common.PaintCount = oldPaint;
            }
        }
        Assert.IsNull(ownerError); Assert.IsNull(mouseError);
        Assert.AreEqual(!wheel, completedBeforeRelease, wheel
            ? "Wheel/button state changes must remain serialized with their input owner."
            : "Public zero-delta MOVE must not wait for an unrelated key/button broker exchange.");
        if (wheel) Assert.AreEqual(0u, sent, "An unavailable held-input owner must still block the wheel packet's native injection.");
    }

    [TestMethod]
    public void BrokerOwnedDownDoesNotMakeIndependentMovementWaitForItsReply()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldAgent = Field("inputAgent").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        var send = typeof(InputSimulation).GetMethod("SendInputEx", Private)!;
        try
        {
            Field("inputPipe").SetValue(null, null); Field("inputAgent").SetValue(null, null); Field("brokerOwnsInput").SetValue(null, true);
            NativeMethods.INPUT64 input = default;
            input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE; // Zero relative delta leaves the pointer in place.
            Assert.AreEqual(1u, (uint)send.Invoke(null, [input])!, "Native-successful movement must not wait for an unrelated held key's broker reply.");
            Assert.IsTrue(PrivilegedInput.HasHeldInput, "Movement must not forget the broker's matching UP responsibility.");
            foreach (var action in new[] { NativeMethods.MOUSEEVENTF.LEFTDOWN, NativeMethods.MOUSEEVENTF.LEFTUP, NativeMethods.MOUSEEVENTF.WHEEL, NativeMethods.MOUSEEVENTF.XUP })
            {
                input.mi.dwFlags = (int)(NativeMethods.MOUSEEVENTF.MOVE | action);
                Assert.AreEqual(0u, (uint)send.Invoke(null, [input])!, "MOVE on a button/wheel packet must not bypass the unavailable owner: " + action);
            }
            input = default; input.type = 1; input.ki.wVk = 0x87; input.ki.dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP;
            Assert.AreEqual(0u, (uint)send.Invoke(null, [input])!, "Key releases must still use the held input's owner.");
        }
        finally
        {
            Field("inputPipe").SetValue(null, oldPipe); Field("inputAgent").SetValue(null, oldAgent); Field("brokerOwnsInput").SetValue(null, oldOwns);
        }
    }

    [TestMethod]
    public async Task AgentDesktopHandleAllowsNativeInput()
    {
        NativeMethods.INPUT64 input = default;
        input.type = 1;
        input.ki.wVk = 0x87; // F24 UP does not type or activate a window.
        input.ki.dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP;
        uint sent = await Task.Run(() => (uint)typeof(PrivilegedInput).GetMethod("Inject", Private)!.Invoke(null, [input])!);
        Assert.AreEqual(1u, sent, "Attaching to the input desktop must include the access SendInput needs.");
    }

    [TestMethod]
    public void ProtectedBinaryUsesUserDataButPortableBinaryKeepsAdjacentPreferences()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.AreEqual(Path.Combine(local, "MouseWithoutBordersPlus", PortableApplication.SettingsFileName),
            PortableApplication.SettingsPathForExecutable(PrivilegedInput.ProtectedExecutable));
        string portable = Path.Combine(Path.GetTempPath(), "mwb-synthetic-portable", "MouseWithoutBordersPlus.exe");
        Assert.AreEqual(Path.Combine(Path.GetDirectoryName(portable)!, PortableApplication.SettingsFileName),
            PortableApplication.SettingsPathForExecutable(portable));
    }

    [TestMethod]
    public void AgentRoleRejectsOrdinaryUserBeforeAnyProductLaunch()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) Assert.Inconclusive("This rejection canary requires an ordinary test process.");
        int previous = Environment.ExitCode;
        try
        {
            Assert.IsTrue(PrivilegedInput.TryRun([Environment.ProcessPath!, PrivilegedInput.AgentArgument, "synthetic-stop-event"]));
            Assert.AreEqual(1, Environment.ExitCode);
            Assert.IsFalse(PrivilegedInput.TryRun([Environment.ProcessPath!, "--ordinary-product-argument"]));
        }
        finally { Environment.ExitCode = previous; }
    }

    [TestMethod]
    public async Task BrokerOwnsTheDownThroughItsMatchingUpReply()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldAgent = Field("inputAgent").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-input-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync(1000); await connected;
        var serve = Task.Run(async () =>
        {
            byte[] bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()], reply = new byte[PrivilegedInput.ReplySize];
            for (int i = 0; i < 2; i++)
            {
                await server.ReadExactlyAsync(bytes);
                Assert.AreEqual(1, MemoryMarshal.Read<NativeMethods.INPUT64>(bytes).type);
                BinaryPrimitives.WriteUInt32LittleEndian(reply, i == 0 ? 3u : 1u); // sent + one held, then sent + none held
                await server.WriteAsync(reply);
            }
        });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("inputAgent").SetValue(null, null); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 input = default; input.type = 1; input.ki.wVk = 0x11; input.ki.wScan = 29;
            Assert.AreEqual(1u, Exchange(input));
            Assert.IsTrue(PrivilegedInput.HasHeldInput, "Returning from UAC must not let a native-successful UP bypass this broker DOWN.");
            input.ki.dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP;
            Assert.AreEqual(1u, Exchange(input));
            Assert.IsFalse(PrivilegedInput.HasHeldInput, "The matching UP reply returns ordinary input to the native fast path.");
            await serve;
        }
        finally
        {
            Field("inputPipe").SetValue(null, oldPipe); Field("inputAgent").SetValue(null, oldAgent); Field("brokerOwnsInput").SetValue(null, oldOwns);
        }
    }

    [TestMethod]
    public async Task MissingReplyCancelsWithoutGivingUncertainOwnershipToNewNativeInput()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldAgent = Field("inputAgent").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-input-timeout-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync(1000); await connected;
        var serve = Task.Run(async () =>
        {
            byte[] bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()];
            await server.ReadExactlyAsync(bytes);
            await Task.Delay(1000); // No reply: the agent might already have injected the DOWN.
            server.Dispose();
        });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("inputAgent").SetValue(null, null); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 input = default; input.type = 1; input.ki.wVk = 0x11;
            var failure = Assert.ThrowsException<TargetInvocationException>(() => Exchange(input));
            Assert.IsInstanceOfType<OperationCanceledException>(failure.InnerException);
            Assert.IsTrue(PrivilegedInput.HasHeldInput, "An unanswered DOWN cannot permit a new native DOWN before old-owner cleanup.");
            await serve;
        }
        finally
        {
            Field("inputPipe").SetValue(null, oldPipe); Field("inputAgent").SetValue(null, oldAgent); Field("brokerOwnsInput").SetValue(null, oldOwns);
        }
    }

    [TestMethod]
    public async Task LostMoveReplyDoesNotCreateAKeyboardOrButtonOwner()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string pipeName = "mwb-move-debt-" + Guid.NewGuid().ToString("N");
        using var endpoint = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = endpoint.WaitForConnectionAsync(); await client.ConnectAsync(1000); await connected;
        try
        {
            Field("inputPipe").SetValue(null, client); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 move = default; move.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE;
            Assert.ThrowsException<TargetInvocationException>(() => Exchange(move));
            Assert.IsFalse(PrivilegedInput.HasHeldInput, "A timed-out movement cannot leave ordinary keys permanently behind a fictitious held-input owner.");
            Assert.AreEqual(0, ClientDebt.Count);
        }
        finally { Field("inputPipe").SetValue(null, oldPipe); Field("brokerOwnsInput").SetValue(null, oldOwns); }
    }

    [DataTestMethod]
    [DataRow(0, 0)] [DataRow(2, 0)] [DataRow(8, 0)] [DataRow(32, 0)] [DataRow(128, 1)] [DataRow(128, 2)]
    public async Task AnAppliedDownWithALostReplyIsReleasedThroughTheReplacementWorker(int mouseFlag, int mouseData)
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null), oldAgent = Field("inputAgent").GetValue(null);
        string name = "mwb-debt-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); await client.ConnectAsync(1000); await connected;
        var applied = new List<NativeMethods.INPUT64>();
        var firstWorker = Task.Run(async () => { var bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()]; await server.ReadExactlyAsync(bytes); applied.Add(MemoryMarshal.Read<NativeMethods.INPUT64>(bytes)); });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 down = default; down.type = 1; down.ki.wVk = 0x11; down.ki.wScan = 29; down.ki.dwFlags = (int)NativeMethods.KEYEVENTF.EXTENDEDKEY;
            if (mouseFlag != 0) { down = default; down.mi.dwFlags = mouseFlag; down.mi.mouseData = mouseData; }
            Assert.ThrowsException<TargetInvocationException>(() => Exchange(down)); await firstWorker;
            Assert.AreEqual(1, applied.Count); Assert.AreEqual(1, ClientDebt.Count);
            using var exited = new EventWaitHandle(true, EventResetMode.ManualReset);
            using var handle = new SafeProcessHandle(exited.SafeWaitHandle.DangerousGetHandle(), false);
            Field("inputAgent").SetValue(null, handle);
            typeof(PrivilegedInput).GetMethod("DiscardExitedAgent", Private)!.Invoke(null, null);
            Assert.IsTrue(PrivilegedInput.HasHeldInput, "The worker's death must not forgive its applied DOWN.");
            string replacementName = "mwb-replacement-" + Guid.NewGuid().ToString("N");
            using var replacement = new NamedPipeServerStream(replacementName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var next = new NamedPipeClientStream(".", replacementName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var ready = replacement.WaitForConnectionAsync(); await next.ConnectAsync(1000); await ready;
            Field("inputPipe").SetValue(null, next);
            var worker = Task.Run(async () =>
            {
                var bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()]; await replacement.ReadExactlyAsync(bytes);
                var release = MemoryMarshal.Read<NativeMethods.INPUT64>(bytes);
                Assert.AreEqual(down.type, release.type);
                if (down.type == 1)
                {
                    Assert.AreEqual(down.ki.wVk, release.ki.wVk); Assert.AreEqual(down.ki.wScan, release.ki.wScan);
                    Assert.AreEqual((int)(NativeMethods.KEYEVENTF.KEYUP | NativeMethods.KEYEVENTF.EXTENDEDKEY), release.ki.dwFlags);
                }
                else { Assert.AreEqual(mouseFlag == 128 ? 256 : mouseFlag * 2, release.mi.dwFlags); Assert.AreEqual(mouseData, release.mi.mouseData); }
                await replacement.WriteAsync(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            });
            Assert.IsTrue((bool)typeof(PrivilegedInput).GetMethod("DrainClientHeldInput", Private)!.Invoke(null, null)!);
            await worker; Assert.AreEqual(0, ClientDebt.Count); Assert.IsFalse(PrivilegedInput.HasHeldInput);
        }
        finally { Field("inputPipe").SetValue(null, oldPipe); Field("brokerOwnsInput").SetValue(null, oldOwns); Field("inputAgent").SetValue(null, oldAgent); }
    }

    [TestMethod]
    public async Task AnExplicitlyRejectedDownDoesNotCreateReleaseDebt()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-rejected-down-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); await client.ConnectAsync(1000); await connected;
        var worker = Task.Run(async () => { await server.ReadExactlyAsync(new byte[Marshal.SizeOf<NativeMethods.INPUT64>()]); await server.WriteAsync(new byte[PrivilegedInput.ReplySize]); });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 down = default; down.type = 1; down.ki.wVk = 0x57;
            Assert.AreEqual(0u, Exchange(down)); await worker;
            Assert.AreEqual(0, ClientDebt.Count); Assert.IsFalse(PrivilegedInput.HasHeldInput);
        }
        finally { Field("inputPipe").SetValue(null, oldPipe); Field("brokerOwnsInput").SetValue(null, oldOwns); }
    }

    [TestMethod]
    public async Task APartiallyRejectedCleanupIsRetriedBeforeFreshInputOnTheSamePipe()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-partial-drain-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); await client.ConnectAsync(1000); await connected;
        var seen = new List<int>();
        var worker = Task.Run(async () =>
        {
            foreach (byte count in new byte[] { 1, 0, 1 })
            {
                var bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()]; await server.ReadExactlyAsync(bytes);
                var release = MemoryMarshal.Read<NativeMethods.INPUT64>(bytes); seen.Add(release.ki.wVk);
                Assert.AreNotEqual(0, release.ki.dwFlags & (int)NativeMethods.KEYEVENTF.KEYUP);
                await server.WriteAsync(new byte[] { count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            }
        });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("brokerOwnsInput").SetValue(null, true); Field("clientNeedsDrain").SetValue(null, true);
            foreach (short key in new short[] { 0x57, 0x11 })
            {
                NativeMethods.INPUT64 down = default; down.type = 1; down.ki.wVk = key;
                typeof(PrivilegedInput).GetMethod("TrackHeldInput", Private)!.Invoke(null, [ClientDebt, down]);
            }
            var drain = typeof(PrivilegedInput).GetMethod("DrainReconnectedInput", Private)!;
            Assert.IsFalse((bool)drain.Invoke(null, null)!); Assert.AreEqual(1, ClientDebt.Count);
            Assert.IsTrue((bool)Field("clientNeedsDrain").GetValue(null)!); Assert.IsTrue(PrivilegedInput.HasHeldInput);
            Assert.IsTrue((bool)drain.Invoke(null, null)!); await worker;
            CollectionAssert.AreEqual(new[] { 0x57, 0x11, 0x11 }, seen);
            Assert.AreEqual(0, ClientDebt.Count); Assert.IsFalse((bool)Field("clientNeedsDrain").GetValue(null)!);
        }
        finally { Field("inputPipe").SetValue(null, oldPipe); Field("brokerOwnsInput").SetValue(null, oldOwns); }
    }

    [TestMethod]
    public async Task ABlockedInjectionExpiresAndAnOldDeadlineCannotKillTheNextOperation()
    {
        using var blocked = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var expired = new ManualResetEventSlim();
        var guard = new PrivilegedInput.InjectionDeadline(TimeSpan.FromMilliseconds(100), () => expired.Set());
        var stalled = Task.Run(() => Assert.ThrowsException<System.TimeoutException>(() => guard.Run(() => { blocked.Set(); release.Wait(); return 1u; })));
        try { Assert.IsTrue(blocked.Wait(1000)); Assert.IsTrue(expired.Wait(2000)); }
        finally { release.Set(); await stalled.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.ThrowsException<System.TimeoutException>(() => guard.Run(() => 1));

        int terminations = 0;
        var healthy = new PrivilegedInput.InjectionDeadline(TimeSpan.FromSeconds(2), () => Interlocked.Increment(ref terminations));
        object? completed = null;
        Assert.AreEqual(1u, healthy.Run(() => { completed = typeof(PrivilegedInput.InjectionDeadline).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(healthy); return 1; }));
        Assert.AreEqual(1u, healthy.Run(() => { typeof(PrivilegedInput.InjectionDeadline).GetMethod("Expire", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(healthy, [completed]); return 1; }));
        Assert.AreEqual(0, terminations, "A late timer for a completed request must not retire a healthy worker.");
    }

    [TestMethod]
    public async Task LeavingTheRemoteDesktopDisconnectsItsOwnerWithoutForgettingAnUnconfirmedDown()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldAgent = Field("inputAgent").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        string name = "mwb-input-leave-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync(1000); await connected;
        var serve = Task.Run(async () =>
        {
            byte[] bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()], reply = new byte[PrivilegedInput.ReplySize];
            await server.ReadExactlyAsync(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(reply, 3u);
            await server.WriteAsync(reply);
            Assert.AreEqual(0, await server.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(2)), "The agent must observe disconnect and run its existing held-input cleanup.");
        });
        try
        {
            Field("inputPipe").SetValue(null, client); Field("inputAgent").SetValue(null, null); Field("brokerOwnsInput").SetValue(null, false);
            NativeMethods.INPUT64 input = default; input.type = 1; input.ki.wVk = 0x11;
            Assert.AreEqual(1u, Exchange(input));
            Common.DoSomethingInTheInputSimulationThread(PrivilegedInput.ReleaseConnection);
            Assert.IsNull(Field("inputPipe").GetValue(null));
            Assert.IsTrue(PrivilegedInput.HasHeldInput, "A new native DOWN must wait until old-owner cleanup is confirmed by a reply or agent exit.");
            await serve;
        }
        finally
        {
            Field("inputPipe").SetValue(null, oldPipe); Field("inputAgent").SetValue(null, oldAgent); Field("brokerOwnsInput").SetValue(null, oldOwns);
        }
    }

    [TestMethod]
    public void DisconnectCleanupContainsOnlyOwnedButtonsAndDistinctExtendedKeys()
    {
        var held = new Dictionary<long, NativeMethods.INPUT64>();
        var track = typeof(PrivilegedInput).GetMethod("TrackHeldInput", Private)!;
        NativeMethods.INPUT64 key = default; key.type = 1; key.ki.wVk = 0x11; key.ki.wScan = 29;
        track.Invoke(null, [held, key]);
        key.ki.dwFlags = (int)NativeMethods.KEYEVENTF.EXTENDEDKEY;
        track.Invoke(null, [held, key]);
        Assert.AreEqual(2, held.Count, "Generic left/right Control can share virtual key and scan code; EXTENDED distinguishes them.");
        key.ki.dwFlags |= (int)NativeMethods.KEYEVENTF.KEYUP;
        track.Invoke(null, [held, key]);
        Assert.AreEqual(1, held.Count);
        NativeMethods.INPUT64 mouse = default; mouse.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTDOWN;
        track.Invoke(null, [held, mouse]);
        Assert.AreEqual(2, held.Count);
        Assert.IsTrue(held.Values.All(input => input.type == 1
            ? (input.ki.dwFlags & (int)NativeMethods.KEYEVENTF.KEYUP) != 0
            : input.mi.dwFlags == (int)NativeMethods.MOUSEEVENTF.LEFTUP));
        mouse.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTUP;
        track.Invoke(null, [held, mouse]);
        Assert.AreEqual(1, held.Count);
    }

    [TestMethod]
    public void MissingInvalidOrUnreadableAuthorizedAccountDoesNotDelegateInput()
    {
        const string sid = "S-1-5-21-100-200-300-1001";
        Assert.AreEqual(sid, PrivilegedInput.NormalizeAuthorizedUserSid(sid));
        Assert.ThrowsException<ArgumentException>(() => PrivilegedInput.NormalizeAuthorizedUserSid("not-a-Windows-SID"));
        Assert.AreEqual(sid, PrivilegedInput.ReadAuthorizedUserSid(() => sid));
        Assert.IsNull(PrivilegedInput.ReadAuthorizedUserSid(() => null!));
        Assert.IsNull(PrivilegedInput.ReadAuthorizedUserSid(() => 42));
        Assert.IsNull(PrivilegedInput.ReadAuthorizedUserSid(() => "corrupted-SID"));
        Assert.IsNull(PrivilegedInput.ReadAuthorizedUserSid(() => throw new UnauthorizedAccessException()));
        Assert.IsNull(PrivilegedInput.ReadAuthorizedUserSid(() => throw new IOException()));
    }

    [TestMethod]
    public void FailedCleanupKeepsTheActualDownUntilItsReleaseSucceeds()
    {
        var track = typeof(PrivilegedInput).GetMethod("TrackHeldInput", Private)!;
        var release = typeof(PrivilegedInput).GetMethod("ReleaseHeldInput", Private)!;
        var isRelease = typeof(PrivilegedInput).GetMethod("IsOwnedRelease", Private)!;
        var held = new Dictionary<long, NativeMethods.INPUT64>();
        NativeMethods.INPUT64 key = default; key.type = 1; key.ki.wVk = 0x11; key.ki.wScan = 29;
        track.Invoke(null, [held, key]);
        NativeMethods.INPUT64 mouse = default; mouse.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTDOWN;
        track.Invoke(null, [held, mouse]);
        Assert.IsFalse((bool)isRelease.Invoke(null, [held, mouse])!);
        mouse.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE;
        Assert.IsFalse((bool)isRelease.Invoke(null, [held, mouse])!, "A failed move must not release a legitimate held modifier or button.");
        key.ki.dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP;
        Assert.IsTrue((bool)isRelease.Invoke(null, [held, key])!);
        key.ki.dwFlags |= (int)NativeMethods.KEYEVENTF.EXTENDEDKEY;
        Assert.IsFalse((bool)isRelease.Invoke(null, [held, key])!, "A different Control key must not trigger recovery of this DOWN.");
        Func<NativeMethods.INPUT64, uint> partiallyRejected = input => input.type == 1 ? 0u : 1u;
        Assert.IsFalse((bool)release.Invoke(null, [held, partiallyRejected])!);
        Assert.AreEqual(1, held.Count, "Successful releases can drain, but a desktop-rejected UP keeps its owner.");
        Func<NativeMethods.INPUT64, uint> recoveredDesktop = _ => 1u;
        Assert.IsTrue((bool)release.Invoke(null, [held, recoveredDesktop])!);
        Assert.AreEqual(0, held.Count);
    }

    [TestMethod]
    public void KnownExitedAgentDiscardsItsCachedEndpointEvenWithoutHeldKeys()
    {
        object? oldPipe = Field("inputPipe").GetValue(null), oldAgent = Field("inputAgent").GetValue(null), oldOwns = Field("brokerOwnsInput").GetValue(null);
        using var pipe = new NamedPipeClientStream(".", "mwb-stale-test-" + Guid.NewGuid().ToString("N"), PipeDirection.InOut);
        using var signaled = new EventWaitHandle(true, EventResetMode.ManualReset);
        using var agentHandle = new SafeProcessHandle(signaled.SafeWaitHandle.DangerousGetHandle(), false);
        try
        {
            Field("inputPipe").SetValue(null, pipe); Field("inputAgent").SetValue(null, agentHandle); Field("brokerOwnsInput").SetValue(null, false);
            typeof(PrivilegedInput).GetMethod("DiscardExitedAgent", Private)!.Invoke(null, null);
            Assert.IsNull(Field("inputPipe").GetValue(null), "The first request after a known agent exit must establish a fresh pipe.");
            Assert.IsNull(Field("inputAgent").GetValue(null));
            Assert.IsFalse(PrivilegedInput.HasHeldInput);
        }
        finally
        {
            Field("inputPipe").SetValue(null, oldPipe); Field("inputAgent").SetValue(null, oldAgent); Field("brokerOwnsInput").SetValue(null, oldOwns);
        }
    }
}
