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
    private readonly List<(FieldInfo Field, object? Owner, object? Value)> saved = new();
    private MouseWithoutBorders.Class.Settings originalSettings = null!, synthetic = null!;
    private SocketStuff originalTransport = null!;
    private ID originalDestination, originalNewDestination;
    private Point originalPosition;
    private string originalName = null!;
    private int originalPaintCount;
    private string folder = null!;

    [TestInitialize]
    public void Setup()
    {
        originalSettings = Setting.Values; originalTransport = Common.Sk;
        originalDestination = MachineStuff.desMachineID; originalNewDestination = MachineStuff.newDesMachineID;
        originalPosition = Common.LastPos; originalPaintCount = Common.PaintCount;
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
            MachineStuff.desMachineID = originalDestination; MachineStuff.newDesMachineID = originalNewDestination;
            Common.LastPos = originalPosition; Common.PaintCount = originalPaintCount;
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
        var direct = new DATA(wire.ToArray());
        Assert.AreEqual(21626, direct.Md.X); // Native peer union [0,300).
        active.SetValue(null, first); point.SetValue(null, new Point(90, 50)); wire.SetLength(0);
        InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(820, 450), 0);
        InvokeMouse(hook, WM.WM_MOUSEMOVE, new Point(939, 450), 0);
        Assert.AreEqual(first, active.GetValue(null)); Assert.AreEqual(new Point(99, 50), point.GetValue(null));
        var split = new DATA(wire.ToArray()[^Package.PACKAGE_SIZE..]);
        Assert.AreEqual(direct.Md.X, split.Md.X); Assert.AreEqual(direct.Md.Y, split.Md.Y);
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

    private static int InvokeKeyboard(InputHook hook, int key, bool up, bool ownInjected = false)
    {
        IntPtr payload = Marshal.AllocHGlobal(24);
        try
        {
            for (int offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(payload, offset, 0);
            Marshal.WriteInt32(payload, 0, key); Marshal.WriteInt32(payload, 8, (up ? 0x80 : 0) | (ownInjected ? 0x10 : 0));
            if (ownInjected) Marshal.WriteIntPtr(payload, 16, InputSimulation.InjectedInputTag);
            return (int)typeof(InputHook).GetMethod("KeyboardHookProc", InstanceFields)!.Invoke(hook, [0, up ? WM.WM_KEYUP : WM.WM_KEYDOWN, payload])!;
        }
        finally { Marshal.FreeHGlobal(payload); }
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
