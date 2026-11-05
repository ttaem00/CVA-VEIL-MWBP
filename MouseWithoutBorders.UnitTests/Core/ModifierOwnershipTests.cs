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
public sealed class ModifierOwnershipTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly List<(FieldInfo Field, object? Owner, object? Value)> saved = new();
    private MouseWithoutBorders.Class.Settings original = null!, synthetic = null!;
    private SocketStuff originalTransport = null!;
    private ID oldDestination, oldNewDestination;
    private int oldPaintCount;
    private InputHook hook = null!;
    private Socket listener = null!, socket = null!, accepted = null!;
    private TcpSk tcp = null!;
    private MemoryStream wire = null!;
    private HashSet<int> physicalHeld = null!;
    private int[] oldHeld = null!;
    private string folder = null!;

    [TestInitialize]
    public void Setup()
    {
        original = Setting.Values; originalTransport = Common.Sk;
        oldDestination = MachineStuff.desMachineID; oldNewDestination = MachineStuff.newDesMachineID;
        oldPaintCount = Common.PaintCount;
        foreach (var type in new[] { typeof(InputHook), typeof(Event), typeof(Package) })
            foreach (var field in type.GetFields(Static).Where(field => !field.IsInitOnly && !field.IsLiteral))
                saved.Add((field, null, field.GetValue(null)));
        var packet = (DATA)typeof(Event).GetField("KeybdPackage", Static)!.GetValue(null)!;
        foreach (var field in typeof(DATA).GetFields(Instance)) saved.Add((field, packet, field.GetValue(packet)));
        packet.Src = ID.NONE;
        var held = typeof(InputHook).GetField("easyMouseKeys", Static)!.GetValue(null)!;
        physicalHeld = (HashSet<int>)typeof(HeldKeyChord).GetField("held", Instance)!.GetValue(held)!;
        oldHeld = physicalHeld.ToArray(); physicalHeld.Clear();

        folder = Path.Combine(Path.GetTempPath(), "mwb-modifiers-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "synthetic-modifier-key";
        document.Properties.MachineID.Value = 101; document.Properties.KeyboardShortcutsEnabled = false;
        string path = Path.Combine(folder, "prefs.json");
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        synthetic = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false); Setting.Values = synthetic;

        listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); accepted = listener.Accept();
        wire = new MemoryStream(); tcp = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", Instance)!.SetValue(tcp, wire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", Instance)!.SetValue(transport, new List<TcpSk> { tcp }); Common.Sk = transport;
        hook = (InputHook)RuntimeHelpers.GetUninitializedObject(typeof(InputHook));
        GC.SuppressFinalize(hook); // This fixture never installs hooks; Stop must not clear a later fixture's held keys.
        hook.KeyboardEvent += Event.KeybdEvent;
        Destination(101);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { synthetic?.SaveSettingsSynchronously(); }
        finally
        {
            tcp?.Dispose(); accepted?.Dispose(); socket?.Dispose(); listener?.Dispose(); wire?.Dispose();
            Setting.Values = original; Common.Sk = originalTransport;
            MachineStuff.desMachineID = oldDestination; MachineStuff.newDesMachineID = oldNewDestination;
            Common.PaintCount = oldPaintCount;
            foreach (var value in saved) value.Field.SetValue(value.Owner, value.Value);
            physicalHeld.Clear(); physicalHeld.UnionWith(oldHeld);
            Directory.Delete(folder, true);
        }
    }

    [DataTestMethod]
    [DataRow((int)VK.LSHIFT)] [DataRow((int)VK.RSHIFT)]
    [DataRow((int)VK.LCONTROL)] [DataRow((int)VK.RCONTROL)]
    [DataRow((int)VK.LMENU)] [DataRow((int)VK.RMENU)]
    [DataRow((int)VK.LWIN)] [DataRow((int)VK.RWIN)]
    public void ALocallyDeliveredDownGetsItsRealUpAfterHandoffAndANewLocalDownSurvives(int key)
    {
        Assert.AreEqual(0, Invoke(key, false)); Assert.AreNotEqual((byte)0, LocalModifiers);
        Destination(202);
        Assert.AreEqual(0, Invoke(key, true), "A remote-routed real UP must also close its earlier local DOWN.");
        Assert.AreEqual((byte)0, LocalModifiers); Assert.AreEqual((long)Package.PACKAGE_SIZE, wire.Length);
        var packet = new DATA(wire.ToArray());
        Assert.AreEqual((ID)202, packet.Des); Assert.AreEqual(key, packet.Kd.wVk);
        Assert.AreNotEqual(0, packet.Kd.dwFlags & (int)WM.LLKHF.UP);
        Destination(101);
        Assert.AreEqual(0, Invoke(key, false)); Assert.AreNotEqual((byte)0, LocalModifiers,
            "A new local DOWN has no asynchronous arrival cleanup left to erase it.");
        Assert.AreEqual(0, Invoke(key, true)); Assert.AreEqual((byte)0, LocalModifiers);
    }

    [DataTestMethod]
    [DataRow((int)VK.LMENU)] [DataRow((int)VK.RMENU)] [DataRow((int)VK.LWIN)] [DataRow((int)VK.RWIN)]
    public void RemoteOnlySystemModifiersDoNotLeakAnUnmatchedUpLocally(int key)
    {
        Destination(202);
        Assert.AreEqual(1, Invoke(key, false)); Assert.AreEqual((byte)0, LocalModifiers);
        Assert.AreEqual(1, Invoke(key, true)); Assert.AreEqual((byte)0, LocalModifiers);
        Assert.AreEqual(2L * Package.PACKAGE_SIZE, wire.Length);
    }

    [DataTestMethod]
    [DataRow((int)VK.SHIFT, (int)VK.LSHIFT)]
    [DataRow((int)VK.CONTROL, (int)VK.LCONTROL)]
    [DataRow((int)VK.MENU, (int)VK.LMENU)]
    public void GenericModifiersPairWithTheirLeftSide(int generic, int left)
    {
        Assert.AreEqual(0, Invoke(generic, false)); Destination(202);
        Assert.AreEqual(0, Invoke(left, true)); Assert.AreEqual((byte)0, LocalModifiers);
    }

    [DataTestMethod]
    [DataRow((int)VK.LSHIFT)] [DataRow((int)VK.RSHIFT)]
    [DataRow((int)VK.LCONTROL)] [DataRow((int)VK.RCONTROL)]
    [DataRow((int)VK.LMENU)] [DataRow((int)VK.RMENU)]
    [DataRow((int)VK.LWIN)] [DataRow((int)VK.RWIN)]
    public void AStartupNativeHoldSeedIsClosedByTheFirstRemoteUp(int key)
    {
        // Simulate Start's outside-callback OS baseline; do not read or inject live keyboard state.
        byte bit = (byte)typeof(InputHook).GetMethod("ModifierBit", Static)!.Invoke(null, [key])!;
        typeof(InputHook).GetField("localModifiers", Instance)!.SetValue(hook, bit); Destination(202);
        Assert.AreEqual(0, Invoke(key, true)); Assert.AreEqual((byte)0, LocalModifiers);
        Assert.AreEqual((long)Package.PACKAGE_SIZE, wire.Length);
    }

    [DataTestMethod]
    [DataRow((int)VK.LCONTROL)] [DataRow((int)VK.LWIN)]
    public void OwnInjectedModifiersTrackLocalDeliveryWithoutNetworkReplay(int key)
    {
        Destination(202);
        Assert.AreEqual(0, Invoke(key, false, true)); Assert.AreNotEqual((byte)0, LocalModifiers);
        Assert.AreEqual(0, Invoke(key, true, true)); Assert.AreEqual((byte)0, LocalModifiers);
        Assert.AreEqual(0L, wire.Length);
    }

    private byte LocalModifiers => (byte)typeof(InputHook).GetField("localModifiers", Instance)!.GetValue(hook)!;

    [TestMethod]
    public async Task ChangingMultipleModeDoesNotWaitForTheReceiverInputLock()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var receiver = Task.Run(() =>
        {
            Common.DoSomethingInTheInputSimulationThread(() => { entered.Set(); release.Wait(); });
        });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        Task? switching = null;
        try
        {
            switching = Task.Run(() => MachineStuff.SwitchToMultipleMode(true, false));
            await switching.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(ID.ALL, Common.DesMachineID, "A hotkey must not wait for a receiver whose SendInput is waiting for that hook callback.");
        }
        finally
        {
            release.Set(); await receiver;
            if (switching != null) await switching;
        }
    }

    [TestMethod]
    public void ReceiverDoesNotReinterpretTheLegacyLockChordAfterTheSenderRemapsIt()
    {
        // The production legacy matcher is removed before running this regression;
        // never run a RED version that would call the real Windows lock API.
        synthetic.KeyboardShortcutsEnabled = true;
        synthetic.HotKeyLockMachine = new HotkeySettings(false, true, true, false, 'L');
        synthetic.SetShortcutKeys(KeyboardAction.LockComputers, [(int)System.Windows.Forms.Keys.CapsLock]);
        var flags = new[] { "winDown", "ctrlDown", "altDown" }.Select(name => typeof(InputSimulation).GetField(name, Static)!).ToArray();
        object?[] previous = flags.Select(field => field.GetValue(null)).ToArray();
        var process = typeof(InputSimulation).GetMethod("InputProcessKeyEx", Static)!;
        try
        {
            InputSimulation.ResetSystemKeyFlags();
            foreach (int key in new[] { (int)VK.LCONTROL, (int)VK.LMENU, (int)'L' })
            {
                object[] arguments = [key, 0, false];
                process.Invoke(null, arguments);
                Assert.IsFalse((bool)arguments[2], "The receiving endpoint must deliver ordinary Ctrl+Alt+L, not execute its old shortcut assignment.");
            }
            CollectionAssert.AreEqual(new[] { (int)System.Windows.Forms.Keys.CapsLock }, synthetic.GetShortcutKeys(KeyboardAction.LockComputers));
        }
        finally
        {
            for (int i = 0; i < flags.Length; i++) flags[i].SetValue(null, previous[i]);
        }
    }

    private static void Destination(uint peer) => MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)peer;
    private int Invoke(int key, bool up, bool own = false)
    {
        IntPtr payload = Marshal.AllocHGlobal(24);
        try
        {
            for (int offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(payload, offset, 0);
            Marshal.WriteInt32(payload, 0, key);
            Marshal.WriteInt32(payload, 8, (up ? (int)WM.LLKHF.UP : 0) | (own ? 0x10 : 0));
            Marshal.WriteIntPtr(payload, 16, own ? InputSimulation.InjectedInputTag : IntPtr.Zero);
            return (int)typeof(InputHook).GetMethod("KeyboardHookProc", Instance)!
                .Invoke(hook, [0, up ? WM.WM_KEYUP : WM.WM_KEYDOWN, payload])!;
        }
        finally { Marshal.FreeHGlobal(payload); }
    }
}
