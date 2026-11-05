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
    private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private static FieldInfo Field(string name) => typeof(PrivilegedInput).GetField(name, Private)!;
    private static uint Exchange(NativeMethods.INPUT64 input) => (uint)typeof(PrivilegedInput).GetMethod("ExchangeInput", Private)!.Invoke(null, [input])!;

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
            byte[] bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()], reply = new byte[4];
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
            byte[] bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()], reply = new byte[4];
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
