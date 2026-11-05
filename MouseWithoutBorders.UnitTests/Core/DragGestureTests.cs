using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using Drag = MouseWithoutBorders.Core.DragDrop;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class DragGestureTests
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private MouseWithoutBorders.Class.Settings original = null!;
    private SocketStuff transport = null!;
    private MouseWithoutBorders.FrmScreen mainForm = null!;
    private string name = null!, folder = null!, file = null!;
    private ID destination, nextDestination, previousDrop, previousDragMachine, previousClipboardMachine;
    private string? previousDragFile;
    private string? previousClipboardOwner;
    private int packetId;
    private object? machinePool;
    private readonly HashSet<int> offers = new();

    [TestInitialize]
    public void Setup()
    {
        original = Setting.Values; transport = Common.Sk; mainForm = Common.MainForm;
        name = Common.MachineName; destination = Common.DesMachineID;
        nextDestination = MachineStuff.newDesMachineID; previousDrop = MachineStuff.dropMachineID;
        previousDragMachine = Drag.DragMachine; previousClipboardMachine = MouseWithoutBorders.Core.Clipboard.LastIDWithClipboardData;
        previousDragFile = MouseWithoutBorders.Core.Clipboard.LastDragDropFile;
        previousClipboardOwner = MouseWithoutBorders.Core.Clipboard.LastMachineWithClipboardData;
        packetId = Package.PackageID;
        machinePool = typeof(MachineStuff).GetField("_machinePool", StaticPrivate)!.GetValue(null);
        typeof(MachineStuff).GetField("_machinePool", StaticPrivate)!.SetValue(null, new MachinePool());
        Common.Sk = null!; Common.MainForm = null!;
        folder = Path.Combine(Path.GetTempPath(), "mwb-gesture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); file = Path.Combine(folder, "source.txt");
        File.WriteAllText(file, "the original file stays here");
        Drag.ResetDragForTests(); Role("SOURCE", 202);
    }

    private void Role(string role, int id)
    {
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "synthetic-gesture-test-key";
        document.Properties.MachineID.Value = id;
        document.Properties.TransferFile = document.Properties.ShareClipboard = true;
        string path = Path.Combine(folder, role + ".json");
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
        Common.MachineName = role; Common.DesMachineID = Common.MachineID;
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (int offer in offers) QueuedFileTransfer.RevokeOffer(offer);
        Drag.ResetDragForTests(); Setting.Values = original; Common.Sk = transport; Common.MainForm = mainForm;
        typeof(MachineStuff).GetField("_machinePool", StaticPrivate)!.SetValue(null, machinePool);
        Common.MachineName = name; Common.DesMachineID = destination;
        MachineStuff.newDesMachineID = nextDestination; MachineStuff.dropMachineID = previousDrop;
        Drag.DragMachine = previousDragMachine; MouseWithoutBorders.Core.Clipboard.LastIDWithClipboardData = previousClipboardMachine;
        MouseWithoutBorders.Core.Clipboard.LastDragDropFile = previousDragFile;
        MouseWithoutBorders.Core.Clipboard.LastMachineWithClipboardData = previousClipboardOwner;
        Package.PackageID = packetId; Directory.Delete(folder, true);
    }

    private static DATA Request(int gesture, int command, uint target = 303) => new()
    {
        Type = PackageType.ExplorerDragDrop, Src = (ID)target, Des = Common.MachineID,
        Machine1 = (ID)gesture, Machine2 = (ID)command, Machine3 = (ID)Drag.GestureMarker,
    };
    private static object? Intent => typeof(Drag).GetField("sourceGesture", StaticPrivate)!.GetValue(null);
    private DATA Offered(object? intent = null)
    {
        var packet = (DATA)typeof(Drag).GetMethod("GestureOffer", StaticPrivate)!.Invoke(null, [intent ?? Intent])!;
        if (packet.Machine2 != ID.NONE) offers.Add((int)packet.Machine2);
        return packet;
    }
    private static DATA Incoming(int offer, bool released, uint source = 202) => new()
    {
        Type = PackageType.ClipboardDragDropOperation, Src = (ID)source, Des = Common.MachineID,
        Machine1 = released ? (ID)1 : ID.NONE, Machine2 = (ID)offer, Machine3 = (ID)QueuedFileTransfer.Marker,
    };

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ThreePcControllerReleaseAndSelectionCanArriveInEitherOrder(bool releaseFirst)
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN);
        Drag.DragDropStep03(Request(7, 0), (ID)101);
        if (releaseFirst) Drag.DragDropStep03(Request(7, 1), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        if (!releaseFirst)
        {
            Assert.AreEqual(ID.NONE, Offered().Machine1, "A selection alone is a preview, not authorization to drop.");
            Drag.DragDropStep03(Request(7, 1), (ID)101);
        }
        DATA packet = Offered();
        Assert.AreEqual((ID)202, packet.Src); Assert.AreEqual((ID)303, packet.Des);
        Assert.AreEqual((ID)1, packet.Machine1); Assert.AreEqual((ID)QueuedFileTransfer.Marker, packet.Machine3);
        Assert.AreNotEqual((ID)Drag.GestureMarker, packet.Machine3);
        CollectionAssert.AreEqual(new[] { file }, QueuedFileTransfer.TakeDurableOffer((int)packet.Machine2));
        Assert.AreEqual("the original file stays here", File.ReadAllText(file));
    }

    [TestMethod]
    public void TwoPcLocalSourceUsesTheSameActualReleaseContract()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN);
        int gesture = Package.PackageID;
        MachineStuff.newDesMachineID = (ID)303; Drag.DragDropStep02();
        Common.DesMachineID = (ID)303;
        Drag.DragDropStep01(WM.WM_LBUTTONUP); Drag.DragDropStep09(WM.WM_LBUTTONUP);
        Drag.DragDropFiles([file], Common.MachineID, gesture);
        Assert.AreEqual((ID)1, Offered().Machine1);
    }

    [TestMethod]
    public void APreviousClickOrInitialReleasedStateCannotCommitANewOffer()
    {
        Role("TARGET", 303);
        Drag.DragDropStep01(WM.WM_LBUTTONUP); Drag.DragDropStep09(WM.WM_LBUTTONUP);
        Drag.DragDropStep08_2(Incoming(44, false));
        Assert.IsTrue(Drag.IsIncomingOffer(44));
        Assert.IsTrue(Drag.IsDropping, "Only a real gesture release or a later actual UP may consume this preview.");
    }

    [TestMethod]
    public void CommittedOffersCannotReviveThroughDelayedPreviewOrDuplicateCommit()
    {
        Role("TARGET", 303);
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsFalse(Drag.IsDropping);
        Drag.DragDropStep08_2(Incoming(44, false));
        Assert.IsFalse(Drag.IsDropping);
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsFalse(Drag.IsDropping);
        Drag.DragDropStep08_2(Incoming(45, false));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
    }

    [TestMethod]
    public void CancelledOrSupersededHelperCallbacksCannotOfferTheWrongSelection()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropStep03(Request(7, 2), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        Assert.AreEqual(ID.NONE, Offered().Machine2);
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(8, 0), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        Assert.AreEqual(ID.NONE, Offered().Machine2, "An old helper result must not acquire the new gesture identity.");
        Drag.DragDropFiles([file], (ID)999, 8);
        Assert.AreEqual(ID.NONE, Offered().Machine2, "The controller is the authenticated peer, not a copied display name.");
        Drag.DragDropFiles([file], (ID)101, 8);
        Assert.AreNotEqual(ID.NONE, Offered().Machine2);
    }

    [TestMethod]
    public void RetargetKeepsTheSelectionButOldDestinationCannotConsumeTheNewOffer()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        int first = (int)Offered().Machine2;
        Drag.DragDropStep03(Request(7, 0, 404), (ID)101);
        DATA current = Offered();
        Assert.AreEqual((ID)404, current.Des); Assert.AreNotEqual((ID)first, current.Machine2);
        Assert.ThrowsException<IOException>(() => QueuedFileTransfer.TakeDurableOffer(first));
        CollectionAssert.AreEqual(new[] { file }, QueuedFileTransfer.TakeDurableOffer((int)current.Machine2));
    }

    [TestMethod]
    public void RetargetDoesNotExtendTheOriginalSelectionExpiry()
    {
        int initial = QueuedFileTransfer.Offer([file]); offers.Add(initial);
        var cache = (IDictionary)typeof(QueuedFileTransfer).GetField("Offers", StaticPrivate)!.GetValue(null)!;
        object selection = cache[initial]!;
        DateTime originalCreation = DateTime.UtcNow.AddMinutes(-9);
        selection.GetType().GetProperty("Created")!.SetValue(selection, originalCreation);
        int replacement = QueuedFileTransfer.RetargetOffer(initial); offers.Add(replacement);
        Assert.AreEqual(originalCreation, cache[replacement]!.GetType().GetProperty("Created")!.GetValue(cache[replacement]));
        Assert.ThrowsException<IOException>(() => QueuedFileTransfer.TakeDurableOffer(replacement, DateTime.UtcNow.AddMinutes(2)));
    }

    [TestMethod]
    public void AnAlreadyConsumedSelectionCannotBreakInputByRetargeting()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        int first = (int)Offered().Machine2;
        _ = QueuedFileTransfer.TakeDurableOffer(first);
        Drag.DragDropStep03(Request(7, 0, 404), (ID)101);
        Assert.AreEqual(ID.NONE, Offered().Machine2);
        Assert.IsFalse(Drag.IsDragging);
    }

    [TestMethod]
    public void LateBeginCannotChangeTheTargetAfterItsActualRelease()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropStep03(Request(7, 1, 404), (ID)101);
        Drag.DragDropStep03(Request(7, 0, 303), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        Assert.AreEqual((ID)404, Offered().Des);
        Assert.AreEqual((ID)1, Offered().Machine1);
    }

    [TestMethod]
    public void UnmarkedUnknownOrWrongControllerFramesCannotAuthorizeASelection()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN);
        var old = Request(7, 0); old.Machine3 = ID.NONE;
        Drag.DragDropStep03(old, (ID)101); Assert.IsNull(Intent);
        Drag.DragDropStep03(Request(7, 0), ID.NONE); Assert.IsNull(Intent);
        Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropStep03(Request(7, 1), (ID)999);
        Drag.DragDropFiles([file], (ID)101, 7);
        Assert.AreEqual(ID.NONE, Offered().Machine1);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void GestureCommandsRoundTripWithoutCollidingWithOfferAndCancelFields(int command)
    {
        Assert.AreNotEqual(MouseWithoutBorders.SharedConst.QUIT_CMD, Drag.CaptureContextMessage);
        var sent = Request(731, command, 404);
        var received = new DATA(sent.Bytes);
        Assert.AreEqual(PackageType.ExplorerDragDrop, received.Type);
        Assert.AreEqual((ID)731, received.Machine1); Assert.AreEqual((ID)command, received.Machine2);
        Assert.AreEqual((ID)404, received.Src); Assert.AreEqual(Common.MachineID, received.Des);
        Assert.AreEqual((ID)Drag.GestureMarker, received.Machine3);
        Assert.AreNotEqual(Drag.GestureMarker, Drag.CancelDragMarker);
        Assert.AreNotEqual(Drag.GestureMarker, QueuedFileTransfer.Marker);
        Assert.IsFalse(Drag.ReceiveDragCancellation(received));
    }

    [TestMethod]
    public void CompletingAnOlderGestureDoesNotConsumeOrHideANewerPreview()
    {
        Role("TARGET", 303);
        Drag.DragDropStep08_2(Incoming(45, false));
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
        Drag.DragDropStep08_2(Incoming(44, false));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
    }

    [TestMethod]
    public void AStaleSelectionWorkerCannotClearTheNextPhysicalPress()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        var cacheLock = typeof(QueuedFileTransfer).GetField("Sync", StaticPrivate)!.GetValue(null)!;
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var worker = new System.Threading.Thread(() =>
        {
            try { Drag.DragDropFiles([file], (ID)101, 7); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        bool waiting;
        lock (cacheLock)
        {
            worker.Start();
            waiting = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3));
            Drag.DragDropStep01(WM.WM_LBUTTONDOWN);
            Drag.DragDropStep03(Request(8, 0), (ID)101);
        }
        Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5))); worker.Join();
        Assert.IsTrue(waiting, "The old selection must be inside its offer-cache wait before the new press.");
        Assert.IsNull(failure); Assert.IsTrue(Drag.MouseDown);
        Assert.IsFalse((bool)Intent!.GetType().GetField("Captured", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Intent)!,
            "The old callback's capture acknowledgement must not finish the new gesture's helper loop.");
        Assert.AreEqual(ID.NONE, Offered().Machine2);
    }

    [TestMethod]
    public void FileGestureSelectionAndCancellationDoNotReplaceOrdinaryClipboardData()
    {
        MouseWithoutBorders.Core.Clipboard.LastIDWithClipboardData = (ID)606;
        MouseWithoutBorders.Core.Clipboard.LastMachineWithClipboardData = "ORDINARY-COPY-PC";
        MouseWithoutBorders.Core.Clipboard.LastDragDropFile = "ordinary-copy-selection";
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7); _ = Offered();
        Assert.AreEqual("ordinary-copy-selection", MouseWithoutBorders.Core.Clipboard.LastDragDropFile);
        Assert.IsTrue(Drag.HandleCancelMouse(WM.WM_RBUTTONDOWN, _ => { }, () => { }));
        Assert.AreEqual((ID)606, MouseWithoutBorders.Core.Clipboard.LastIDWithClipboardData);
        Assert.AreEqual("ORDINARY-COPY-PC", MouseWithoutBorders.Core.Clipboard.LastMachineWithClipboardData);
        Assert.AreEqual("ordinary-copy-selection", MouseWithoutBorders.Core.Clipboard.LastDragDropFile);
    }

    [TestMethod]
    public void AReplacedPreviewCannotReviveButItsAlreadyReleasedCommitCanFinishOnce()
    {
        Role("TARGET", 303);
        Drag.DragDropStep08_2(Incoming(44, false));
        Drag.DragDropStep08_2(Incoming(45, false));
        Drag.DragDropStep08_2(Incoming(44, false));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
        Assert.IsTrue(Drag.WasDragCancelled((ID)202, 44));
        Assert.IsFalse(Drag.WasDragCancelled((ID)202, 44, finalOnly: true), "Retiring a preview must not cancel a real release already in flight.");
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsTrue(Drag.WasDragCancelled((ID)202, 44, finalOnly: true));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
    }

    [TestMethod]
    public void TerminalCancellationCannotBeDowngradedToPreviewRetirement()
    {
        Role("TARGET", 303);
        var cancellation = new DATA { Type = PackageType.ClipboardDragDropEnd, Src = (ID)202, Des = Common.MachineID,
            Machine1 = (ID)202, Machine2 = (ID)44, Machine3 = (ID)Drag.CancelDragMarker };
        Assert.IsTrue(Drag.ReceiveDragCancellation(cancellation, () => { }));
        typeof(Drag).GetMethod("RememberCancelledDrag", StaticPrivate)!.Invoke(null, [(ID)202, 44, false]);
        Assert.IsTrue(Drag.WasDragCancelled((ID)202, 44, finalOnly: true));
        Drag.DragDropStep08_2(Incoming(45, false));
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
    }

    [TestMethod]
    public void ForeignTargetButtonReleaseCannotConsumeTheOriginalControllersPreview()
    {
        Role("TARGET", 303);
        Drag.DragDropStep08_2(Incoming(44, false));
        Drag.DragDropStep01(WM.WM_LBUTTONUP); Drag.DragDropStep09(WM.WM_LBUTTONUP);
        Assert.IsTrue(Drag.IsIncomingOffer(44));
        Assert.IsFalse(Drag.WasDragCancelled((ID)202, 44));
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsFalse(Drag.IsDropping);
        Assert.IsTrue(Drag.WasDragCancelled((ID)202, 44, finalOnly: true));
    }

    [TestMethod]
    public void ReturningToSourceSendsOnlyTheIdentityBoundCancellation()
    {
        Role("CONTROLLER", 101);
        using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)); listener.Listen(1);
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Connect(listener.LocalEndPoint!); using var accepted = listener.Accept();
        using var sourceWire = new MemoryStream(); using var previousTargetWire = new MemoryStream();
        using var source = new TcpSk(true, socket, SocketStatus.Connected, "SOURCE") { MachineId = 202 };
        using var target = new TcpSk(true, socket, SocketStatus.Connected, "TARGET") { MachineId = 303 };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(source, sourceWire);
        typeof(TcpSk).GetField("encryptedStream", flags)!.SetValue(target, previousTargetWire);
        var fake = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); GC.SuppressFinalize(fake);
        fake.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", flags)!.SetValue(fake, new List<TcpSk> { source, target });
        Common.Sk = fake; Common.DesMachineID = (ID)202; MachineStuff.newDesMachineID = (ID)303;
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep02();
        Common.DesMachineID = (ID)303; Drag.DragDropStep11();
        Assert.IsTrue(SpinWait.SpinUntil(() => sourceWire.Length == Package.PACKAGE_SIZE * 2, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0L, previousTargetWire.Length, "An unmarked end could arrive after another source's fresh preview and dismiss it.");
        byte[] cancelBytes = sourceWire.ToArray().AsSpan(Package.PACKAGE_SIZE, Package.PACKAGE_SIZE).ToArray();
        cancelBytes[1] = cancelBytes[2] = cancelBytes[3] = 0;
        var packet = new DATA(cancelBytes);
        Assert.AreEqual(PackageType.ExplorerDragDrop, packet.Type);
        Assert.AreEqual((ID)202, packet.Des); Assert.AreEqual((ID)2, packet.Machine2);
        Assert.AreEqual((ID)Drag.GestureMarker, packet.Machine3);

        // A new gesture drops onto the controller's own screen. Moving away after
        // its UP must not forward that pending offer while the source commit arrives.
        Common.DesMachineID = (ID)202; MachineStuff.newDesMachineID = Common.MachineID;
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep02();
        Common.DesMachineID = Common.MachineID;
        Drag.DragDropStep08_2(Incoming(44, false));
        Drag.DragDropStep01(WM.WM_LBUTTONUP); Drag.DragDropStep09(WM.WM_LBUTTONUP);
        MachineStuff.newDesMachineID = (ID)303; Drag.ChangeDropMachine();
        Assert.IsTrue(Drag.IsIncomingOffer(44));
        Assert.AreEqual(0L, previousTargetWire.Length, "An already released gesture cannot acquire a new destination.");
    }

    [TestMethod]
    public void PeerDisconnectRetiresOnlyItsCurrentPreviewAndStillAcceptsAnAlreadyReleasedCommit()
    {
        Role("TARGET", 303);
        var owner = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); GC.SuppressFinalize(owner);
        Common.Sk = owner;
        Drag.DragDropStep08_2(Incoming(44, false));
        Drag.PeerDisconnected(owner, (ID)202);
        Assert.IsFalse(Drag.IsDropping);
        Assert.IsTrue(Drag.WasDragCancelled((ID)202, 44));
        Assert.IsFalse(Drag.WasDragCancelled((ID)202, 44, finalOnly: true));
        Drag.DragDropStep08_2(Incoming(44, false));
        Assert.IsFalse(Drag.IsDropping, "A queued preview from the lost connection cannot revive itself.");
        Drag.DragDropStep08_2(Incoming(45, false, 404));
        Drag.DragDropStep08_2(Incoming(44, true));
        Assert.IsTrue(Drag.IsIncomingOffer(45));
        Assert.IsTrue(Drag.WasDragCancelled((ID)202, 44, finalOnly: true));
    }

    [TestMethod]
    public void AnUnrelatedPeerOrOldTransportCannotClearCurrentPreviewOrSendingState()
    {
        Role("TARGET", 303);
        var owner = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); GC.SuppressFinalize(owner);
        var previousOwner = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); GC.SuppressFinalize(previousOwner);
        Common.Sk = owner;
        MouseWithoutBorders.Core.Clipboard.LastMachineWithClipboardData = "ORDINARY-COPY-PC";
        Drag.DragDropStep08_2(Incoming(44, false));
        Drag.PeerDisconnected(owner, (ID)404);
        Drag.PeerDisconnected(previousOwner, (ID)202);
        Assert.IsTrue(Drag.IsIncomingOffer(44));
        Assert.IsFalse(Drag.WasDragCancelled((ID)202, 44));
        Drag.SetDragForTests(Common.MachineID, 55, sending: true);
        Drag.PeerDisconnected(owner, (ID)202);
        Assert.IsTrue(Drag.IsDragging);
        Assert.AreEqual("ORDINARY-COPY-PC", MouseWithoutBorders.Core.Clipboard.LastMachineWithClipboardData);
    }

    [TestMethod]
    public void CancellationRevokesAReleasedSourceOfferWithoutDependingOnPreviewState()
    {
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropFiles([file], (ID)101, 7);
        int offer = (int)Offered().Machine2;
        Drag.DragDropStep03(Request(7, 1), (ID)101);
        Assert.IsFalse(Drag.IsDragging);
        var cancellation = new DATA { Type = PackageType.ClipboardDragDropEnd, Src = (ID)303, Des = Common.MachineID,
            Machine1 = Common.MachineID, Machine2 = (ID)offer, Machine3 = (ID)Drag.CancelDragMarker };
        Assert.IsTrue(Drag.ReceiveDragCancellation(cancellation, () => { }));
        Assert.ThrowsException<IOException>(() => QueuedFileTransfer.TakeDurableOffer(offer));
    }

    [TestMethod]
    public void PreviewRetirementAndRemoteCancellationCannotRevokeALocalSelection()
    {
        int offer = QueuedFileTransfer.Offer([file]); offers.Add(offer);
        typeof(Drag).GetMethod("RememberCancelledDrag", StaticPrivate)!.Invoke(null, [Common.MachineID, offer, false]);
        var cancellation = new DATA { Type = PackageType.ClipboardDragDropEnd, Src = (ID)303, Des = Common.MachineID,
            Machine1 = (ID)404, Machine2 = (ID)offer, Machine3 = (ID)Drag.CancelDragMarker };
        Assert.IsTrue(Drag.ReceiveDragCancellation(cancellation, () => { }));
        CollectionAssert.AreEqual(new[] { file }, QueuedFileTransfer.TakeDurableOffer(offer));
    }

    [TestMethod]
    public void AnAcceptedReleasedWorkerFinishesWithoutOverwritingTheNextDrag()
    {
        string secondFile = Path.Combine(folder, "second.txt"); File.WriteAllText(secondFile, "next selection");
        Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(7, 0), (ID)101);
        Drag.DragDropStep03(Request(7, 1), (ID)101);
        object released = Intent!;
        var cacheLock = typeof(QueuedFileTransfer).GetField("Sync", StaticPrivate)!.GetValue(null)!;
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var worker = new System.Threading.Thread(() =>
        {
            try { Drag.DragDropFiles([file], (ID)101, 7); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        bool waiting;
        int secondOffer;
        lock (cacheLock)
        {
            worker.Start();
            waiting = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3));
            Drag.DragDropStep01(WM.WM_LBUTTONDOWN); Drag.DragDropStep03(Request(8, 0, 404), (ID)101);
            Drag.DragDropFiles([secondFile], (ID)101, 8);
            secondOffer = (int)Offered().Machine2;
        }
        Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5))); worker.Join();
        Assert.IsTrue(waiting); Assert.IsNull(failure);
        DATA first = Offered(released), second = Offered();
        Assert.AreEqual((ID)1, first.Machine1); Assert.AreEqual((ID)303, first.Des);
        Assert.AreEqual((ID)secondOffer, second.Machine2); Assert.AreEqual((ID)404, second.Des);
        Assert.AreEqual(ID.NONE, second.Machine1); Assert.IsTrue(Drag.IsDragging); Assert.IsTrue(Drag.MouseDown);
        Assert.AreEqual(secondOffer, typeof(Drag).GetField("offeredFiles", StaticPrivate)!.GetValue(null));
        CollectionAssert.AreEqual(new[] { file }, QueuedFileTransfer.TakeDurableOffer((int)first.Machine2));
        CollectionAssert.AreEqual(new[] { secondFile }, QueuedFileTransfer.TakeDurableOffer(secondOffer));
    }
}
