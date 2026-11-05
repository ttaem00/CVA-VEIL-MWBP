using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using SharedClipboard = MouseWithoutBorders.Core.Clipboard;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class ClipboardFileLimitTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private string folder = null!;
    private MouseWithoutBorders.Class.Settings previousSettings = null!;
    private SocketStuff? previousSockets;
    private bool previousUserMode;
    private ID previousDestination;
    private int[]? previousIcons;
    private int previousIconIndex;
    private object? previousTopology;
    private object? previousMatrix;
    private MouseWithoutBorders.FrmScreen? previousMainForm;
    private Dictionary<FieldInfo, object?> clipboardState = null!;
    private readonly List<MouseWithoutBorders.Class.Settings> settings = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), "mwb-clipboard-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        previousSettings = Setting.Values; previousSockets = Common.Sk;
        previousUserMode = Common.RunWithNoAdminRight; previousDestination = Common.DesMachineID;
        previousMainForm = Common.MainForm; Common.MainForm = null!;
        previousIcons = Common.toggleIcons; previousIconIndex = Common.toggleIconsIndex;
        previousTopology = typeof(MonitorTopology).GetField("state", PrivateStatic)!.GetValue(null);
        var matrixField = typeof(MachineStuff).GetField("mcMatrix", PrivateStatic)!;
        previousMatrix = matrixField.GetValue(null); matrixField.SetValue(null, null);
        clipboardState = typeof(SharedClipboard).GetFields(PrivateStatic)
            .Where(field => !field.IsLiteral && !field.IsInitOnly).ToDictionary(field => field, field => field.GetValue(null));
        Setting.Values = Create("A", 101);
        Common.Sk = null!; Common.RunWithNoAdminRight = true; Common.DesMachineID = Common.MachineID;
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var value in settings) value.SaveSettingsSynchronously();
        Setting.Values = previousSettings; Common.Sk = previousSockets!;
        Common.RunWithNoAdminRight = previousUserMode; Common.DesMachineID = previousDestination;
        Common.MainForm = previousMainForm!;
        Common.toggleIcons = previousIcons!; Common.toggleIconsIndex = previousIconIndex;
        typeof(MonitorTopology).GetField("state", PrivateStatic)!.SetValue(null, previousTopology);
        typeof(MachineStuff).GetField("mcMatrix", PrivateStatic)!.SetValue(null, previousMatrix);
        foreach (var state in clipboardState) state.Key.SetValue(null, state.Value);
        Directory.Delete(folder, true);
    }

    private MouseWithoutBorders.Class.Settings Create(string name, int id)
    {
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "synthetic-clipboard-limit-key";
        document.Properties.MachineID.Value = id;
        document.Properties.MachineMatrixString = ["A", "B", "", ""];
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions))!;
        Assert.IsTrue(json["properties"]!.AsObject().Remove("LimitAutomaticClipboardFileSize"), "Exercise an existing profile without the new preference.");
        string path = Path.Combine(folder, name + ".json");
        PortableSettingsStore.Write(path, json.ToJsonString());
        var value = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
        settings.Add(value);
        return value;
    }

    private static void ReadyForClipboardEvent()
    {
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        SharedClipboard.HasSwitchedMachineSinceLastCopy = true;
    }

    private string FileOfLength(long bytes)
    {
        string path = Path.Combine(folder, "selected.bin");
        using var file = File.Create(path);
        file.SetLength(bytes);
        return path;
    }

    [DataTestMethod]
    [DataRow(0L, true, true)]
    [DataRow(314572799L, true, true)]
    [DataRow(314572800L, true, true)]
    [DataRow(314572801L, true, false)]
    [DataRow(314572801L, false, true)]
    public void AutomaticFilePublicationHonorsTheExactBoundary(long bytes, bool limit, bool published)
    {
        string path = FileOfLength(bytes);
        Setting.Values.LimitAutomaticClipboardFileSize = limit;
        SharedClipboard.LastDragDropFile = "previous clipboard selection";
        ReadyForClipboardEvent();
        SharedClipboard.CheckClipboardEx(new ByteArrayOrString(path), true);
        Assert.AreEqual(published ? path : null, SharedClipboard.LastDragDropFile);
        Assert.IsNull(SharedClipboard.LastClipboardData);
        Assert.AreEqual(bytes, new FileInfo(path).Length, "Publication must not read or rewrite the source file.");
    }

    [TestMethod]
    public void DisablingTheLimitAllowsTheSamePreviouslyBlockedFileWithoutResettingClipboardState()
    {
        string path = FileOfLength(314572801L);
        Setting.Values.LimitAutomaticClipboardFileSize = true;
        SharedClipboard.HasSwitchedMachineSinceLastCopy = false;
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        SharedClipboard.CheckClipboardEx(new ByteArrayOrString(path), true);
        Assert.IsNull(SharedClipboard.LastDragDropFile);
        Assert.IsFalse(SharedClipboard.HasSwitchedMachineSinceLastCopy);

        Setting.Values.LimitAutomaticClipboardFileSize = false;
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        SharedClipboard.CheckClipboardEx(new ByteArrayOrString(path), true);
        Assert.AreEqual(path, SharedClipboard.LastDragDropFile);
        Assert.IsFalse(SharedClipboard.HasSwitchedMachineSinceLastCopy);
        Assert.AreEqual(314572801L, new FileInfo(path).Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedClipboardPayloadIsNotRepublishedAndKeepsThePublishedData(bool image)
    {
        ReadyForClipboardEvent();
        var payload = image ? new ByteArrayOrString(new byte[] { 1, 2, 3, 4 }) : new ByteArrayOrString("same clipboard text");
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(payload, false));
        byte[] published = SharedClipboard.LastClipboardData;
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        Assert.IsFalse(SharedClipboard.CheckClipboardEx(payload, false), "An identical observed value must not compress or broadcast again.");
        Assert.AreSame(published, SharedClipboard.LastClipboardData, "Ignoring a duplicate must retain the data served to a later clipboard request.");
        Assert.AreEqual(image, SharedClipboard.IsClipboardDataImage);
    }

    [TestMethod]
    public void CaseOnlyTextChangeAndARepeatAfterSwitchRemainPublishable()
    {
        ReadyForClipboardEvent();
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(new ByteArrayOrString("CustomerCode"), false));
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(new ByteArrayOrString("CUSTOMERCODE"), false));
        ReadyForClipboardEvent();
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(new ByteArrayOrString("CUSTOMERCODE"), false));
    }

    [TestMethod]
    public void AnObservedImageIsOwnedByItsPublicationRatherThanTheRpcCallersMutableArray()
    {
        byte[] image = [1, 2, 3, 4];
        ReadyForClipboardEvent();
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(new ByteArrayOrString(image), false));
        image[0] = 9;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, SharedClipboard.LastClipboardData);
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        Assert.IsFalse(SharedClipboard.CheckClipboardEx(new ByteArrayOrString(new byte[] { 1, 2, 3, 4 }), false));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AClipboardSenderFinishesWithoutResettingAPeerThatHasNotReadOrClosedYet(bool image)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var sender = new TcpClient(); await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiver = await listener.AcceptTcpClientAsync();
        receiver.ReceiveTimeout = 3000;
        byte[] bytes = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
        string laterFile = FileOfLength(8); SharedClipboard.LastDragDropFile = laterFile;
        using var output = sender.GetStream();
        // The peer deliberately reads only after the sender has returned. Close(10)
        // instead waits for this peer's FIN and then resets its still-open connection.
        await Task.Run(() => SocketStuff.SendClipboardData(sender.Client, output, bytes, image)).WaitAsync(TimeSpan.FromSeconds(5));
        using var input = receiver.GetStream();
        byte[] headerBytes = new byte[TransferHeader.Size]; input.ReadExactly(headerBytes);
        var header = TransferHeader.Parse(headerBytes);
        Assert.AreEqual(image ? "image" : "text", header.Name);
        Assert.AreEqual((long)bytes.Length, header.Length);
        byte[] received = new byte[bytes.Length]; input.ReadExactly(received);
        CollectionAssert.AreEqual(bytes, received);
        FileTransferEngine.ReadPadding(input, bytes.Length);
        Assert.AreEqual(0, input.Read(new byte[1]), "A slow clipboard consumer must observe graceful EOF rather than a reset.");
        Assert.AreEqual(8L, new FileInfo(laterFile).Length, "The fixed clipboard payload must never send or mutate the later file selection.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AReceivedClipboardBodyDoesNotWaitForAnUnrelatedLegacyFileReceive(bool image)
    {
        using var payload = new MemoryStream();
        if (image)
        {
            using var bitmap = new System.Drawing.Bitmap(1, 1);
            bitmap.Save(payload, System.Drawing.Imaging.ImageFormat.Png);
        }
        else
        {
            using var compressor = new System.IO.Compression.DeflateStream(payload, System.IO.Compression.CompressionMode.Compress, true);
            compressor.Write(System.Text.Encoding.Unicode.GetBytes("UNIclipboard while a file is receiving"));
        }
        byte[] bytes = payload.ToArray();
        using var body = new MemoryStream();
        body.Write(new TransferHeader(bytes.Length, image ? "image" : "text").Encode());
        body.Write(bytes); FileTransferEngine.WritePadding(body, bytes.Length); body.Position = 0;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var fileReceive = Task.Run(() => SharedClipboard.ExecuteClipboardReceive(() => { entered.Set(); release.Wait(); }, 1000));
        Task? receive = null;
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
            SharedClipboard.LastMachineWithClipboardData = "OLDER-CLIPBOARD-PEER";
            SharedClipboard.clipboardCopiedTime = 1234;
            receive = Task.Run(() => SharedClipboard.ReceiveAndProcessClipboardData("B", socket, Stream.Null, body, "Other"));
            Assert.AreSame(receive, await Task.WhenAny(receive, Task.Delay(1000)), "Text and images must complete while the legacy file receive still owns its gate.");
            await receive;
            Assert.AreEqual(body.Length, body.Position, "The validated clipboard body must actually be consumed.");
            Assert.IsTrue((long)typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.GetValue(null)! > 0,
                "The receive owner must reach clipboard publication rather than rejecting the connection.");
            Assert.AreEqual(0L, SharedClipboard.clipboardCopiedTime,
                "A later MachineSwitched must not request the older peer's advertised file over this received text/image.");
        }
        finally
        {
            release.Set(); Assert.IsTrue(await fileReceive);
            if (receive != null) await receive;
        }
    }

    [DataTestMethod]
    [DataRow(false, false, true, false)]
    [DataRow(true, false, true, false)]
    [DataRow(false, true, true, false)]
    [DataRow(true, true, true, false)]
    [DataRow(false, false, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, false, true, true)]
    [DataRow(true, false, true, true)]
    public async Task SmallClipboardUsesTheAuthenticatedClipboardSocketAndNeverTheInputStream(bool image, bool replacedByFile,
        bool inMatrix, bool removedDuringHandshake)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        string oldName = Common.MachineName, oldKey = Encryption.MyKey;
        var poolField = typeof(MachineStuff).GetField("_machinePool", PrivateStatic)!;
        object? oldPool = poolField.GetValue(null);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var controlListener = new TcpListener(IPAddress.Loopback, 0); controlListener.Start();
        using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)controlListener.LocalEndpoint).Port);
        using var accepted = await controlListener.AcceptTcpClientAsync();
        using var inputWire = new MemoryStream();
        using var peer = new TcpSk(true, control.Client, SocketStatus.Connected, "B", IPAddress.Loopback) { MachineId = 202 };
        typeof(TcpSk).GetField("encryptedStream", instance)!.SetValue(peer, inputWire);
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); GC.SuppressFinalize(transport);
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", instance)!.SetValue(transport, new List<TcpSk> { peer });
        typeof(SocketStuff).GetField("<TcpPort>k__BackingField", instance)!.SetValue(transport, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? receiver = null;
        try
        {
            Common.MachineName = "A"; Common.Sk = transport;
            if (!inMatrix) Setting.Values.MachineMatrixString = "A,,,";
            Encryption.InitEncryption(); Encryption.MyKey = "synthetic-clipboard-wire-key";
            var pool = new MachinePool(); poolField.SetValue(null, pool);
            pool.LearnMachine("B"); pool.TryUpdateMachineID("B", (ID)202, true);
            receiver = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                connected.SetResult();
                client.ReceiveTimeout = client.SendTimeout = 3000;
                using var network = client.GetStream();
                using var input = Encryption.GetDecryptedStream(network);
                Common.SendOrReceiveARandomDataBlockPerInitialIV(input, false);
                byte[] helloBytes = new byte[Package.PACKAGE_SIZE_EX]; input.ReadExactly(helloBytes);
                var hello = new DATA(helloBytes);
                Assert.AreEqual(PackageType.ClipboardPush, hello.Type);
                Assert.AreEqual((ID)101, hello.Src);
                arrived.SetResult();
                await release.Task.WaitAsync(timeout.Token);
                using var output = Encryption.GetEncryptedStream(network);
                Common.SendOrReceiveARandomDataBlockPerInitialIV(output);
                output.Write(new DATA { Type = PackageType.ClipboardPush, Src = (ID)202, MachineName = "B", PostAction = ClipboardPostAction.Other }.Bytes);
                if (replacedByFile || removedDuringHandshake)
                {
                    Assert.AreEqual(0, input.Read(new byte[32]), "A superseded clipboard or removed PC must not receive old text, an image, or the newly copied file.");
                    return;
                }
                byte[] headerBytes = new byte[TransferHeader.Size]; input.ReadExactly(headerBytes);
                var header = TransferHeader.Parse(headerBytes);
                Assert.AreEqual(image ? "image" : "text", header.Name);
                byte[] contents = new byte[checked((int)header.Length)]; input.ReadExactly(contents);
                if (image) CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, contents);
                else Assert.AreEqual("small immediate clipboard", ClipboardTextDecoder.Decode(contents));
            });
            ReadyForClipboardEvent();
            var payload = image ? new ByteArrayOrString(new byte[] { 1, 2, 3, 4 }) : new ByteArrayOrString("small immediate clipboard");
            Assert.IsTrue(SharedClipboard.CheckClipboardEx(payload, false));
            var completed = await Task.WhenAny(inMatrix ? arrived.Task : connected.Task, receiver, Task.Delay(inMatrix ? 3000 : 1000));
            Assert.AreEqual(0L, inputWire.Length, "Clipboard body frames must not queue in front of mouse packets on the input connection.");
            if (!inMatrix)
            {
                Assert.IsFalse(connected.Task.IsCompleted, "A connected PC removed from the configured matrix must receive neither a clipboard connection nor its content.");
                return;
            }
            Assert.AreSame(arrived.Task, completed, "Small clipboard data must be pushed immediately, without switching PCs or requesting it.");
            if (replacedByFile)
            {
                string copiedFile = FileOfLength(8);
                typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
                SharedClipboard.CheckClipboardEx(new ByteArrayOrString(copiedFile), true);
                Assert.AreEqual(copiedFile, SharedClipboard.LastDragDropFile);
            }
            if (removedDuringHandshake) MachineStuff.MachineMatrix = ["A", "", "", ""];
            release.SetResult();
            await receiver.WaitAsync(timeout.Token);
        }
        finally
        {
            release.TrySetCanceled(); timeout.Cancel(); listener.Stop();
            if (receiver != null) try { await receiver; } catch (OperationCanceledException) { }
            Common.Sk = null!; Common.MachineName = oldName; Encryption.MyKey = oldKey; poolField.SetValue(null, oldPool);
        }
    }

    [TestMethod]
    public void FileLimitDoesNotBlockTextImagesOrExplicitDragOffers()
    {
        Assert.IsTrue(Setting.Values.LimitAutomaticClipboardFileSize);
        ReadyForClipboardEvent();
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(new ByteArrayOrString("ordinary text"), false));
        Assert.IsFalse(SharedClipboard.IsClipboardDataImage);
        Assert.IsNotNull(SharedClipboard.LastClipboardData);
        byte[] image = [1, 2, 3, 4];
        ReadyForClipboardEvent();
        Assert.IsTrue(SharedClipboard.CheckClipboardEx(new ByteArrayOrString(image), false));
        Assert.IsTrue(SharedClipboard.IsClipboardDataImage);
        CollectionAssert.AreEqual(image, SharedClipboard.LastClipboardData);
        string path = FileOfLength(314572801L);
        ReadyForClipboardEvent();
        SharedClipboard.CheckClipboardEx(new ByteArrayOrString(path), true);
        Assert.IsNull(SharedClipboard.LastDragDropFile);
        int offer = QueuedFileTransfer.Offer([path]);
        try { CollectionAssert.AreEqual(new[] { "selected.bin" }, QueuedFileTransfer.Preview(offer)); }
        finally { QueuedFileTransfer.RevokeOffer(offer); }
        typeof(DurableTransfers).GetMethod("CheckPolicy", PrivateStatic)!.Invoke(null, [false]);
    }

    [TestMethod]
    public void LimitDefaultsOnPersistsSharesAndAnOlderPeerCannotEraseIt()
    {
        Assert.IsTrue(Setting.Values.LimitAutomaticClipboardFileSize);
        var peer = Create("B", 202);
        long before = Setting.Values.SharedSnapshot().Revision;
        Setting.Values.LimitAutomaticClipboardFileSize = false;
        Setting.Values.SaveSettingsSynchronously();
        Assert.IsFalse(new MouseWithoutBorders.Class.Settings(new SettingsUtils(Path.Combine(folder, "A.json")), false).LimitAutomaticClipboardFileSize);
        var snapshot = Setting.Values.SharedSnapshot();
        Assert.IsTrue(snapshot.Revision > before);
        Setting.Values.LimitAutomaticClipboardFileSize = false;
        Assert.AreEqual(snapshot.Revision, Setting.Values.SharedSnapshot().Revision, "The existing snapshot owner must not create a revision for an unchanged checkbox.");
        Assert.IsTrue(peer.AcceptSharedSettings(snapshot));
        Assert.IsFalse(peer.LimitAutomaticClipboardFileSize);
        Assert.IsTrue(snapshot.Values.Remove("LimitAutomaticClipboardFileSize"));
        snapshot.Revision++;
        Assert.IsTrue(peer.AcceptSharedSettings(snapshot));
        Assert.IsFalse(peer.LimitAutomaticClipboardFileSize, "A peer without this option must preserve the receiving PC's existing value.");
        snapshot.Values.Remove("ShareClipboard");
        Assert.ThrowsException<InvalidDataException>(() => snapshot.ReadValues(), "Existing required fields remain required.");
    }
}
