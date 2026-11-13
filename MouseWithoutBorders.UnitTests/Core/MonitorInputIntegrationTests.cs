using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class MonitorInputIntegrationTests
{
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int failedRemotePacketId = 1900030;
    private readonly List<(FieldInfo Field, object? Owner, object? Value)> saved = new();
    private MouseWithoutBorders.Class.Settings originalSettings = null!, synthetic = null!;
    private SocketStuff originalTransport = null!;
    private ID originalDestination, originalNewDestination;
    private Point originalPosition;
    private string originalName = null!;
    private int originalPaintCount;
    private int originalSwitchCount;
    private System.Diagnostics.Process originalProcess = null!, ownedProcess = null!;
    private string folder = null!;

    [TestInitialize]
    public void Setup()
    {
        originalSettings = Setting.Values; originalTransport = Common.Sk;
        originalProcess = Common.CurrentProcess;
        Common.CurrentProcess = ownedProcess = System.Diagnostics.Process.GetCurrentProcess();
        originalDestination = MachineStuff.desMachineID; originalNewDestination = MachineStuff.newDesMachineID;
        originalPosition = Common.LastPos; originalPaintCount = Common.PaintCount;
        originalSwitchCount = MachineStuff.SwitchLocation.Count;
        originalName = Common.MachineName; Common.MachineName = "A";
        var matrix = typeof(MachineStuff).GetField("mcMatrix", StaticFields)!;
        saved.Add((matrix, null, matrix.GetValue(null))); matrix.SetValue(null, null);
        foreach (var type in new[] { typeof(Event), typeof(InputHook), typeof(DragDrop), typeof(Package), typeof(MonitorTopology), typeof(Clipboard) })
            foreach (var field in type.GetFields(StaticFields).Where(field => !field.IsInitOnly && !field.IsLiteral))
                saved.Add((field, null, field.GetValue(null)));
        var packet = typeof(Event).GetField("MousePackage", StaticFields)!.GetValue(null)!;
        foreach (var field in typeof(DATA).GetFields(InstanceFields)) saved.Add((field, packet, field.GetValue(packet)));
        ((DATA)packet).Src = ID.NONE;

        folder = Path.Combine(Path.GetTempPath(), "mwb-input-path-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "synthetic-input-key";
        document.Properties.MachineID.Value = 101; document.Properties.TransferFile = false;
        document.Properties.MachineMatrixString = ["A", "B", "", ""];
        string path = Path.Combine(folder, "prefs.json");
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        synthetic = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false); Setting.Values = synthetic;
        var state = typeof(MonitorTopology).GetField("state", StaticFields)!;
        state.SetValue(null, Activator.CreateInstance(state.FieldType, InstanceFields, null,
            [new Dictionary<uint, MonitorDescriptor[]>(), Array.Empty<MonitorOrigin>(), Array.Empty<uint>(), 0u, new MonitorLayout([])], null));
        MonitorTopology.AcceptPeer(101, [new("A1", 0, 0, 2560, 1440)]);
        MonitorTopology.AcceptPeer(202, [new("B1", 0, 0, 2560, 1440), new("B2", -8, -1440, 2560, 1440)]);
        MonitorTopology.ApplyOrigins([new(101, "A1", 0, 0), new(202, "B1", 2560, 0), new(202, "B2", 5120, 0)]);
        Common.Sk = null!; DragDrop.IsDragging = false; DragDrop.IsDropping = false;
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { synthetic?.SaveSettingsSynchronously(); }
        finally
        {
            Setting.Values = originalSettings; Common.Sk = originalTransport;
            Common.CurrentProcess = originalProcess; ownedProcess.Dispose();
            MachineStuff.desMachineID = originalDestination; MachineStuff.newDesMachineID = originalNewDestination;
            Common.LastPos = originalPosition; Common.PaintCount = originalPaintCount;
            MachineStuff.SwitchLocation.Count = originalSwitchCount;
            Common.MachineName = originalName;
            foreach (var value in saved) value.Field.SetValue(value.Owner, value.Value);
            Directory.Delete(folder, true);
        }
    }

    [DataTestMethod]
    [DataRow(100, 0, 99, 50, 0, 50)]
    [DataRow(-100, 0, 0, 50, 99, 50)]
    [DataRow(0, 100, 50, 99, 50, 0)]
    [DataRow(0, -100, 50, 0, 50, 99)]
    public void ActivationAtAnAlreadyClippedEdgeCrossesWithoutChangingOrdinaryEchoRouting(int worldX, int worldY, int x, int y, int arrivedX, int arrivedY)
    {
        var first = new MonitorPlacement(new(101, "A"), new(0, 0, 100, 100), Point.Empty);
        var next = new MonitorPlacement(new(202, "B"), new(0, 0, 100, 100), new(worldX, worldY));
        var layout = new MonitorLayout([first, next]);
        var edge = new Point(x, y);
        Assert.AreEqual(first.Key, layout.Route(first.Key, edge, edge).Monitor.Key, "Ordinary arrival echoes must retain their original meaning.");
        var activated = Event.ProbeActivationEdge(layout, first.Key, edge, _ => true);
        Assert.AreEqual(next.Key, activated.Monitor.Key);
        Assert.AreEqual(new Point(arrivedX, arrivedY), activated.NativePoint);
    }

    [TestMethod]
    public void ActivationCannotChooseAnAmbiguousCornerOrJumpAGap()
    {
        var first = new MonitorPlacement(new(101, "A"), new(0, 0, 100, 100), Point.Empty);
        var right = new MonitorPlacement(new(202, "B"), new(0, 0, 100, 100), new(100, 0));
        var below = new MonitorPlacement(new(303, "C"), new(0, 0, 100, 100), new(0, 100));
        var corner = new Point(99, 99);
        Assert.AreEqual(first.Key, Event.ProbeActivationEdge(new([first, right, below]), first.Key, corner, _ => true).Monitor.Key,
            "Zero native movement cannot tell which of two corner exits the user intended.");
        Assert.AreEqual(right.Key, Event.ProbeActivationEdge(new([first, right]), first.Key, corner, _ => true).Monitor.Key,
            "A corner with just one shared exit is unambiguous.");
        Assert.AreEqual(first.Key, Event.ProbeActivationEdge(new([first, right with { WorldOrigin = new(101, 0) }]), first.Key, new(99, 50), _ => true).Monitor.Key);
        Assert.AreEqual(first.Key, Event.ProbeActivationEdge(new([first, right]), first.Key, new(99, 50), _ => false).Monitor.Key);
    }

    [TestMethod]
    public void ActivationSurvivesParallelMovementButCannotBounceAfterASuccessfulCrossing()
    {
        var first = new MonitorPlacement(new(101, "A"), new(0, 0, 100, 100), Point.Empty);
        var next = new MonitorPlacement(new(202, "B"), new(0, 0, 100, 100), new(100, 0));
        var layout = new MonitorLayout([first, next]);
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)101;
        Event.SetMouseSwitchActivation(true);
        Assert.AreEqual(first.Key, Event.RouteMouseMove(layout, first.Key, new(99, 49), new(99, 50), _ => true).Monitor.Key);
        var crossed = Event.RouteMouseMove(layout, first.Key, new(99, 50), new(99, 50), _ => true);
        Assert.AreEqual(next.Key, crossed.Monitor.Key);
        Assert.AreEqual(next.Key, Event.RouteMouseMove(layout, next.Key, crossed.NativePoint, crossed.NativePoint, _ => true).Monitor.Key,
            "An arrival echo during the same activation hold must not cross back.");
        Event.SetMouseSwitchActivation(true);
        Event.SetMouseSwitchActivation(false);
        Assert.AreEqual(first.Key, Event.RouteMouseMove(layout, first.Key, new(99, 50), new(99, 50), _ => true).Monitor.Key,
            "Releasing the activation key before moving must cancel its pending edge probe.");
        Event.SetMouseSwitchActivation(true);
        Assert.AreEqual(next.Key, Event.RouteMouseMove(layout, first.Key, new(99, 50), new(99, 50), _ => true).Monitor.Key);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RealHeldKeyTransitionsArmAndCancelOnlyTheNextMouseGesture(bool multipleKeys)
    {
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        int[] originalHeld = held.DownKeys;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        var process = typeof(InputHook).GetMethod("ProcessKeyEx", InstanceFields)!;
        var pending = typeof(Event).GetField("activationPending", StaticFields)!;
        try
        {
            held.Clear();
            synthetic.EasyMouse = (int)EasyMouseOption.Custom;
            synthetic.EasyMouseActivationKeys = multipleKeys ? [(int)VK.LCONTROL, 0x14] : [0x14];
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)101;
            Event.SetMouseSwitchActivation(false);
            void Key(int code, bool up) => process.Invoke(hook, [code, up ? (int)WM.LLKHF.UP : 0,
                new KEYBDDATA { wVk = code, dwFlags = up ? (int)WM.LLKHF.UP : 0 }]);
            if (multipleKeys) { Key((int)VK.LCONTROL, false); Assert.IsFalse((bool)pending.GetValue(null)!); }
            Key(0x14, false); Assert.IsTrue((bool)pending.GetValue(null)!);
            Key(0x14, true); Assert.IsFalse((bool)pending.GetValue(null)!);
            Key(0x14, false); Assert.IsTrue((bool)pending.GetValue(null)!);
            var point = new Point(2559, 100); Common.LastPos = point; Event.ResetMonitorPointer(point);
            Event.MouseEvent(new() { X = point.X, Y = point.Y, dwFlags = WM.WM_LBUTTONDOWN }, 0, 0);
            Event.MouseEvent(new() { X = point.X, Y = point.Y, dwFlags = WM.WM_MOUSEWHEEL }, 0, 0);
            Assert.AreEqual((ID)101, Common.DesMachineID); Assert.IsTrue((bool)pending.GetValue(null)!);
        }
        finally { held.Clear(); foreach (int key in originalHeld) held.Update(key, true); }
    }

    [DataTestMethod]
    [DataRow(WM.WM_LBUTTONDOWN, false)]
    [DataRow(WM.WM_MOUSEWHEEL, false)]
    [DataRow(WM.WM_LBUTTONDOWN, true)]
    [DataRow(WM.WM_MOUSEWHEEL, true)]
    public void FirstRemoteButtonUsesTargetMonitorCoordinatesWithoutAPrecedingMove(int message, bool resolutionChanged)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var wire = new MemoryStream();
        using var tcp = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(tcp, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { tcp });
        Common.Sk = transport;
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
        Common.LastPos = new Point(800, 450);
        typeof(Event).GetField("activeMonitor", StaticFields)!.SetValue(null, new MonitorKey(202, "B2"));
        typeof(Event).GetField("monitorPoint", StaticFields)!.SetValue(null, resolutionChanged ? new Point(2500, 1300) : new Point(492, -720));
        if (resolutionChanged)
            MonitorTopology.AcceptPeer(202, [new("B1", 0, 0, 800, 600), new("B2", -8, -600, 800, 600)]);
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        hook.MouseEvent += Event.MouseEvent;
        int wheel = message == WM.WM_MOUSEWHEEL ? 120 : 0;
        Assert.AreEqual(1, InvokeMouse(hook, message, new Point(900, 500), wheel), "Remote input must be suppressed on the controller.");
        WaitForInput(transport);
        Assert.AreEqual((long)Package.PACKAGE_SIZE, wire.Length, "The first button must be one self-contained packet, without a preceding MOVE.");
        var delivered = new DATA(wire.ToArray());
        Assert.AreEqual((int)PackageType.Mouse, (int)delivered.Type & 0xff);
        Assert.AreEqual((ID)101, delivered.Src); Assert.AreEqual((ID)202, delivered.Des);
        Assert.AreEqual(message, delivered.Md.dwFlags); Assert.AreEqual(wheel, delivered.Md.WheelDelta);
        // Target desktop union: [-8,-1440]..[2560,1440], not the controller's raw 900,500.
        Assert.AreEqual(resolutionChanged ? 64805 : 12759, delivered.Md.X);
        Assert.AreEqual(resolutionChanged ? 32712 : 16383, delivered.Md.Y);
        Assert.AreEqual(new MonitorKey(202, "B2"), typeof(Event).GetField("activeMonitor", StaticFields)!.GetValue(null));
    }

    [TestMethod]
    public void WrongWindowsNeighbourReachesTheHookDelegateAndItsSuppressionIsHonoured()
    {
        MonitorTopology.AcceptPeer(101, [new("A1", 0, 0, 2560, 1440), new("A2", -8, -1440, 2560, 1440)]);
        MonitorTopology.ApplyOrigins([new(101, "A1", 0, 0), new(101, "A2", 5120, 0), new(202, "B1", 2560, 0), new(202, "B2", 7680, 0)]);
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)101;
        Common.LastPos = new Point(100, 1);
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        int calls = 0;
        // Do not invoke Event's native SetCursorPos branch; verify the real hook's delegate contract.
        hook.MouseEvent += (mouse, dx, dy) =>
        {
            calls++;
            Assert.AreEqual(new Point(100, -1), new Point(mouse.X, mouse.Y));
            Assert.AreEqual(0, dx); Assert.AreEqual(-2, dy);
            var layout = MonitorTopology.Layout;
            var routed = layout.Route(new(101, "A1"), Common.LastPos, new(mouse.X, mouse.Y));
            Assert.AreEqual(new MonitorKey(101, "A1"), routed.Monitor.Key);
            Assert.AreEqual(new Point(100, 0), routed.NativePoint);
            return true;
        };
        Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(100, -1), 0));
        Assert.AreEqual(1, calls);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ASeparatedMonitorOnTheSamePeerCannotBypassAnIntermediatePeerGate(bool activationRequired)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(2);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var middleSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        middleSocket.Connect(listener.LocalEndPoint!); using var middleAccepted = listener.Accept();
        using var wire = new MemoryStream(); using var middleWire = new MemoryStream();
        using var tcp = new TcpSk(false, socket, SocketStatus.Connected, "A") { MachineId = 202 };
        using var middle = new TcpSk(false, middleSocket, SocketStatus.Connected, "B") { MachineId = 303 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(tcp, wire);
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(middle, middleWire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport,
            activationRequired ? new List<TcpSk> { tcp, middle } : new List<TcpSk> { tcp });
        Common.Sk = transport;
        Setting.Values.EasyMouse = (int)(activationRequired ? EasyMouseOption.Disable : EasyMouseOption.Enable);
        MonitorTopology.AcceptPeer(202, [new("A1", 0, 0, 100, 100), new("A2", 200, 0, 100, 100)]);
        MonitorTopology.AcceptPeer(303, [new("B1", 0, 0, 100, 100)]);
        MonitorTopology.ApplyOrigins([new(202, "A1", 0, 0), new(303, "B1", 100, 0), new(202, "A2", 200, 0)]);
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
        Common.LastPos = new Point(800, 450);
        var first = new MonitorKey(202, "A1");
        var active = typeof(Event).GetField("activeMonitor", StaticFields)!;
        var point = typeof(Event).GetField("monitorPoint", StaticFields)!;
        active.SetValue(null, first); point.SetValue(null, new Point(90, 50));
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook); hook.MouseEvent += Event.MouseEvent;
        Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(959, 450), 0));
        Assert.AreEqual(first, active.GetValue(null), "A large MOVE must stop before an offline or inactive intermediate PC, even when the final monitor has the original peer.");
        Assert.AreEqual(new Point(99, 50), point.GetValue(null));
        WaitForInput(transport);
        var direct = new DATA(wire.ToArray());
        Assert.AreEqual(21626, direct.Md.X); // Native peer union [0,300).
        active.SetValue(null, first); point.SetValue(null, new Point(90, 50)); wire.SetLength(0);
        InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(820, 450), 0);
        InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(939, 450), 0);
        WaitForInput(transport);
        Assert.AreEqual(first, active.GetValue(null)); Assert.AreEqual(new Point(99, 50), point.GetValue(null));
        var split = new DATA(wire.ToArray()[^Package.PACKAGE_SIZE..]);
        Assert.AreEqual(direct.Md.X, split.Md.X); Assert.AreEqual(direct.Md.Y, split.Md.Y);
    }

    [TestMethod]
    public void HeldCapsLockCrossesBothWaysWithoutSendingAnAbsoluteMoveInRelativeMode()
    {
        using var game = new QueuedPeer(202);
        var transport = QueueTransport(game.Peer);
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        var previousKeys = held.DownKeys;
        var nativePosition = Point.Empty;
        bool canRestorePosition = NativeMethods.GetCursorPos(ref nativePosition);
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        hook.MouseEvent += Event.MouseEvent;
        hook.KeyboardEvent += _ => { };
        DATA ReadFrame(byte[] bytes, int offset)
        {
            var frame = new DATA(bytes[offset..(offset + Package.PACKAGE_SIZE)]);
            frame.Type = (PackageType)((int)frame.Type & 0xff);
            return frame;
        }
        try
        {
            held.Clear();
            synthetic.EasyMouse = (int)EasyMouseOption.Custom;
            synthetic.EasyMouseActivationKeys = [20];
            synthetic.MoveMouseRelatively = true;
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)101;
            var edge = new Point(2559, 100);
            Common.LastPos = edge;
            Event.ResetMonitorPointer(edge);

            InvokeKeyboard(hook, 20, false);
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEMOVE, edge, 0));
            Assert.AreEqual((ID)202, Common.DesMachineID);
            WaitForInput(transport);
            var arrival = game.Wire.ToArray();
            for (int offset = 0; offset < arrival.Length; offset += Package.PACKAGE_SIZE)
                Assert.AreNotEqual(PackageType.Mouse, ReadFrame(arrival, offset).Type,
                    "Relative arrival must not send a position or the previous PC's delta.");
            game.Wire.SetLength(0);

            var parked = Common.LastPos;
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(parked.X + 5, parked.Y), 0));
            InvokeKeyboard(hook, 20, true);
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(parked.X - 15, parked.Y), 0));
            Assert.AreEqual((ID)202, Common.DesMachineID, "Releasing Caps Lock must prevent a game movement from leaving the screen.");
            WaitForInput(transport);
            var movement = game.Wire.ToArray();
            Assert.IsTrue(movement.Length >= Package.PACKAGE_SIZE);
            int sentX = 0;
            for (int offset = 0; offset < movement.Length; offset += Package.PACKAGE_SIZE)
            {
                var move = ReadFrame(movement, offset);
                Assert.IsTrue(RelativeMouse.IsRelativeMove(move));
                sentX += RelativeMouse.Decode(move.Md.X);
                Assert.AreEqual(0, RelativeMouse.Decode(move.Md.Y));
            }
            Assert.AreEqual(-10, sentX, "Only the two physical deltas may reach the remote PC.");

            game.Wire.SetLength(0);
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_LBUTTONDOWN, parked, 0));
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_LBUTTONUP, parked, 0));
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEWHEEL, parked, -120));
            WaitForInput(transport);
            var buttons = game.Wire.ToArray();
            int[] messages = [WM.WM_LBUTTONDOWN, WM.WM_LBUTTONUP, WM.WM_MOUSEWHEEL];
            Assert.AreEqual(messages.Length * Package.PACKAGE_SIZE, buttons.Length);
            for (int i = 0; i < messages.Length; i++)
            {
                var button = ReadFrame(buttons, i * Package.PACKAGE_SIZE);
                Assert.AreEqual(PackageType.Mouse, button.Type);
                Assert.AreEqual(messages[i], button.Md.dwFlags);
                Assert.AreEqual(Event.MOVE_MOUSE_RELATIVE, button.Md.X);
                Assert.AreEqual(Event.MOVE_MOUSE_RELATIVE, button.Md.Y);
                Assert.AreEqual(messages[i] == WM.WM_MOUSEWHEEL ? -120 : 0, button.Md.WheelDelta);
            }

            InvokeKeyboard(hook, 20, false);
            Assert.AreEqual(1, InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(parked.X - 15, parked.Y), 0));
            Assert.AreEqual((ID)101, Common.DesMachineID, "Caps Lock permits a deliberate return in relative mode.");
            WaitForInput(transport);
            var exit = game.Wire.ToArray();
            Assert.AreEqual(PackageType.HideMouse, ReadFrame(exit, exit.Length - Package.PACKAGE_SIZE).Type);
        }
        finally
        {
            held.Clear();
            foreach (int key in previousKeys) held.Update(key, true);
            if (canRestorePosition) NativeMethods.SetCursorPos(nativePosition.X, nativePosition.Y);
        }
    }

    [TestMethod]
    public void RecordedCapsLockReconnectRunsOncePerPressAndRespectsTheMasterSwitch()
    {
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        var before = held.DownKeys; held.Clear();
        int originalReopen = InitAndCleanup.PleaseReopenSocket;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.KeyboardEvent += _ => { };
        try
        {
            synthetic.KeyboardShortcutsEnabled = true;
            synthetic.SetShortcutKeys(KeyboardAction.Reconnect, [(int)System.Windows.Forms.Keys.CapsLock]);
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)101;
            InitAndCleanup.PleaseReopenSocket = 0;
            Assert.AreEqual(1, InvokeKeyboard(hook, 20, false));
            Assert.AreEqual(InitAndCleanup.REOPEN_WHEN_HOTKEY, InitAndCleanup.PleaseReopenSocket);
            InitAndCleanup.PleaseReopenSocket = 0;
            Assert.AreEqual(1, InvokeKeyboard(hook, 20, false));
            Assert.AreEqual(0, InitAndCleanup.PleaseReopenSocket, "Auto-repeat must be consumed without executing again.");
            InvokeKeyboard(hook, 20, true);
            InvokeKeyboard(hook, 20, false);
            Assert.AreEqual(InitAndCleanup.REOPEN_WHEN_HOTKEY, InitAndCleanup.PleaseReopenSocket);
            InvokeKeyboard(hook, 20, true);
            synthetic.KeyboardShortcutsEnabled = false; InitAndCleanup.PleaseReopenSocket = 0;
            InvokeKeyboard(hook, 20, false);
            Assert.AreEqual(0, InitAndCleanup.PleaseReopenSocket);
            Assert.AreEqual(1, synthetic.GetShortcutKeys(KeyboardAction.Reconnect).Length);
        }
        finally { held.Clear(); foreach (int key in before) held.Update(key, true); InitAndCleanup.PleaseReopenSocket = originalReopen; }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnavailableRemoteDestinationCannotSwallowEitherLocalDevice(bool keyboard)
    {
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
        MachineStuff.SwitchLocation.Count = 5;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.MouseEvent += Event.MouseEvent;
        hook.KeyboardEvent += Event.KeybdEvent;
        int result = keyboard ? InvokeKeyboard(hook, 0x87, false)
            : InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(800, 450), 0);
        Assert.AreEqual(0, result, "A missing transport must let the physical event reach Windows immediately.");
        Assert.AreEqual((ID)101, Common.DesMachineID);
        Assert.AreEqual((ID)101, MachineStuff.NewDesMachineID);
        Assert.AreEqual(0, MachineStuff.SwitchLocation.Count, "A pending remote warp must not undo recovery.");
        if (keyboard) InvokeKeyboard(hook, 0x87, true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingMouseGeometryReturnsLocallyWithoutBlockingKeyboardOnlyConnections(bool keyboard)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var tcp = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 303 };
        using var wire = new MemoryStream();
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(tcp, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { tcp });
        Common.Sk = transport;
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)303;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.MouseEvent += Event.MouseEvent; hook.KeyboardEvent += Event.KeybdEvent;
        int result = keyboard ? InvokeKeyboard(hook, 0x87, false)
            : InvokeMouse(hook, WM.WM_LBUTTONDOWN, new Point(800, 450), 0);
        Assert.AreEqual(keyboard ? 1 : 0, result);
        Assert.AreEqual(keyboard ? (ID)303 : (ID)101, Common.DesMachineID);
        WaitForInput(transport);
        Assert.AreEqual(keyboard ? (long)Package.PACKAGE_SIZE : 0L, wire.Length,
            "Mouse routing needs geometry; a keyboard-only connection must remain usable.");
        if (keyboard) { InvokeKeyboard(hook, 0x87, true); WaitForInput(transport); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmergencyReturnIgnoresStaleKeysAndTheShortcutMasterSwitch(bool shortcutsEnabled)
    {
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        var before = held.DownKeys;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        int forwarded = 0; hook.KeyboardEvent += _ => forwarded++;
        try
        {
            held.Clear();
            synthetic.KeyboardShortcutsEnabled = shortcutsEnabled;
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)101;
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.LControlKey, false);
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.RMenu, false);
            held.Update(20, true); // Missed Caps Lock UP after a game transition.
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            MachineStuff.SwitchLocation.Count = 5;
            forwarded = 0;
            Assert.AreEqual(0, InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.Back, false, ownInjected: true));
            Assert.AreEqual((ID)202, Common.DesMachineID, "An injected peer key must not invoke the physical emergency escape.");
            Assert.AreEqual(1, InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.Back, false));
            Assert.AreEqual((ID)101, Common.DesMachineID, "The reserved escape must run before exact configurable-chord matching or network forwarding.");
            Assert.AreEqual((ID)101, MachineStuff.NewDesMachineID);
            Assert.AreEqual(0, MachineStuff.SwitchLocation.Count);
            Assert.AreEqual(0, forwarded, "The emergency key must never be sent to the old target.");
            Assert.AreEqual(0, held.DownKeys.Length, "Stale activation keys must not immediately send the pointer away again.");
            Assert.IsFalse(InputHook.EasyMouseKeyDown);
            Assert.AreEqual(1, InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.Back, false), "Auto-repeat must not delete local content.");
            Assert.AreEqual(1, InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.Back, true));
            Assert.AreEqual(0, forwarded);
            Assert.AreEqual(0, InvokeKeyboard(hook, 0x87, false));
            Assert.AreEqual(1, forwarded);
        }
        finally { held.Clear(); foreach (int key in before) held.Update(key, true); }
    }

    [DataTestMethod]
    [DataRow(WM.WM_LBUTTONDOWN, WM.WM_LBUTTONUP, 0)]
    [DataRow(WM.WM_RBUTTONDOWN, WM.WM_RBUTTONUP, 0)]
    [DataRow(WM.WM_MBUTTONDOWN, WM.WM_MBUTTONUP, 0)]
    [DataRow(WM.WM_XBUTTONDOWN, WM.WM_XBUTTONUP, 1)]
    [DataRow(WM.WM_XBUTTONDOWN, WM.WM_XBUTTONUP, 2)]
    public void EmergencyReturnsBeforeABlockedPeerAndReleasesOnlyForwardedHolds(int down, int up, int button)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var wire = new MemoryStream();
        using var tcp = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(tcp, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { tcp }); Common.Sk = transport;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.KeyboardEvent += Event.KeybdEvent; hook.MouseEvent += Event.MouseEvent;
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        var before = held.DownKeys; Task<int>? escape = null;
        try
        {
            held.Clear(); synthetic.KeyboardShortcutsEnabled = false;
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            InvokeKeyboard(hook, 'W', false);
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.LControlKey, false);
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.RMenu, false, extended: true);
            InvokeMouse(hook, down, new Point(800, 450), button);
            WaitForInput(transport);
            Assert.AreEqual(4L * Package.PACKAGE_SIZE, wire.Length);
            wire.SetLength(0); wire.Position = 0;
            lock (tcp)
            {
                escape = Task.Run(() => InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.Back, false));
                Assert.IsTrue(escape.Wait(TimeSpan.FromSeconds(2)), "Recovery must not wait for the peer's TCP frame lock.");
                Assert.AreEqual(1, escape.Result); Assert.AreEqual((ID)101, Common.DesMachineID);
                Assert.AreEqual(0, InvokeKeyboard(hook, 0x87, false), "Local keys must work while remote cleanup is blocked.");
                Assert.AreEqual(0L, wire.Length);
            }
            Assert.IsTrue(SpinWait.SpinUntil(() => { lock (tcp) return wire.Length >= 5L * Package.PACKAGE_SIZE; }, TimeSpan.FromSeconds(3)));
            DATA[] frames;
            lock (tcp) frames = wire.ToArray().Chunk(Package.PACKAGE_SIZE).Select(bytes => new DATA(bytes)).ToArray();
            foreach (var frame in frames) frame.Type = (PackageType)((uint)frame.Type & 0xff);
            var keys = frames.Where(frame => frame.Type == PackageType.Keyboard).ToArray();
            CollectionAssert.AreEquivalent(new[] { (int)'W', (int)System.Windows.Forms.Keys.LControlKey, (int)System.Windows.Forms.Keys.RMenu }, keys.Select(frame => frame.Kd.wVk).ToArray());
            Assert.IsTrue(keys.All(frame => (frame.Kd.dwFlags & (int)WM.LLKHF.UP) != 0));
            Assert.AreNotEqual(0, keys.Single(frame => frame.Kd.wVk == (int)System.Windows.Forms.Keys.RMenu).Kd.dwFlags & (int)WM.LLKHF.EXTENDED);
            var mouse = frames.Single(frame => frame.Type == PackageType.Mouse);
            Assert.AreEqual(up, mouse.Md.dwFlags); Assert.AreEqual(button, mouse.Md.WheelDelta);
            Assert.AreEqual(Event.MOVE_MOUSE_RELATIVE, mouse.Md.X, "A cleanup UP must not warp the remote pointer.");
            Assert.AreEqual(PackageType.HideMouse, frames.Last().Type);
        }
        finally
        {
            escape?.Wait(TimeSpan.FromSeconds(3));
            lock (tcp) { }
            held.Clear(); foreach (int key in before) held.Update(key, true);
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void QueuedEmergencyCleanupPrecedesResumedInputAndCannotReachARetiredOwner(bool all, bool retired)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var wire = new MemoryStream();
        using var tcp = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(tcp, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { tcp }); Common.Sk = transport;
        MachineStuff.desMachineID = (ID)101;
        lock (tcp)
        {
            using var scope = new InputHookDispatch(true);
            Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = (ID)202, Kd = new KEYBDDATA { wVk = 'W' } }, null, false);
            transport.ReleaseInput((ID)202);
            if (retired) Common.Sk = null!;
            else
            {
                MachineStuff.desMachineID = all ? ID.ALL : (ID)202;
                Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = all ? ID.ALL : (ID)202, Kd = new KEYBDDATA { wVk = 'X' } }, null, false);
            }
        }
        WaitForInput(transport);
        if (retired) Assert.AreEqual(0L, wire.Length);
        else
        {
            var keys = wire.ToArray().Chunk(32).Select(bytes => new DATA(bytes)).Where(frame => ((uint)frame.Type & 0xff) == (uint)PackageType.Keyboard).ToArray();
            CollectionAssert.AreEqual(new[] { (int)'W', 'W', 'X' }, keys.Select(frame => frame.Kd.wVk).ToArray());
            Assert.AreNotEqual(0, keys[1].Kd.dwFlags & (int)WM.LLKHF.UP, "Returning to the peer without W must still release its old W DOWN.");
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmergencyRetainsEachRecipientsHoldsWhenKeysRepeatAcrossTwoPeers(bool releaseOnSecondPeer)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(2);
        using var firstSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        firstSocket.Connect(listener.LocalEndPoint!); using var firstReceiver = listener.Accept();
        using var secondSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        secondSocket.Connect(listener.LocalEndPoint!); using var secondReceiver = listener.Accept();
        using var firstWire = new MemoryStream(); using var secondWire = new MemoryStream();
        using var first = new TcpSk(false, firstSocket, SocketStatus.Connected, "B") { MachineId = 202 };
        using var second = new TcpSk(false, secondSocket, SocketStatus.Connected, "C") { MachineId = 303 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(first, firstWire);
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(second, secondWire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { first, second }); Common.Sk = transport;
        MonitorTopology.AcceptPeer(303, [new("C1", 0, 0, 800, 600)]);
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.KeyboardEvent += Event.KeybdEvent; hook.MouseEvent += Event.MouseEvent;
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        var before = held.DownKeys;
        try
        {
            held.Clear(); synthetic.KeyboardShortcutsEnabled = false;
            foreach (ID target in new[] { (ID)202, (ID)303 })
            {
                MachineStuff.desMachineID = MachineStuff.newDesMachineID = target;
                InvokeKeyboard(hook, 'W', false);
                InvokeMouse(hook, WM.WM_RBUTTONDOWN, new Point(800, 450), 0);
            }
            if (releaseOnSecondPeer)
            {
                InvokeKeyboard(hook, 'W', true);
                InvokeMouse(hook, WM.WM_RBUTTONUP, new Point(800, 450), 0);
            }
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.LControlKey, false);
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.LMenu, false);
            WaitForInput(transport);
            firstWire.SetLength(0); firstWire.Position = 0; secondWire.SetLength(0); secondWire.Position = 0;
            InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.Back, false);
            Assert.IsTrue(SpinWait.SpinUntil(() => { lock (first) lock (second)
                return firstWire.Length == 2L * Package.PACKAGE_SIZE && secondWire.Length == (releaseOnSecondPeer ? 3L : 5L) * Package.PACKAGE_SIZE; }, TimeSpan.FromSeconds(3)));
            foreach (var (tcp, wire, needsRelease) in new[] { (first, firstWire, true), (second, secondWire, !releaseOnSecondPeer) })
            {
                DATA[] frames; lock (tcp) frames = wire.ToArray().Chunk(Package.PACKAGE_SIZE).Select(bytes => new DATA(bytes)).ToArray();
                Assert.AreEqual(needsRelease ? 1 : 0, frames.Count(frame => ((uint)frame.Type & 0xff) == (uint)PackageType.Keyboard && frame.Kd.wVk == 'W'));
                Assert.AreEqual(needsRelease ? 1 : 0, frames.Count(frame => ((uint)frame.Type & 0xff) == (uint)PackageType.Mouse && frame.Md.dwFlags == WM.WM_RBUTTONUP));
            }
        }
        finally { lock (first) lock (second) { } held.Clear(); foreach (int key in before) held.Update(key, true); }
    }

    [TestMethod]
    public void RecordingReceivesPlusInjectedKeysAndPassesReleasesWithoutForwarding()
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        int forwarded = 0; hook.KeyboardEvent += _ => forwarded++;
        var recorded = new List<(int, bool)>();
        typeof(InputHook).GetField("recordingKeys", StaticFields)!.SetValue(null, true);
        InputHook.KeyRecorder = (key, down) => recorded.Add((key, down));
        Assert.AreEqual(1, InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.LWin, false, ownInjected: true));
        Assert.AreEqual(1, InvokeKeyboard(hook, 'R', false, ownInjected: true));
        Assert.AreEqual(0, InvokeKeyboard(hook, 'R', true, ownInjected: true));
        Assert.AreEqual(0, InvokeKeyboard(hook, (int)System.Windows.Forms.Keys.LWin, true, ownInjected: true));
        Assert.AreEqual(4, recorded.Count); Assert.AreEqual(0, forwarded);
    }

    [TestMethod]
    public void NextPcSkipsOfflineSlotsWrapsAndCanLeaveAllPcMode()
    {
        var poolField = typeof(MachineStuff).GetField("_machinePool", StaticFields)!;
        var pool = poolField.GetValue(null);
        try
        {
            poolField.SetValue(null, new MachinePool()); synthetic.MachineMatrixString = "A,B,C,";
            foreach (var (name, id) in new[] { ("A", 101), ("B", 202), ("C", 303) })
            { MachineStuff.MachinePool.LearnMachine(name); MachineStuff.MachinePool.TryUpdateMachineID(name, (ID)id, false); }
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
            using var peer = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer }); Common.Sk = transport;
            MachineStuff.desMachineID = (ID)101; Assert.AreEqual("B", MachineStuff.NextMachineName());
            MachineStuff.desMachineID = (ID)202; Assert.AreEqual("A", MachineStuff.NextMachineName());
            MachineStuff.desMachineID = ID.ALL; Assert.AreEqual("A", MachineStuff.NextMachineName());
            peer.Status = SocketStatus.ForceClosed; MachineStuff.desMachineID = (ID)101;
            Assert.IsNull(MachineStuff.NextMachineName(), "With only this PC online there is no next destination.");
        }
        finally { poolField.SetValue(null, pool); }
    }

    [TestMethod]
    public void PhysicalReturnCrossesToLocalAfterCrdMovesAndReleasesItsOwnActivationKey()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var wire = new MemoryStream(); using var peer = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(peer, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer }); Common.Sk = transport;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.KeyboardEvent += Event.KeybdEvent; hook.MouseEvent += Event.MouseEvent;
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        int[] oldHeld = held.DownKeys; var cursor = new Point(); NativeMethods.GetCursorPos(ref cursor);
        IntPtr key = Marshal.AllocHGlobal(24), mouse = Marshal.AllocHGlobal(32);
        try
        {
            held.Clear(); synthetic.KeyboardShortcutsEnabled = false;
            synthetic.EasyMouse = (int)EasyMouseOption.Custom; synthetic.EasyMouseActivationKeys = [0x14];
            synthetic.DisableEasyMouseWhenForegroundWindowIsFullscreen = false;
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = Common.MachineID;
            InvokeKeyboard(hook, 0x14, false);
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            typeof(Event).GetField("activeMonitor", StaticFields)!.SetValue(null, new MonitorKey(202, "B1"));
            typeof(Event).GetField("monitorPoint", StaticFields)!.SetValue(null, new Point(1, 100));
            Common.LastPos = new(100, 100);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(key, i, 0);
            Marshal.WriteInt32(key, 0, 0x14); Marshal.WriteInt32(key, 8, 0x90); Marshal.WriteIntPtr(key, 16, new(0x43524430));
            typeof(InputHook).GetMethod("KeyboardHookProc", InstanceFields)!.Invoke(hook, [0, WM.WM_KEYUP, key]);
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(mouse, i, 0);
            Marshal.WriteInt32(mouse, 0, 2000); Marshal.WriteInt32(mouse, 4, 800);
            Marshal.WriteInt32(mouse, 12, 1); Marshal.WriteIntPtr(mouse, 24, new(0x43524430));
            var callback = typeof(InputHook).GetMethod("MouseHookProc", InstanceFields)!;
            callback.Invoke(hook, [0, WM.WM_MOUSEMOVE, mouse]);
            Assert.AreEqual((ID)202, Common.DesMachineID); Assert.IsTrue(InputHook.EasyMouseKeyDown);
            Assert.AreEqual(0, wire.Length, "Remote-session movement cannot feed back to its MWBP peer.");
            Marshal.WriteInt32(mouse, 0, 1998); Marshal.WriteInt32(mouse, 12, 0);
            callback.Invoke(hook, [0, WM.WM_MOUSEMOVE, mouse]);
            Assert.AreEqual(Common.MachineID, Common.DesMachineID, "The physical -2px gesture must cross the real B1→A1 boundary.");
            Assert.AreEqual(new Point(2559, 100), Common.LastPos);
            Assert.IsTrue(SpinWait.SpinUntil(() => wire.Length >= 32, TimeSpan.FromSeconds(2)), "The old peer cleanup must finish before fixture disposal.");
        }
        finally
        {
            held.Clear(); foreach (int value in oldHeld) held.Update(value, true);
            NativeMethods.SetCursorPos(cursor.X, cursor.Y); Marshal.FreeHGlobal(key); Marshal.FreeHGlobal(mouse);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ARemoteFrameLockCannotStrandTheKeyboardHook(bool inventoryLock)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var peer = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        using var wire = new MemoryStream();
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(peer, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer }); Common.Sk = transport;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        synthetic.KeyboardShortcutsEnabled = false; hook.KeyboardEvent += Event.KeybdEvent;
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (inventoryLock ? transport.TcpSocketsLock : peer) { entered.Set(); release.Wait(); } });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
        Task<int>? callback = null;
        try
        {
            callback = Task.Run(() => InvokeKeyboard(hook, 0x87, true));
            Assert.IsTrue(await Task.WhenAny(callback, Task.Delay(400)) == callback,
                "A stalled peer frame or inventory owner must not hold the real hook entrypoint until Windows removes it.");
            Assert.AreEqual(1, await callback, "Temporary contention must enqueue the remote input without disconnecting a healthy peer.");
            Assert.AreEqual((ID)202, Common.DesMachineID);
            Assert.AreSame(socket, peer.BackingSocket);
            Assert.IsFalse(socket.SafeHandle.IsClosed);
        }
        finally { release.Set(); await holder; if (callback != null) await callback; }
        Assert.IsTrue(SpinWait.SpinUntil(() => { lock (peer) return wire.Length == 32; }, TimeSpan.FromSeconds(3)),
            "The original UP must reach the peer after contention ends.");
    }

    [TestMethod]
    public async Task BroadcastQueuesEachLogicalPeerOnceWithoutWaitingForDuplicateFrameOwners()
    {
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        var peers = new List<TcpSk>(); var sockets = new List<Socket>(); var holders = new List<Task>();
        using var release = new ManualResetEventSlim(); using var entered = new CountdownEvent(8);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(8);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(listener.LocalEndPoint!); sockets.Add(socket); sockets.Add(listener.Accept());
                var peer = new TcpSk(i % 2 == 0, socket, SocketStatus.Connected, "B") { MachineId = 202 }; peers.Add(peer);
                typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(peer, new MemoryStream());
                holders.Add(Task.Factory.StartNew(() => { lock (peer) { entered.Signal(); release.Wait(); } }, TaskCreationOptions.LongRunning));
            }
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, peers); Common.Sk = transport;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (new InputHookDispatch(true))
            {
                Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = ID.ALL, Kd = new() { wVk = 0x87 } }, null, false);
                Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = ID.ALL, Kd = new() { wVk = 0x87, dwFlags = (int)WM.LLKHF.UP } }, null, false);
            }
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(100), "Broadcast enqueue must not wait for any peer frame owner.");
            Assert.AreEqual(1, peers.Count(peer => peer.InputWriter != null), "Duplicate connections must share one selected recipient.");
        }
        finally { release.Set(); await Task.WhenAll(holders); WaitForInput(transport); foreach (var socket in sockets) socket.Dispose(); foreach (var peer in peers) peer.Dispose(); }
    }

    private sealed class FailedWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic peer write failure.");
    }

    [TestMethod]
    public void ABlockedWriterCannotDelayEitherHookOrAnotherPeerAndPacketsKeepTheirOriginalValues()
    {
        using var slow = new QueuedPeer(202); using var fast = new QueuedPeer(303);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var blockedWire = new GatedWriteStream(entered, release);
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(slow.Peer, blockedWire);
        var transport = QueueTransport(slow.Peer, fast.Peer);
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.KeyboardEvent += Event.KeybdEvent; synthetic.KeyboardShortcutsEnabled = false;
        try
        {
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            Assert.AreEqual(1, InvokeKeyboard(hook, 'W', false));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.AreEqual(1, InvokeKeyboard(hook, 'W', true));
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)303;
            Assert.AreEqual(1, InvokeKeyboard(hook, 'X', false));
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(100), "The hook waited on the blocked network writer.");
            Assert.IsTrue(SpinWait.SpinUntil(() => fast.Peer.InputWriter?.IsIdle == true, TimeSpan.FromSeconds(2)));
            var delivered = new DATA(fast.Wire.ToArray());
            Assert.AreEqual((ID)303, delivered.Des); Assert.AreEqual((int)'X', delivered.Kd.wVk);
            Assert.AreEqual(0L, blockedWire.Length);
            Assert.AreSame(slow.Socket, slow.Peer.BackingSocket, "A busy healthy connection must remain connected.");
        }
        finally { release.Set(); WaitForInput(transport); }
        var slowFrames = blockedWire.ToArray().Chunk(32).Select(bytes => new DATA(bytes)).ToArray();
        CollectionAssert.AreEqual(new[] { (int)'W', 'W' }, slowFrames.Select(frame => frame.Kd.wVk).ToArray());
        Assert.IsTrue(slowFrames.All(frame => frame.Des == (ID)202));
        Assert.AreEqual(0, slowFrames[0].Kd.dwFlags & (int)WM.LLKHF.UP);
        Assert.AreNotEqual(0, slowFrames[1].Kd.dwFlags & (int)WM.LLKHF.UP);
    }

    [TestMethod]
    public void QueuedMovesCoalesceOnlyAtTheTailAndPreserveEveryInputBarrier()
    {
        using var peer = new QueuedPeer(202);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var wire = new GatedWriteStream(entered, release);
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(peer.Peer, wire);
        var transport = QueueTransport(peer.Peer);
        void SendMouse(int message, int x) => Common.SkSend(new DATA { Type = PackageType.Mouse, Des = (ID)202,
            Md = new() { dwFlags = message, X = x, Y = x, WheelDelta = message == WM.WM_MOUSEWHEEL ? 120 : 0 } }, null, false);
        try
        {
            using var dispatch = new InputHookDispatch(true);
            SendMouse(WM.WM_MOUSEMOVE, 1);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            SendMouse(WM.WM_MOUSEMOVE, 2); SendMouse(WM.WM_MOUSEMOVE, 3);
            SendMouse(WM.WM_LBUTTONDOWN, 3);
            SendMouse(WM.WM_MOUSEMOVE, 4); SendMouse(WM.WM_MOUSEMOVE, 5);
            Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = (ID)202, Kd = new() { wVk = 'W' } }, null, false);
            SendMouse(WM.WM_MOUSEMOVE, 6); SendMouse(WM.WM_MOUSEWHEEL, 6);
            SendMouse(WM.WM_MOUSEMOVE, -Event.MOVE_MOUSE_RELATIVE - 1);
            SendMouse(WM.WM_MOUSEMOVE, -Event.MOVE_MOUSE_RELATIVE - 2);
            SendMouse(WM.WM_LBUTTONUP, 6);
        }
        finally { release.Set(); WaitForInput(transport); }
        var frames = wire.ToArray().Chunk(32).Select(bytes => new DATA(bytes)).ToArray();
        // Positions are replaced by the latest one; unsent distances add up (1 + 2) so none is lost.
        CollectionAssert.AreEqual(new[] { 1, 3, 3, 5, 6, 6, -Event.MOVE_MOUSE_RELATIVE - 3, 6 },
            frames.Where(frame => ((uint)frame.Type & 0xff) == (uint)PackageType.Mouse).Select(frame => frame.Md.X).ToArray());
        Assert.AreEqual((uint)PackageType.Keyboard, (uint)frames[4].Type & 0xff);
        Assert.AreEqual(WM.WM_LBUTTONDOWN, frames[2].Md.dwFlags);
        Assert.AreEqual(WM.WM_MOUSEWHEEL, frames[6].Md.dwFlags);
        Assert.AreEqual(WM.WM_LBUTTONUP, frames[^1].Md.dwFlags);
    }

    [TestMethod]
    public void QueueOverflowPassesTheFirstFailedPhysicalClickLocallyAndDoesNotRetireAnotherPeer()
    {
        using var slow = new QueuedPeer(202); using var fast = new QueuedPeer(303);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var wire = new GatedWriteStream(entered, release);
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(slow.Peer, wire);
        var transport = QueueTransport(slow.Peer, fast.Peer);
        try
        {
            using (new InputHookDispatch(true))
            {
                Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = (ID)202, Kd = new() { wVk = 'W' } }, null, false);
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
                for (int i = 0; i < PeerInputWriter.Capacity; i++)
                    Common.SkSend(new DATA { Type = PackageType.Keyboard, Des = (ID)202, Kd = new() { wVk = 'W' } }, null, false);
            }
            var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
            hook.MouseEvent += Event.MouseEvent;
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            Assert.AreEqual(0, InvokeMouse(hook, WM.WM_LBUTTONDOWN, new Point(800, 450), 0), "A rejected DOWN must reach the local hook chain.");
            Assert.AreEqual(Common.MachineID, Common.DesMachineID);
            Assert.IsTrue(SpinWait.SpinUntil(() => slow.Socket.SafeHandle.IsClosed, TimeSpan.FromSeconds(2)));
            Assert.IsFalse(fast.Socket.SafeHandle.IsClosed);
        }
        finally { release.Set(); WaitForInput(transport); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OrdinaryPeerHandoffOrdersOwnedUpsBeforeHideAndPreservesAHealthyNewPeer(bool resumed)
    {
        using var slow = new QueuedPeer(202); using var fast = new QueuedPeer(303);
        var transport = QueueTransport(slow.Peer, fast.Peer);
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.KeyboardEvent += Event.KeybdEvent; synthetic.KeyboardShortcutsEnabled = false;
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
        lock (slow.Peer)
        {
            InvokeKeyboard(hook, 'W', false);
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)303;
            Common.SendPackage((ID)202, PackageType.HideMouse); // Also ordered without a hook scope.
            if (resumed)
            {
                MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
                InvokeKeyboard(hook, 'X', false);
            }
        }
        WaitForInput(transport);
        var frames = slow.Wire.ToArray().Chunk(32).Select(bytes => new DATA(bytes)).ToArray();
        Assert.AreEqual((int)'W', frames[0].Kd.wVk);
        Assert.AreEqual((int)'W', frames[1].Kd.wVk);
        Assert.AreNotEqual(0, frames[1].Kd.dwFlags & (int)WM.LLKHF.UP, "Even immediate reentry without W must release the old hold.");
        if (resumed) Assert.AreEqual((int)'X', frames[^1].Kd.wVk);
        else Assert.AreEqual((uint)PackageType.HideMouse, (uint)frames[^1].Type & 0xff);

        MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)303;
        slow.Peer.InputWriter!.Stop();
        Common.SendPackage((ID)202, PackageType.HideMouse);
        Assert.AreEqual((ID)303, Common.DesMachineID, "A stale cleanup failure must not pull the healthy active peer back to local.");
    }

    [TestMethod]
    public void APostedWriterFailureRechecksDestinationAndTransportOnTheInputThread()
    {
        using var peer = new QueuedPeer(202);
        var transport = QueueTransport(peer.Peer);
        var originalForm = Common.InputCallbackForm;
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using var form = new FrmInputCallback(); _ = form.Handle;
                Common.InputCallbackForm = form; peer.Peer.Status = SocketStatus.SendError;
                MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
                form.NotifyInputPeerFailure(transport, 202);
                MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)303;
                System.Windows.Forms.Application.DoEvents();
                Assert.AreEqual((ID)303, Common.DesMachineID);
                MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
                form.NotifyInputPeerFailure(transport, 202);
                Common.Sk = null!;
                System.Windows.Forms.Application.DoEvents();
                Assert.AreEqual((ID)202, Common.DesMachineID);
                Common.Sk = transport; form.NotifyInputPeerFailure(transport, 202);
                System.Windows.Forms.Application.DoEvents();
                Assert.AreEqual(Common.MachineID, Common.DesMachineID);
            }
            catch (Exception error) { failure = error; }
            finally { Common.InputCallbackForm = originalForm; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5))); Assert.IsNull(failure);
    }

    private static SocketStuff QueueTransport(params TcpSk[] peers)
    {
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, peers.ToList());
        Common.Sk = transport;
        return transport;
    }

    private sealed class QueuedPeer : IDisposable
    {
        internal Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly Socket accepted;
        internal MemoryStream Wire { get; } = new();
        internal TcpSk Peer { get; }
        internal QueuedPeer(uint id)
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            Socket.Connect(listener.LocalEndPoint!); accepted = listener.Accept();
            Peer = new TcpSk(false, Socket, SocketStatus.Connected, id == 202 ? "B" : "C") { MachineId = id };
            typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(Peer, Wire);
        }
        public void Dispose() { Peer.Dispose(); Wire.Dispose(); Socket.Dispose(); accepted.Dispose(); }
    }

    private sealed class GatedWriteStream(ManualResetEventSlim entered, ManualResetEventSlim release) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new IOException("The test did not release its gated writer.");
            base.Write(buffer, offset, count);
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task AFailedRemoteUpReleasesAppliedHoldsAndDiscardedFramesCannotTakeOwnership(bool siblingAlive, bool receiverRetirement)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var peer = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        using var other = new TcpSk(true, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        using var wire = new FailedWriteStream();
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(peer, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer }); Common.Sk = transport;
        using var sibling = new QueuedPeer(202);
        if (siblingAlive)
        {
            transport.TcpSockets.Add(sibling.Peer); transport.PublishSocketSnapshot();
        }
        var packet = new DATA { Type = PackageType.Keyboard, Src = (ID)202, Des = Common.MachineID,
            Id = Interlocked.Increment(ref failedRemotePacketId), Kd = new() { wVk = 0x87 } };
        var ledger = (System.Collections.IDictionary)typeof(InputSimulation).GetField("remoteHolds", StaticFields)!.GetValue(null)!;
        object? original = ledger[202u];
        ledger.Remove(202u);
        try
        {
            Receiver.ProcessPackage(packet, peer);
            Assert.IsTrue((NativeMethods.GetAsyncKeyState((IntPtr)0x87) & 0x8000) != 0, "A real native DOWN must first be applied.");
            var state = ledger[202u]!; var owner = state.GetType().GetField("Owner", InstanceFields)!;
            Receiver.ProcessPackage(packet, other); // Same processed ID, another connection.
            Assert.AreSame(peer, owner.GetValue(state), "Duplicate input must not advance ownership.");
            Receiver.ProcessPackage(new DATA { Type = PackageType.Keyboard, Src = (ID)202, Des = (ID)303,
                Id = Interlocked.Increment(ref failedRemotePacketId), Kd = new() { wVk = 0x87 } }, other);
            Assert.AreSame(peer, owner.GetValue(state), "Input for another destination must not advance ownership.");
            var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
            synthetic.KeyboardShortcutsEnabled = false; hook.KeyboardEvent += Event.KeybdEvent;
            MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
            if (receiverRetirement)
                typeof(SocketStuff).GetMethod("UpdateTcpSockets", InstanceFields)!.Invoke(transport, [peer, SocketStatus.Error]);
            else InvokeKeyboard(hook, 0x87, true);
            for (int i = 0; i < 60 && (NativeMethods.GetAsyncKeyState((IntPtr)0x87) & 0x8000) != 0; i++) await Task.Delay(25);
            Assert.IsFalse((NativeMethods.GetAsyncKeyState((IntPtr)0x87) & 0x8000) != 0,
                "The actual failed transport path must release its previously applied remote DOWN.");
        }
        finally
        {
            WaitForInput(transport);
            InputSimulation.SendKey(new() { wVk = 0x87, dwFlags = (int)WM.LLKHF.UP });
            ledger.Remove(202u); if (original != null) ledger[202u] = original;
        }
    }

    private static void WaitForInput(SocketStuff transport) => Assert.IsTrue(SpinWait.SpinUntil(
        () => transport.SocketSnapshot.All(peer => peer.InputWriter == null || peer.InputWriter.IsIdle), TimeSpan.FromSeconds(4)), "The input writers did not drain.");

    private static int InvokeKeyboard(InputHook hook, int key, bool up, bool ownInjected = false, bool extended = false, bool foreignInjected = false)
    {
        IntPtr payload = Marshal.AllocHGlobal(24);
        try
        {
            for (int offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(payload, offset, 0);
            Marshal.WriteInt32(payload, 0, key); Marshal.WriteInt32(payload, 8, (up ? 0x80 : 0) | (ownInjected || foreignInjected ? 0x10 : 0) | (extended ? 1 : 0));
            if (ownInjected) Marshal.WriteIntPtr(payload, 16, InputSimulation.InjectedInputTag);
            else if (foreignInjected) Marshal.WriteIntPtr(payload, 16, new IntPtr(0x1234));
            return (int)typeof(InputHook).GetMethod("KeyboardHookProc", InstanceFields)!.Invoke(hook, [0, up ? WM.WM_KEYUP : WM.WM_KEYDOWN, payload])!;
        }
        finally { Marshal.FreeHGlobal(payload); }
    }

    [DataTestMethod]
    [DataRow(0u, true)]
    [DataRow(1u, true)]
    [DataRow(0u, false)]
    [DataRow(1u, false)]
    public async Task LostNativeHookRecoversAfterObservedPhysicalActivityWithoutReplug(uint kind, bool automatic)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldHook = Common.Hook; var oldForm = Common.InputCallbackForm;
        var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", StaticFields)!.GetValue(null)!;
        int[] oldHeld = held.DownKeys;
        synthetic.KeyboardShortcutsEnabled = false; synthetic.EasyMouse = (int)EasyMouseOption.Disable;
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = Common.MachineID;
        var thread = new System.Threading.Thread(() =>
        {
            Exception? failure = null;
            using (var form = new FrmInputCallback())
            {
            _ = form.Handle;
            InputHook? hook = null;
            bool chordInjectionStarted = false;
            form.BeginInvoke((Action)(async () =>
            {
                try
                {
                    hook = new InputHook(); Common.Hook = hook; Common.InputCallbackForm = form;
                    Assert.IsTrue(InputHook.ObserveDevices(form.Handle));
                    if (!automatic && !form.RegisterRecoveryHotkey())
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 1409) Assert.Inconclusive("The running installed MWBP or another app already owns this Windows chord; do not steal it or interrupt the user's session.");
                        Assert.Fail($"Windows emergency hotkey registration failed: {error}.");
                    }
                    int calls = 0;
                    hook.MouseEvent += (_, _, _) => { if (kind == 0) calls++; return false; };
                    hook.KeyboardEvent += _ => { if (kind == 1) calls++; };
                    NativeMethods.INPUT64 input = default;
                    if (kind == 0) { input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE; input.mi.dwExtraInfo = new IntPtr(0x1234); }
                    else { input.type = 1; input.ki.wVk = 0x87; input.ki.dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP; input.ki.dwExtraInfo = new IntPtr(0x1234); }
                    async Task Send()
                    {
                        Assert.AreEqual(kind == 0 ? 2u : 1u, await Task.Run(() =>
                        {
                            if (kind != 0) return NativeMethods.SendInput64(1, [input], Marshal.SizeOf<NativeMethods.INPUT64>());
                            // Windows may elide a zero MOVE. A single atomic opposing pair
                            // exercises real mouse capture without leaving the pointer moved.
                            var position = new Point(); NativeMethods.GetCursorPos(ref position);
                            int direction = position.X < System.Windows.Forms.SystemInformation.VirtualScreen.Right - 1 ? 1 : -1;
                            var first = input; first.mi.dx = direction;
                            var second = input; second.mi.dx = -direction;
                            return NativeMethods.SendInput64(2, [first, second], Marshal.SizeOf<NativeMethods.INPUT64>());
                        }));
                        await Task.Delay(50); // Drain asynchronously dispatched Windows hook messages.
                    }
                    await Send(); Assert.IsTrue(calls > 0, "The initial native hook must observe its real Windows callback.");
                    var handle = typeof(InputHook).GetField(kind == 0 ? "hMouseHook" : "hKeyboardHook", InstanceFields)!;
                    Assert.AreNotEqual(0, NativeMethods.UnhookWindowsHookEx((int)handle.GetValue(hook)!));
                    if (automatic)
                    {
                        var other = typeof(InputHook).GetField(kind == 0 ? "hKeyboardHook" : "hMouseHook", InstanceFields)!;
                        Assert.AreNotEqual(0, NativeMethods.UnhookWindowsHookEx((int)other.GetValue(hook)!));
                    }
                    int before = calls;
                    await Send(); Assert.AreEqual(before, calls, "Windows input succeeds while the removed capture hook silently receives nothing.");
                    typeof(InputHook).GetField(kind == 0 ? "mouseHookSeenAt" : "keyboardHookSeenAt", InstanceFields)!.SetValue(hook, Environment.TickCount64 - 2000);
                    hook.ObservePhysicalActivity(kind, Environment.TickCount64 - 500);
                    if (automatic)
                    {
                        typeof(InputHook).GetField(kind == 0 ? "keyboardHookSeenAt" : "mouseHookSeenAt", InstanceFields)!.SetValue(hook, Environment.TickCount64 - 2000);
                        hook.ObservePhysicalActivity(kind == 0 ? 1u : 0u, Environment.TickCount64 - 500);
                    }
                    Assert.IsTrue(hook.HasMissedPhysicalInput(Environment.TickCount64));
                    if (automatic) form.CheckInputHealth();
                    else
                    {
                        // Exercise Windows' real registered shortcut with the LL hook removed.
                        var chord = new[] { 0xa2, 0xa4, 8 }.Select(key => new NativeMethods.INPUT64
                        { type = 1, ki = new() { wVk = (short)key, dwExtraInfo = new IntPtr(0x1234) } }).ToArray();
                        chordInjectionStarted = true;
                        Assert.AreEqual(3u, await Task.Run(() => NativeMethods.SendInput64(3, chord, Marshal.SizeOf<NativeMethods.INPUT64>())));
                    }
                    for (int i = 0; i < 60 && hook.HasMissedPhysicalInput(Environment.TickCount64); i++)
                    {
                        if (automatic) form.CheckInputHealth(); // The production timer also retries after delivery grace.
                        await Task.Delay(50);
                    }
                    Assert.IsFalse(hook.HasMissedPhysicalInput(Environment.TickCount64));
                    if (!automatic)
                    {
                        Assert.IsTrue((bool)typeof(InputHook).GetField("emergencyBackspaceDown", InstanceFields)!.GetValue(hook)!,
                            "Windows hotkey recovery must latch Backspace through modifier release.");
                        foreach (int key in new[] { 0xa2, 0xa4 }) InvokeKeyboard(hook, key, true);
                        Assert.AreEqual(1, InvokeKeyboard(hook, 8, false), "Modifier release before Backspace must not turn repeat into deletion.");
                        Assert.AreEqual(1, InvokeKeyboard(hook, 8, true));
                    }
                    await Send(); Assert.IsTrue(calls > before, "The existing queued health entrypoint must reinstall capture without a USB operation or network reconnect.");
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    if (chordInjectionStarted)
                    {
                        var releases = new[] { 8, 0xa4, 0xa2 }.Select(key => new NativeMethods.INPUT64
                        { type = 1, ki = new() { wVk = (short)key, dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP, dwExtraInfo = InputSimulation.InjectedInputTag } }).ToArray();
                        NativeMethods.SendInput64(3, releases, Marshal.SizeOf<NativeMethods.INPUT64>());
                    }
                    InputHook.ObserveDevices(IntPtr.Zero);
                    hook?.Stop(); if (hook != null) GC.SuppressFinalize(hook);
                    held.Clear(); foreach (int key in oldHeld) held.Update(key, true);
                    Common.Hook = oldHook; Common.InputCallbackForm = oldForm;
                    System.Windows.Forms.Application.ExitThread();
                }
            }));
            System.Windows.Forms.Application.Run();
            }
            if (failure == null) done.SetResult(); else done.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public void CaptureHealthRequiresMissedActivityOfTheSameDeviceAndAllowsDeliveryGrace()
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        Assert.IsFalse(hook.HasMissedPhysicalInput(10000), "Idle is not evidence of failed capture and must not generate test input.");
        typeof(InputHook).GetField("mouseHookSeenAt", InstanceFields)!.SetValue(hook, 10000L);
        hook.ObservePhysicalActivity(0, 10020);
        Assert.IsFalse(hook.HasMissedPhysicalInput(11000), "Mouse activity cannot diagnose a keyboard hook that has not been used.");
        hook.ObservePhysicalActivity(1, 12000);
        Assert.IsFalse(hook.HasMissedPhysicalInput(12100), "Raw input delivery is not ordered with callback processing.");
        Assert.IsTrue(hook.HasMissedPhysicalInput(12300));
        Assert.IsFalse(hook.RecoverMissedInput(), "One deliberately suppressed device must not cause automatic remapper reordering.");
        typeof(InputHook).GetField("keyboardHookSeenAt", InstanceFields)!.SetValue(hook, 12010L);
        Assert.IsFalse(hook.HasMissedPhysicalInput(12300), "A delayed matching callback must cancel repair.");
    }

    [TestMethod]
    public void CaptureHealthDoesNotCombineUnrelatedOldAndNewSuppressedInput()
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        hook.ObservePhysicalActivity(1, 1000);
        hook.ObservePhysicalActivity(0, 100000);
        Assert.IsTrue(hook.HasMissedPhysicalInput(100300), "The current mouse activity is still available for diagnostics.");
        Assert.IsFalse(hook.RecoverMissedInput(), "A long-idle keyboard is not evidence of a current two-device capture outage.");
        Assert.IsFalse(hook.HasMissedPhysicalInput(105000), "Unmatched old activity must expire rather than survive indefinitely.");
    }
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ValidInjectedCallbacksProveCaptureIsAliveWithoutChangingRealInputAccounting(bool own)
    {
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
        var real = Event.RealInputEventCount;
        MachineStuff.desMachineID = MachineStuff.newDesMachineID = Common.MachineID;
        InvokeKeyboard(hook, 0x87, true, ownInjected: own, foreignInjected: !own);
        hook.ObservePhysicalActivity(1, Environment.TickCount64);
        Assert.IsFalse(hook.HasMissedPhysicalInput(Environment.TickCount64 + 1000), "A valid replacement or tagged callback proves this hook is still installed.");
        Assert.AreEqual(real + (own ? 0u : 1u), Event.RealInputEventCount);
    }

    [TestMethod]
    public void HotkeyReturnToThisPcConsumesPendingClipboardWithoutMouseCrossing()
    {
        var field = typeof(MachineStuff).GetField("_machinePool", StaticFields)!;
        var original = field.GetValue(null);
        try
        {
            field.SetValue(null, new MachinePool());
            MachineStuff.MachinePool.LearnMachine("A");
            MachineStuff.MachinePool.TryUpdateMachineID("A", (ID)101, false);
            MachineStuff.desMachineID = (ID)202;
            Clipboard.LastMachineWithClipboardData = null!; // No worker/network is started by GetRemoteClipboard.
            Clipboard.clipboardCopiedTime = Common.GetTick() - 60000;
            Clipboard.HasSwitchedMachineSinceLastCopy = false;
            MachineStuff.SwitchToMachine("A");
            Assert.AreEqual((ID)101, Common.DesMachineID);
            Assert.AreEqual(0L, Clipboard.clipboardCopiedTime, "A hotkey return must consume the pending copy just like an edge crossing.");
            Assert.IsTrue(Clipboard.HasSwitchedMachineSinceLastCopy);
        }
        finally { field.SetValue(null, original); }
    }

    [TestMethod]
    public void PendingFileAdvertisementSurvivesWaitingBeforeSwitching()
    {
        MachineStuff.desMachineID = (ID)101;
        Clipboard.LastMachineWithClipboardData = null!;
        Clipboard.clipboardCopiedTime = Common.GetTick() - 60000;
        using var peer = new TcpSk(false, null!, SocketStatus.Connected, "B") { MachineId = 202 };
        Receiver.ProcessPackage(new DATA { Type = PackageType.MachineSwitched, Src = (ID)202, Des = (ID)101, Id = 180001 }, peer);
        Assert.AreEqual(0L, Clipboard.clipboardCopiedTime, "A still-pending copy must not expire after thirty seconds.");
    }

    [TestMethod]
    public void ClipboardAdvertisementArrivingAfterSwitchStillRequestsTheFile()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var wire = new MemoryStream();
        using var peer = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", InstanceFields)!.SetValue(peer, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer });
        Common.Sk = transport; MachineStuff.desMachineID = (ID)202;
        Common.SendClipboardBeat();
        WaitForInput(transport);
        Assert.AreEqual((long)Package.PACKAGE_SIZE_EX + Package.PACKAGE_SIZE, wire.Length, "The late copy must advertise before requesting the active PC to fetch it.");
        var bytes = wire.ToArray();
        var first = new DATA(bytes[..Package.PACKAGE_SIZE_EX]);
        var second = new DATA(bytes[Package.PACKAGE_SIZE_EX..]);
        Assert.AreEqual((int)PackageType.Clipboard, (int)first.Type & 0xff);
        Assert.AreEqual((int)PackageType.MachineSwitched, (int)second.Type & 0xff);
        Assert.AreEqual((ID)202, second.Des);
    }

    [TestMethod]
    public void ReceivedMouseHookDoesNotWaitForPreferencesPersistence()
    {
        int local = synthetic.MachineId; // Initialize the existing identity cache.
        MachineStuff.desMachineID = (ID)local;
        var hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook);
        var gate = (System.Threading.Lock)typeof(MouseWithoutBorders.Class.Settings)
            .GetField("_loadingSettingsLock", InstanceFields)!.GetValue(synthetic)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        Task? callback = null;
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            callback = Task.Run(() => InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(50, 50), 0, ownInjected: true));
            Assert.IsTrue(callback.Wait(TimeSpan.FromSeconds(1)), "A received MOVE's own hook must not wait for the preferences writer.");
        }
        finally { release.Set(); writer.GetAwaiter().GetResult(); callback?.GetAwaiter().GetResult(); }
        synthetic.MachineId = 303;
        Assert.AreEqual(303, synthetic.MachineId, "An identity collision update must replace the cached ID.");
    }

    private static int InvokeMouse(InputHook hook, int message, Point point, int wheel, bool ownInjected = false)
    {
        IntPtr payload = Marshal.AllocHGlobal(32);
        try
        {
            for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(payload, offset, 0);
            Marshal.WriteInt32(payload, 0, point.X); Marshal.WriteInt32(payload, 4, point.Y);
            Marshal.WriteInt32(payload, 8, wheel << 16);
            if (ownInjected)
            {
                Marshal.WriteInt32(payload, 12, 1);
                Marshal.WriteIntPtr(payload, IntPtr.Size == 8 ? 24 : 20, InputSimulation.InjectedInputTag);
            }
            return (int)typeof(InputHook).GetMethod("MouseHookProc", InstanceFields)!.Invoke(hook, [0, message, payload])!;
        }
        finally { Marshal.FreeHGlobal(payload); }
    }
}
