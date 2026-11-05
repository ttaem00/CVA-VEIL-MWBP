using System.Text.Json;
using System.Reflection;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class SharedSettingsTests
{
    private string folder = null!;
    private readonly List<MouseWithoutBorders.Class.Settings> created = new();
    [TestInitialize] public void Setup() { folder = Path.Combine(Path.GetTempPath(), "mwb-shared-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
    [TestCleanup] public void Cleanup()
    {
        foreach (var settings in created) settings.SaveSettingsSynchronously();
        Directory.Delete(folder, true);
    }

    private MouseWithoutBorders.Class.Settings Create(string name, int id)
    {
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "test-key-" + name;
        document.Properties.MachineID.Value = id;
        document.Properties.MachineMatrixString = ["A", "B", "C", ""];
        document.Properties.Name2IP.Value = name + " 192.0.2.1";
        document.Properties.DefaultReceivingFolder = folder;
        string path = Path.Combine(folder, name + ".json");
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        var settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
        created.Add(settings);
        return settings;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BlockedPreferencesIoDoesNotBlockInputReadsOrOrdinaryEdits(bool reload)
    {
        var settings = Create("A", 101);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = settings.GetType().GetField("_pendingDocument", instance)!;
        if (!reload)
        {
            pending.SetValue(settings, settings.GetType().GetMethod("SnapshotDocument", instance)!.Invoke(settings, null));
            settings.GetType().GetField("_writerRunning", instance)!.SetValue(settings, true);
        }
        object fileLock = typeof(PortableSettingsStore).GetField("FileLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Exception? writeError = null, editError = null;
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                if (reload) settings.UpdateSettingsFromJson();
                else settings.GetType().GetMethod("WritePendingSettings", instance)!.Invoke(settings, null);
            }
            catch (Exception error) { writeError = error; }
        }) { IsBackground = true };
        var edit = new System.Threading.Thread(() =>
        {
            try { _ = settings.DrawMouse; _ = settings.EasyMouse; settings.HideMouse = true; }
            catch (Exception error) { editError = error; }
        }) { IsBackground = true };
        bool completedBeforeDiskRelease = false, workerFinished = false, editFinished = false;
        System.Threading.Monitor.Enter(fileLock);
        try
        {
            worker.Start();
            Assert.IsTrue(System.Threading.SpinWait.SpinUntil(() =>
                (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)), "The actual worker did not reach the blocked file write.");
            edit.Start();
            completedBeforeDiskRelease = edit.Join(TimeSpan.FromSeconds(1));
        }
        finally
        {
            System.Threading.Monitor.Exit(fileLock);
            workerFinished = worker.Join(TimeSpan.FromSeconds(5));
            editFinished = (edit.ThreadState & System.Threading.ThreadState.Unstarted) != 0 || edit.Join(TimeSpan.FromSeconds(5));
        }
        Assert.IsTrue(workerFinished, "Preferences worker did not finish after releasing disk I/O.");
        Assert.IsTrue(editFinished, "Settings access did not finish after releasing disk I/O.");
        Assert.IsNull(writeError); Assert.IsNull(editError);
        Assert.IsTrue(completedBeforeDiskRelease, "Input settings reads and ordinary edits must not wait for the pending disk write.");
        Assert.IsTrue(settings.HideMouse);
    }

    [TestMethod]
    public void SynchronousSaveSupersedesAWorkerWaitingToWriteAnOlderDocument()
    {
        var settings = Create("A", 101);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = settings.GetType().GetField("_pendingDocument", instance)!;
        pending.SetValue(settings, settings.GetType().GetMethod("SnapshotDocument", instance)!.Invoke(settings, null));
        settings.GetType().GetField("_writerRunning", instance)!.SetValue(settings, true);
        object writeGate = settings.GetType().GetField("_settingsWriteLock", instance)!.GetValue(settings)!;
        bool latest = !settings.DrawMouse;
        Exception? writeError = null;
        var worker = new System.Threading.Thread(() =>
        {
            try { settings.GetType().GetMethod("WritePendingSettings", instance)!.Invoke(settings, null); }
            catch (Exception error) { writeError = error; }
        }) { IsBackground = true };
        System.Threading.Tasks.Task<bool>? readable = null;
        bool workerFinished = false;
        System.Threading.Monitor.Enter(writeGate);
        try
        {
            worker.Start();
            Assert.IsTrue(System.Threading.SpinWait.SpinUntil(() =>
                (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)), "The worker did not wait at the write gate.");
            // Also fail cleanly if a regression keeps the settings lock while waiting at the gate.
            readable = System.Threading.Tasks.Task.Run(() => settings.DrawMouse);
            Assert.IsTrue(readable.Wait(TimeSpan.FromSeconds(1)), "The delayed worker must release the settings lock before waiting to write.");
            settings.DrawMouse = latest;
            settings.SaveSettingsSynchronously(); // The same thread reenters writeGate and commits the newer document first.
        }
        finally
        {
            System.Threading.Monitor.Exit(writeGate);
            workerFinished = worker.Join(TimeSpan.FromSeconds(5));
            readable?.Wait(TimeSpan.FromSeconds(5));
        }
        Assert.IsTrue(workerFinished); Assert.IsNull(writeError);
        Assert.AreEqual(latest, PortableSettingsStore.Read(Path.Combine(folder, "A.json")).Properties.DrawMouseCursor,
            "A delayed worker must not replace a completed synchronous save with its older snapshot.");
        Assert.IsNull(pending.GetValue(settings));
    }

    [TestMethod]
    public void AnEditQueuedDuringDiskWriteIsTheFinalPersistedDocument()
    {
        var settings = Create("A", 101);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = settings.GetType().GetField("_pendingDocument", instance)!;
        pending.SetValue(settings, settings.GetType().GetMethod("SnapshotDocument", instance)!.Invoke(settings, null));
        settings.GetType().GetField("_writerRunning", instance)!.SetValue(settings, true);
        object fileLock = typeof(PortableSettingsStore).GetField("FileLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        bool latest = !settings.DrawMouse;
        Exception? writeError = null, editError = null;
        var worker = new System.Threading.Thread(() =>
        {
            try { settings.GetType().GetMethod("WritePendingSettings", instance)!.Invoke(settings, null); }
            catch (Exception error) { writeError = error; }
        }) { IsBackground = true };
        var edit = new System.Threading.Thread(() =>
        {
            try { settings.DrawMouse = latest; settings.SaveSettings(); }
            catch (Exception error) { editError = error; }
        }) { IsBackground = true };
        bool queuedBeforeDiskRelease = false, workerFinished = false, editFinished = false;
        System.Threading.Monitor.Enter(fileLock);
        try
        {
            worker.Start();
            Assert.IsTrue(System.Threading.SpinWait.SpinUntil(() =>
                (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)), "The worker did not reach the blocked file write.");
            edit.Start();
            queuedBeforeDiskRelease = edit.Join(TimeSpan.FromSeconds(1));
        }
        finally
        {
            System.Threading.Monitor.Exit(fileLock);
            workerFinished = worker.Join(TimeSpan.FromSeconds(5));
            editFinished = (edit.ThreadState & System.Threading.ThreadState.Unstarted) != 0 || edit.Join(TimeSpan.FromSeconds(5));
        }
        Assert.IsTrue(workerFinished); Assert.IsTrue(editFinished);
        Assert.IsNull(writeError); Assert.IsNull(editError);
        Assert.IsTrue(queuedBeforeDiskRelease, "A newer snapshot must queue while the previous disk write is blocked.");
        Assert.AreEqual(latest, PortableSettingsStore.Read(Path.Combine(folder, "A.json")).Properties.DrawMouseCursor,
            "Finishing an older write must not clear or lose a newer pending snapshot.");
        Assert.IsNull(pending.GetValue(settings));
    }

    [TestMethod]
    public void RecordedShortcutsPersistShareClearAndSurviveOlderPeers()
    {
        var source = Create("A", 101); var peer = Create("B", 202);
        source.HotKeySwitchMachine = (int)Keys.F1;
        CollectionAssert.AreEqual(new[] { (int)Keys.ControlKey, (int)Keys.Menu, (int)Keys.F2 }, source.GetShortcutKeys(KeyboardAction.Computer2));
        source.SetShortcutKeys(KeyboardAction.NextComputer, [(int)Keys.CapsLock]);
        source.SetShortcutKeys(KeyboardAction.Computer2, []);
        source.SaveSettingsSynchronously();
        var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(Path.Combine(folder, "A.json")), false); created.Add(reloaded);
        CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, reloaded.GetShortcutKeys(KeyboardAction.NextComputer));
        Assert.AreEqual(0, reloaded.GetShortcutKeys(KeyboardAction.Computer2).Length, "Clearing does not restore the legacy F2 fallback.");
        var snapshot = source.SharedSnapshot();
        Assert.IsTrue(peer.AcceptSharedSettings(snapshot));
        CollectionAssert.AreEqual(source.GetShortcutKeys(KeyboardAction.NextComputer), peer.GetShortcutKeys(KeyboardAction.NextComputer));
        snapshot.Values.Remove("ShortcutKeys"); snapshot.Revision++;
        Assert.IsTrue(peer.AcceptSharedSettings(snapshot), "An existing peer's complete original schema is still accepted.");
        CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, peer.GetShortcutKeys(KeyboardAction.NextComputer), "An older peer cannot erase the new bindings it cannot represent.");
        snapshot.Values.Remove("ShareClipboard");
        Assert.ThrowsException<InvalidDataException>(() => snapshot.ReadValues(), "Existing required preferences remain required.");
        var defaults = Create("C", 303);
        CollectionAssert.AreEqual(new[] { (int)Keys.ControlKey, (int)Keys.Menu, (int)Keys.F5 }, defaults.GetShortcutKeys(KeyboardAction.NextComputer));
    }

    [TestMethod]
    public void LateKnownPeerCannotRestoreOldLayoutAndLocalValuesStayLocal()
    {
        var first = Create("A", 101); var late = Create("C", 303);
        var old = late.SharedSnapshot();
        first.MachineMatrixString = "B,A,C,";
        first.EasyMouse = (int)EasyMouseOption.Custom;
        first.EasyMouseActivationKeys = [(int)System.Windows.Forms.Keys.CapsLock];
        first.ShareClipboard = false; first.TransferFile = false;
        first.DrawMouse = false; late.DrawMouse = true;
        first.HotKeyReconnect = new HotkeySettings(false, true, true, false, 'R');
        first.CommitSharedSettings();
        var current = first.SharedSnapshot();
        Assert.IsFalse(first.AcceptSharedSettings(old));
        Assert.IsTrue(late.AcceptSharedSettings(current));
        Assert.AreEqual("B,A,C,", late.MachineMatrixString);
        CollectionAssert.AreEqual(first.EasyMouseActivationKeys, late.EasyMouseActivationKeys);
        Assert.IsFalse(late.ShareClipboard); Assert.IsFalse(late.TransferFile);
        Assert.IsTrue(late.DrawMouse, "A peer must not replace this PC's cursor renderer choice.");
        Assert.AreEqual(first.HotKeyReconnect.Code, late.HotKeyReconnect.Code);
        Assert.AreEqual("test-key-C", late.MyKey);
        Assert.AreEqual(303, late.MachineId);
        Assert.AreEqual("C 192.0.2.1", late.Name2IP);
        Assert.AreEqual(folder, late.DefaultReceivingFolder);
        Assert.IsFalse(late.AcceptSharedSettings(old));
        Assert.IsFalse(late.AcceptSharedSettings(current));
        late.SaveSettingsSynchronously();
        var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(Path.Combine(folder, "C.json")), false);
        Assert.AreEqual(current.Revision, reloaded.SharedSnapshot().Revision);
        Assert.AreEqual(current.Writer, reloaded.SharedSnapshot().Writer);
        Assert.IsTrue(reloaded.DrawMouse);
        long revision = first.SharedSnapshot().Revision;
        first.DrawMouse = true; first.CommitSharedSettings(); first.SaveSettingsSynchronously();
        Assert.AreEqual(revision, first.SharedSnapshot().Revision, "Local cursor edits must not publish a shared revision.");
        first.DrawMouse = false; first.SaveSettingsSynchronously();
        Assert.IsFalse(new MouseWithoutBorders.Class.Settings(new SettingsUtils(Path.Combine(folder, "A.json")), false).DrawMouse);
        string wire = JsonSerializer.Serialize(current);
        foreach (string local in new[] { "SecurityKey", "Name2IP", "DefaultReceivingFolder", "MachineID", "AppMode", "TCPPort", "DrawMouseCursor" })
            Assert.IsFalse(wire.Contains('"' + local + '"'), local);
    }

    [TestMethod]
    public void ClipboardChoiceDoesNotDisableExplicitFileTransfers()
    {
        var original = Setting.Values;
        try
        {
            Setting.Values = Create("A", 101);
            Setting.Values.ShareClipboard = false;
            Setting.Values.TransferFile = true;
            var policy = typeof(DurableTransfers).GetMethod("CheckPolicy", BindingFlags.Static | BindingFlags.NonPublic)!;
            policy.Invoke(null, new object[] { false });
            Setting.Values.TransferFile = false;
            var blocked = Assert.ThrowsException<TargetInvocationException>(() => policy.Invoke(null, new object[] { false }));
            Assert.IsInstanceOfType<OperationCanceledException>(blocked.InnerException);
        }
        finally { Setting.Values = original; }
    }

    [TestMethod]
    public void SharedRevisionOwnsDeduplicationEvenWhenSenderPacketNumbersRepeat()
    {
        var original = Setting.Values;
        var originalSockets = Common.Sk;
        var originalCallback = Common.InputCallbackForm;
        var originalReceived = Package.PackageReceived;
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var recent = (Array)typeof(Receiver).GetField("RecentProcessedPackageIDs", flags)!.GetValue(null)!;
        var savedRecent = (Array)recent.Clone();
        var index = typeof(Receiver).GetField("recentProcessedPackageIndex", flags)!;
        object savedIndex = index.GetValue(null)!;
        long savedProcessed = Receiver.processedPackageCount, savedSkipped = Receiver.skippedPackageCount;
        try
        {
            var source = Create("A", 101); var receiver = Create("B", 202);
            Setting.Values = receiver; Common.Sk = null; Common.InputCallbackForm = null;
            using var tcp = new TcpSk(true, null!, SocketStatus.Connected, "A") { MachineId = 101 };
            const int repeatedId = 17364902;
            Receiver.ProcessPackage(new DATA { Type = PackageType.Hi, Id = repeatedId }, tcp);
            source.MachineMatrixString = "C,B,A,"; source.CommitSharedSettings();
            tcp.ReceivedSharedSettings = source.SharedSnapshot();
            var packet = new DATA { Type = PackageType.SharedSettings, Id = repeatedId };
            Receiver.ProcessPackage(packet, tcp);
            Assert.AreEqual(source.MachineMatrixString, receiver.MachineMatrixString,
                "A newer shared revision must survive a reused input packet number.");
            source.EasyMouseActivationKeys = [(int)Keys.CapsLock]; source.CommitSharedSettings();
            tcp.ReceivedSharedSettings = source.SharedSnapshot();
            Receiver.ProcessPackage(packet, tcp);
            CollectionAssert.AreEqual(source.EasyMouseActivationKeys, receiver.EasyMouseActivationKeys);
            receiver.Changed = false;
            tcp.ReceivedSharedSettings = source.SharedSnapshot();
            Receiver.ProcessPackage(packet, tcp);
            Assert.IsFalse(receiver.Changed, "The shared revision owner already rejects duplicate socket copies.");
        }
        finally
        {
            Setting.Values = original; Common.Sk = originalSockets; Common.InputCallbackForm = originalCallback;
            Package.PackageReceived = originalReceived; MachineStuff.MachineMatrix = null;
            Array.Copy(savedRecent, recent, recent.Length); index.SetValue(null, savedIndex);
            Receiver.processedPackageCount = savedProcessed; Receiver.skippedPackageCount = savedSkipped;
        }
    }

    [TestMethod]
    public void SimultaneousEditsConvergeAndAdoptionDoesNotCreateAnEdit()
    {
        var first = Create("A", 101); var second = Create("B", 202);
        first.MachineMatrixString = "B,A,C,"; first.CommitSharedSettings();
        second.MachineMatrixString = "C,B,A,"; second.CommitSharedSettings();
        var a = first.SharedSnapshot(); var b = second.SharedSnapshot();
        Assert.AreEqual(a.Revision, b.Revision);
        Assert.IsTrue(first.AcceptSharedSettings(b));
        Assert.IsFalse(second.AcceptSharedSettings(a));
        Assert.AreEqual(first.MachineMatrixString, second.MachineMatrixString);
        Assert.AreEqual(b.Revision, first.SharedSnapshot().Revision);
        first.EasyMouseActivationKeys = [(int)System.Windows.Forms.Keys.CapsLock];
        first.CommitSharedSettings();
        Assert.IsTrue(second.AcceptSharedSettings(first.SharedSnapshot()));
    }

    [TestMethod]
    public async Task OpenSettingsReloadDoesNotPublishAnotherRevision()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            var original = Setting.Values;
            string originalName = Common.MachineName;
            var matrix = typeof(MachineStuff).GetField("mcMatrix", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previousMatrix = matrix.GetValue(null);
            try
            {
                var source = Create("A", 101); var receiver = Create("B", 202);
                Setting.Values = receiver; receiver.Username = "shared-settings-test"; Common.MachineName = "B";
                matrix.SetValue(null, null);
                using var form = new MouseWithoutBorders.FrmMatrix();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("OnLoad", flags)!.Invoke(form, [EventArgs.Empty]);
                typeof(MouseWithoutBorders.FrmMatrix).GetField("formShown", flags)!.SetValue(form, true);
                source.EasyMouse = (int)EasyMouseOption.Custom;
                source.EasyMouseActivationKeys = [(int)Keys.CapsLock];
                source.HotKeyReconnect = new HotkeySettings(false, true, true, false, 'R');
                source.KeyboardShortcutsEnabled = true;
                source.ShareClipboard = false; source.TransferFile = true;
                source.MachineMatrixString = "C,B,A,"; source.CommitSharedSettings();
                var sent = source.SharedSnapshot(); Assert.IsTrue(receiver.AcceptSharedSettings(sent));
                matrix.SetValue(null, null);
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("LoadSettingsToUI", flags)!.Invoke(form, null);
                Assert.AreEqual(sent.Revision, receiver.SharedSnapshot().Revision);
                Assert.AreEqual(sent.Writer, receiver.SharedSnapshot().Writer);
                Assert.IsFalse(receiver.ShareClipboard); Assert.IsTrue(receiver.TransferFile);
                Assert.AreEqual("R", ((ComboBox)typeof(MouseWithoutBorders.FrmMatrix).GetField("comboBoxReconnect", flags)!.GetValue(form)!).Text);
                var chord = (Button)typeof(MouseWithoutBorders.FrmMatrix).GetField("keyChordButton", flags)!.GetValue(form)!;
                StringAssert.Contains(chord.Text, "Caps Lock");
                receiver.Name2IP = "B 192.0.2.2"; receiver.CommitSharedSettings();
                Assert.AreEqual(sent.Revision, receiver.SharedSnapshot().Revision, "A local network edit must not create a shared edit.");
                source.SaveSettingsSynchronously(); receiver.SaveSettingsSynchronously();
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
            finally { Setting.Values = original; Common.MachineName = originalName; matrix.SetValue(null, previousMatrix); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    public void ActualTransportSerializesSharedAndOrdinaryFramesAndRejectsPreauthentication()
    {
        var original = Setting.Values;
        var originalTransport = Common.Sk;
        int previousId = Package.PackageID;
        var previousSent = Package.PackageSent;
        try
        {
            Setting.Values = Create("A", 101);
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(listener.LocalEndPoint!);
            using var accepted = listener.Accept();
            using var wire = new SplitWriteStream();
            using var tcp = new TcpSk(true, socket, SocketStatus.Connected, "B") { MachineId = 202 };
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(tcp, wire);
            typeof(TcpSk).GetField("decryptedStream", flags)!.SetValue(tcp, wire);
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", flags)!.SetValue(transport, new List<TcpSk> { tcp });
            Common.Sk = transport;
            Parallel.Invoke(
                () => { for (int i = 0; i < 20; i++) transport.SendSharedSettings(tcp); },
                () => { for (int i = 0; i < 20; i++) transport.TcpSend(tcp, new DATA { Type = PackageType.Hi, Id = 10000 + i, Src = (ID)101 }); });
            wire.Position = 0;
            int shared = 0, ordinary = 0;
            for (int i = 0; i < 40; i++)
            {
                var packet = SocketStuff.TcpReceiveData(tcp, out _);
                if (packet.Type == PackageType.SharedSettings)
                {
                    shared++;
                    Assert.IsNotNull(tcp.ReceivedSharedSettings);
                    Assert.AreEqual(4, tcp.ReceivedSharedSettings.ReadValues().MachineMatrixString.Count);
                }
                else { Assert.AreEqual(PackageType.Hi, packet.Type); ordinary++; }
            }
            Assert.AreEqual(20, shared); Assert.AreEqual(20, ordinary); Assert.AreEqual(wire.Length, wire.Position);
            wire.SetLength(0); wire.Position = 0;
            transport.SendSharedSettings(tcp);
            wire.Position = 0; tcp.Status = SocketStatus.Handshaking;
            Assert.ThrowsException<InvalidDataException>(() => SocketStuff.TcpReceiveData(tcp, out _));
            Assert.AreEqual(32L, wire.Position, "An unauthenticated frame must be rejected before reading its JSON body.");
            Setting.Values.SaveSettingsSynchronously();
        }
        finally { Setting.Values = original; Common.Sk = originalTransport; Package.PackageID = previousId; Package.PackageSent = previousSent; }
    }

    private sealed class SplitWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            int half = count / 2;
            base.Write(buffer, offset, half);
            System.Threading.Thread.Sleep(1);
            base.Write(buffer, offset + half, count - half);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void DirectCommandsUseOneStreamAndBroadcastStillUsesBoth(bool firstFails, bool broadcast)
    {
        var original = Setting.Values;
        var originalTransport = Common.Sk;
        bool originalInvalidKey = SocketStuff.InvalidKeyFound;
        int originalId = Package.PackageID;
        var originalSent = Package.PackageSent;
        var matrix = typeof(MachineStuff).GetField("mcMatrix", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalMatrix = matrix.GetValue(null);
        try
        {
            Setting.Values = Create("A", 101);
            matrix.SetValue(null, null);
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(listener.LocalEndPoint!);
            using var accepted = listener.Accept();
            using MemoryStream firstWire = firstFails ? new RejectingWriteStream() : new MemoryStream();
            using var secondWire = new MemoryStream();
            using var first = new TcpSk(false, socket, SocketStatus.Connected, "B") { MachineId = 202 };
            using var second = new TcpSk(true, socket, SocketStatus.Connected, "B") { MachineId = 202 };
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(first, firstWire);
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(second, secondWire);
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", flags)!.SetValue(transport, new List<TcpSk> { first, second });
            Common.Sk = transport;
            // Keep a deliberate failed write from starting unrelated real reconnects.
            SocketStuff.InvalidKeyFound = true;
            Common.SkSend(new DATA { Type = PackageType.Mouse, Src = (ID)101, Des = broadcast ? ID.ALL : (ID)202,
                Md = new MOUSEDATA { X = 1234, Y = 5678, dwFlags = WM.WM_MOUSEMOVE } }, null, false);
            Assert.AreEqual(broadcast ? 64L : 32L, firstWire.Length + secondWire.Length,
                "A single-PC input must not be replayed on both halves of its connection pair.");
            Assert.AreEqual(firstFails ? 0L : 32L, firstWire.Length);
            Assert.AreEqual(firstFails || broadcast ? 32L : 0L, secondWire.Length,
                "A failed first write must still use the other connected stream.");
        }
        finally
        {
            Setting.Values = original;
            Common.Sk = originalTransport;
            SocketStuff.InvalidKeyFound = originalInvalidKey;
            Package.PackageID = originalId;
            Package.PackageSent = originalSent;
            matrix.SetValue(null, originalMatrix);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConnectionQueryDoesNotWriteAProbeOrFlushAnAuthenticatedSocket(bool client)
    {
        var original = Setting.Values; var oldTransport = Common.Sk; string oldName = Common.MachineName;
        var oldSent = Package.PackageSent;
        try
        {
            Setting.Values = Create("A", 101); Common.MachineName = "A";
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
            using var wire = new ObservedQueryStream();
            using var tcp = new TcpSk(client, socket, SocketStatus.Connected, "B") { MachineId = 202 };
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(tcp, wire);
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", flags)!.SetValue(transport, new List<TcpSk> { tcp }); Common.Sk = transport;
            Assert.IsTrue(Common.IsConnectedTo((ID)202));
            Assert.IsTrue(Common.IsConnectedTo((ID)101), "The local PC requires no remote socket.");
            Assert.AreEqual(0L, wire.Length, "A routing/state query must not send a TCP probe.");
            Assert.AreEqual(0, wire.Flushes, "A routing/state query must not flush the input transport.");
            Assert.AreEqual(SocketStatus.Connected, tcp.Status);
        }
        finally { Setting.Values = original; Common.Sk = oldTransport; Common.MachineName = oldName; Package.PackageSent = oldSent; }
    }

    [DataTestMethod]
    [DataRow((int)SocketStatus.Handshaking, true, 202u)]
    [DataRow((int)SocketStatus.Connected, false, 202u)]
    [DataRow((int)SocketStatus.Connected, true, 303u)]
    public void ConnectionQueryRejectsUnauthenticatedUnknownAndObservedFailedSockets(int statusValue, bool backing, uint peer)
    {
        var status = (SocketStatus)statusValue;
        var original = Setting.Values; var oldTransport = Common.Sk;
        try
        {
            Setting.Values = Create("A", 101);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var tcp = new TcpSk(false, backing ? socket : null!, status, "B") { MachineId = peer };
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(transport, new List<TcpSk> { tcp }); Common.Sk = transport;
            Assert.IsFalse(Common.IsConnectedTo((ID)202));
            Assert.AreEqual(status, tcp.Status, "Only the send/lifecycle owner records socket failures.");
        }
        finally { Setting.Values = original; Common.Sk = oldTransport; }
    }

    private sealed class ObservedQueryStream : MemoryStream
    {
        internal int Flushes;
        public override void Flush() { Flushes++; base.Flush(); }
    }

    private sealed class RejectingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Deliberate test write failure.");
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void FileEndpointUsesAuthenticatedConnectionsInEitherDirection(bool client, bool connected)
    {
        var original = Common.Sk;
        using var tcp = new TcpSk(client, null!, connected ? SocketStatus.Connected : SocketStatus.Handshaking, "B", IPAddress.Loopback);
        try
        {
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, new List<TcpSk> { tcp });
            Common.Sk = transport;
            Assert.AreEqual(connected ? IPAddress.Loopback : null, Common.GetConnectedSocketIPAddressFor("b"));
            Assert.IsNull(Common.GetConnectedSocketIPAddressFor("other"));
        }
        finally { Common.Sk = original; }
    }

    [TestMethod]
    public void FramesAreBoundedAuthenticatedAndConsumeExactlyThePaddedBody()
    {
        var settings = Create("A", 101); settings.CommitSharedSettings();
        byte[] json = SharedSettingsFrame.Encode(settings.SharedSnapshot());
        int padded = SharedSettingsFrame.PaddedLength(json.Length);
        Assert.AreEqual(0, padded % 32);
        byte[] bytes = new byte[padded + 32]; json.CopyTo(bytes, 0); bytes[padded] = 42;
        using var stream = new MemoryStream(bytes);
        var decoded = SharedSettingsFrame.Read(stream, json.Length, true);
        Assert.AreEqual(settings.SharedSnapshot().Revision, decoded.Revision);
        decoded.Values["SecurityKey"] = JsonSerializer.SerializeToElement("ignored-test-key");
        Assert.AreEqual("", decoded.ReadValues().SecurityKey.Value, "Unknown values must not be adopted.");
        decoded.Values.Remove("WrapMouse");
        Assert.ThrowsException<InvalidDataException>(() => decoded.ReadValues());
        Assert.AreEqual(padded, stream.Position); Assert.AreEqual(42, stream.ReadByte());
        stream.Position = 0;
        Assert.ThrowsException<InvalidDataException>(() => SharedSettingsFrame.Read(stream, json.Length, false));
        Assert.AreEqual(0L, stream.Position);
        foreach (int invalid in new[] { 0, -1, SharedSettingsFrame.MaximumBytes + 1, int.MaxValue })
            Assert.ThrowsException<InvalidDataException>(() => SharedSettingsFrame.Read(stream, invalid, true));
        using var truncated = new MemoryStream(json);
        Assert.ThrowsException<EndOfStreamException>(() => SharedSettingsFrame.Read(truncated, json.Length + 32, true));
        using var malformed = new MemoryStream(new byte[32]);
        Assert.IsNull(SharedSettingsFrame.Read(malformed, 1, true));
    }
}
