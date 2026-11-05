using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Exceptions;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class InputTransportIsolationTests
{
    [DataTestMethod]
    [DataRow("input")]
    [DataRow("probe")]
    [DataRow("settings")]
    [DataRow("switch")]
    public void ABlockedPeerWriteDoesNotBlockInputToAnotherPeer(string operation)
    {
        var oldSettings = Setting.Values; var oldTransport = Common.Sk; string oldName = Common.MachineName;
        int oldId = Package.PackageID; var oldSent = Package.PackageSent;
        var matrix = typeof(MachineStuff).GetField("mcMatrix", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldMatrix = matrix.GetValue(null);
        var pool = typeof(MachineStuff).GetField("_machinePool", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldPool = pool.GetValue(null);
        var oldDestination = MachineStuff.desMachineID; string oldDestinationName = MachineStuff.DesMachineName;
        var oldNext = MachineStuff.newDesMachineID; var oldLocation = MachineStuff.SwitchLocation;
        long oldJump = MachineStuff.lastJump; int oldSwitchCount = Common.SwitchCount;
        bool oldMouseDown = DragDrop.MouseDown, oldDragging = DragDrop.IsDragging, oldDropping = DragDrop.IsDropping;
        var oldDragMachine = DragDrop.DragMachine; bool oldSwitched = Clipboard.HasSwitchedMachineSinceLastCopy;
        bool oldSwitchSignal = operation == "switch" && Common.EvSwitch.WaitOne(0);
        string folder = Path.Combine(Path.GetTempPath(), "mwb-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        MouseWithoutBorders.Class.Settings? settings = null;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Task? blocked = null, input = null;
        bool independentCompleted = false;
        try
        {
            var document = new MouseWithoutBordersSettings();
            document.Properties.SecurityKey.Value = "synthetic-transport-test-key";
            document.Properties.MachineID.Value = 101;
            document.Properties.MachineMatrixString = ["A", "B", "C", ""];
            string path = Path.Combine(folder, "settings.json");
            PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
            settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
            Setting.Values = settings; Common.MachineName = "A"; matrix.SetValue(null, null);
            if (operation == "switch")
            {
                var machines = new MachinePool(); pool.SetValue(null, machines);
                foreach (var (name, id) in new[] { ("A", 101), ("B", 202), ("C", 303) })
                { machines.LearnMachine(name); machines.TryUpdateMachineID(name, (ID)id, true); }
                Common.DesMachineID = (ID)202; MachineStuff.SwitchLocation = new MouseLocation();
                DragDrop.MouseDown = DragDrop.IsDragging = DragDrop.IsDropping = false; DragDrop.DragMachine = ID.NONE;
            }
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(2);
            using var slowSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            slowSocket.Connect(listener.LocalEndPoint!); using var slowReceiver = listener.Accept();
            using var fastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            fastSocket.Connect(listener.LocalEndPoint!); using var fastReceiver = listener.Accept();
            using var slowWire = new GatedNetworkStream(slowSocket, entered, release);
            using var fastWire = new NetworkStream(fastSocket, false);
            using var slow = new TcpSk(true, slowSocket, SocketStatus.Connected, "B") { MachineId = 202 };
            using var fast = new TcpSk(true, fastSocket, SocketStatus.Connected, "C") { MachineId = 303 };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(slow, slowWire);
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(fast, fastWire);
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", flags)!.SetValue(transport, new List<TcpSk> { slow, fast });
            Common.Sk = transport;
            try
            {
                blocked = Task.Run(() =>
                {
                    if (operation == "probe") Assert.IsTrue(Common.AtLeastOneSocketEstablished());
                    else if (operation == "settings") MachineStuff.SendMachineMatrix();
                    else if (operation == "switch")
                    {
                        Assert.IsTrue(Event.PrepareToSwitchToMachine((ID)303, new System.Drawing.Point(Event.XY_BY_PIXEL + 100, Event.XY_BY_PIXEL + 100)));
                        Common.SkSend(MousePacket(303), null, false);
                    }
                    else Common.SkSend(MousePacket(202), null, false);
                });
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)), "The real transport did not reach the gated peer write.");
                input = operation == "switch" ? blocked : Task.Run(() => Common.SkSend(MousePacket(303), null, false));
                independentCompleted = input.Wait(TimeSpan.FromSeconds(2));
                if (independentCompleted)
                {
                    fastReceiver.ReceiveTimeout = 2000;
                    byte[] frame = new byte[32];
                    using var read = new NetworkStream(fastReceiver, false);
                    bool sawMouse = false;
                    for (int i = 0; i < (operation == "switch" ? 2 : 1); i++)
                    {
                        read.ReadExactly(frame);
                        Assert.AreEqual(303u, BitConverter.ToUInt32(frame, 12));
                        if (frame[0] == (byte)PackageType.Mouse) sawMouse = true;
                        else Assert.AreEqual((byte)PackageType.MachineSwitched, frame[0]);
                    }
                    Assert.IsTrue(sawMouse, "The actual switch entrypoint must reach its new peer's MOVE before the previous peer finishes hiding.");
                }
            }
            finally
            {
                release.Set();
                Assert.IsTrue(blocked == null || blocked.Wait(TimeSpan.FromSeconds(5)), "The blocked transport did not finish after release.");
                Assert.IsTrue(input == null || input.Wait(TimeSpan.FromSeconds(5)), "Input did not finish after release.");
                lock (slow) { } // The asynchronous old-peer hide must finish before its test socket is disposed.
            }
        }
        finally
        {
            settings?.SaveSettingsSynchronously();
            Setting.Values = oldSettings; Common.Sk = oldTransport; Common.MachineName = oldName;
            Package.PackageID = oldId; Package.PackageSent = oldSent; matrix.SetValue(null, oldMatrix);
            pool.SetValue(null, oldPool); MachineStuff.desMachineID = oldDestination; MachineStuff.DesMachineName = oldDestinationName;
            MachineStuff.newDesMachineID = oldNext; MachineStuff.SwitchLocation = oldLocation;
            MachineStuff.lastJump = oldJump; Common.SwitchCount = oldSwitchCount;
            DragDrop.MouseDown = oldMouseDown; DragDrop.IsDragging = oldDragging; DragDrop.IsDropping = oldDropping;
            DragDrop.DragMachine = oldDragMachine; Clipboard.HasSwitchedMachineSinceLastCopy = oldSwitched;
            if (operation == "switch") { Common.EvSwitch.Reset(); if (oldSwitchSignal) Common.EvSwitch.Set(); }
            Directory.Delete(folder, true);
        }
        Assert.IsTrue(independentCompleted, "Input to C waited for unrelated peer B's network write.");
    }

    private static DATA MousePacket(uint peer) => new()
    {
        Type = PackageType.Mouse, Src = (ID)101, Des = (ID)peer,
        Md = new MOUSEDATA { X = 1000, Y = 2000, dwFlags = WM.WM_MOUSEMOVE },
    };

    [DataTestMethod]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    public void ADelayedHideIsCheckedForEachRecipientInsideItsFrameOwner(bool broadcast, bool currentRecipient, bool allActive)
    {
        var oldDestination = MachineStuff.desMachineID; var oldSent = Package.PackageSent;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var receiver = listener.Accept();
        using var wire = new MemoryStream();
        uint peer = currentRecipient ? 202u : 303u;
        using var tcp = new TcpSk(true, socket, SocketStatus.Connected, "B") { MachineId = peer };
        typeof(TcpSk).GetField("encryptedStream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tcp, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        Exception? failure = null;
        var writer = new System.Threading.Thread(() =>
        {
            try { transport.TcpSend(tcp, new DATA { Type = PackageType.HideMouse, Src = (ID)101, Des = broadcast ? ID.ALL : (ID)peer }); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        try
        {
            MachineStuff.desMachineID = (ID)101;
            lock (tcp)
            {
                writer.Start();
                Assert.IsTrue(SpinWait.SpinUntil(() => (writer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)),
                    "The delayed hide did not reach the actual TCP frame lock.");
                MachineStuff.desMachineID = allActive ? ID.ALL : (ID)202;
            }
            Assert.IsTrue(writer.Join(TimeSpan.FromSeconds(3)));
            Assert.IsNull(failure);
            Assert.AreEqual(currentRecipient || allActive ? 0L : 32L, wire.Length,
                "A late direct or ALL hide must skip only the recipient that is active when its frame can actually be written.");
        }
        finally
        {
            if ((writer.ThreadState & System.Threading.ThreadState.Unstarted) == 0) writer.Join(TimeSpan.FromSeconds(3));
            MachineStuff.desMachineID = oldDestination; Package.PackageSent = oldSent;
        }
    }

    [TestMethod]
    public void ASharedSettingsSenderWaitingOnItsPeerCannotCaptureANewOwnersGroup()
    {
        var oldTransport = Common.Sk;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var tcp = new TcpSk(true, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        using var wire = new MemoryStream();
        typeof(TcpSk).GetField("encryptedStream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tcp, wire);
        var retired = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        retired.TcpSocketsLock = new object();
        var replacement = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        using var started = new ManualResetEventSlim();
        Task? pending = null;
        try
        {
            Common.Sk = retired;
            lock (tcp)
            {
                pending = Task.Run(() => { started.Set(); retired.SendSharedSettings(tcp); });
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(3)));
                Common.Sk = replacement;
            }
            Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(0L, wire.Length, "A retired transport must not capture or send the replacement group's preferences.");
        }
        finally
        {
            pending?.Wait(TimeSpan.FromSeconds(3));
            Common.Sk = oldTransport;
        }
    }

    private sealed class GatedNetworkStream(Socket socket, ManualResetEventSlim entered, ManualResetEventSlim release)
        : NetworkStream(socket, false)
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new IOException("The test did not release the gated write.");
            base.Write(buffer, offset, count);
        }
    }

    [DataTestMethod]
    [DataRow("null")]
    [DataRow("closed")]
    [DataRow("disposed-stream")]
    public void ASelectedConnectionThatClosesUsesTheExistingExpectedFailure(string closedState)
    {
        var oldTransport = Common.Sk; string oldName = Common.MachineName; var oldSent = Package.PackageSent;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var receiver = listener.Accept();
        using var tcp = new TcpSk(true, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", flags)!.SetValue(transport, new List<TcpSk> { tcp });
        if (closedState == "null") tcp.BackingSocket = null;
        else if (closedState == "closed") socket.Close();
        else
        {
            var wire = new NetworkStream(socket, false);
            wire.Dispose();
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(tcp, wire);
        }
        try
        {
            Common.Sk = transport; Common.MachineName = "A";
            Assert.ThrowsException<ExpectedSocketException>(() => transport.TcpSend(tcp, MousePacket(202)));
            Assert.IsFalse(Common.AtLeastOneSocketEstablished());
        }
        finally { Common.Sk = oldTransport; Common.MachineName = oldName; Package.PackageSent = oldSent; }
    }
}
