using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class SendFilesTests
{
    [TestMethod]
    public void ManualFileOfferIgnoresTextClipboardChoiceButKeepsFilePolicyAndSource()
    {
        WithSettings(folder =>
        {
            Setting.Values.ShareClipboard = false; Setting.Values.TransferFile = true;
            string first = Path.Combine(folder, "first.txt"), second = Path.Combine(folder, "second.txt");
            File.WriteAllText(first, "original one"); File.WriteAllText(second, "original two");
            int offer = QueuedFileTransfer.Offer([first, second]);
            try
            {
                var policy = typeof(DurableTransfers).GetMethod("CheckPolicy", BindingFlags.Static | BindingFlags.NonPublic)!;
                policy.Invoke(null, [true]);
                CollectionAssert.AreEqual(new[] { first, second }, QueuedFileTransfer.TakeDurableOffer(offer));
                Assert.AreEqual("original one", File.ReadAllText(first)); Assert.AreEqual("original two", File.ReadAllText(second));
                Setting.Values.TransferFile = false;
                Assert.ThrowsException<OperationCanceledException>(() => QueuedFileTransfer.Offer([first]));
                var blocked = Assert.ThrowsException<TargetInvocationException>(() => policy.Invoke(null, [true]));
                Assert.IsInstanceOfType<OperationCanceledException>(blocked.InnerException);
            }
            finally { QueuedFileTransfer.RevokeOffer(offer); }
        });
    }

    [TestMethod]
    public void DisconnectedTargetReportsRejectionWithoutConsumingOrSendingTheSelection()
    {
        WithSettings(folder =>
        {
            Setting.Values.TransferFile = true;
            string source = Path.Combine(folder, "file.txt"); File.WriteAllText(source, "keep");
            int offer = QueuedFileTransfer.Offer([source]); var sockets = Common.Sk;
            try
            {
                Common.Sk = null;
                ID other = Common.MachineID == (ID)1 ? (ID)2 : (ID)1;
                Assert.IsFalse(QueuedFileTransfer.SendToPeer(offer, other, "DISCONNECTED-SYNTHETIC-PC"));
                CollectionAssert.AreEqual(new[] { source }, QueuedFileTransfer.TakeDurableOffer(offer));
                Assert.AreEqual("keep", File.ReadAllText(source));
            }
            finally { Common.Sk = sockets; QueuedFileTransfer.RevokeOffer(offer); }
        });
    }

    [TestMethod]
    public void CommittedManualOfferUsesReceiverDropBeforeStartAndAcceptsTheNormalWireManifest()
        => WithSettings(folder => CommittedOfferRoundTrip(folder).GetAwaiter().GetResult());

    private static async Task CommittedOfferRoundTrip(string folder)
    {
        var sockets = Common.Sk; var main = Common.MainForm; string name = Common.MachineName, key = Encryption.MyKey;
        var poolField = typeof(MachineStuff).GetField("_machinePool", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? oldPool = poolField.GetValue(null);
        int offer = 0;
        using var controlListener = new TcpListener(IPAddress.Loopback, 0); controlListener.Start();
        using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)controlListener.LocalEndpoint).Port);
        using var acceptedControl = await controlListener.AcceptTcpClientAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task? server = null;
        try
        {
            Common.MainForm = null!; Encryption.InitEncryption(); Encryption.MyKey = "synthetic-manual-transfer-key";
            void Role(string machine, int id)
            {
                var document = new MouseWithoutBordersSettings(); document.Properties.MachineID.Value = id;
                document.Properties.ShareClipboard = false; document.Properties.TransferFile = true;
                string path = Path.Combine(folder, machine + ".json");
                PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
                Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false); Common.MachineName = machine;
            }
            Role("SOURCE", 101);
            var pool = new MachinePool(); poolField.SetValue(null, pool);
            pool.LearnMachine("RECEIVER"); pool.TryUpdateMachineID("RECEIVER", (ID)202, true);
            using var sourceWire = new MemoryStream();
            using var peer = new TcpSk(true, control.Client, SocketStatus.Connected, "RECEIVER", IPAddress.Loopback) { MachineId = 202 };
            typeof(TcpSk).GetField("encryptedStream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(peer, sourceWire);
            var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff)); GC.SuppressFinalize(transport);
            transport.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, new List<TcpSk> { peer });
            typeof(SocketStuff).GetField("<TcpPort>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, ((IPEndPoint)listener.LocalEndpoint).Port);
            Common.Sk = transport;
            byte[] contents = RandomNumberGenerator.GetBytes(193);
            string source = Path.Combine(folder, "manual.txt"); File.WriteAllBytes(source, contents);
            offer = QueuedFileTransfer.Offer([source]);
            Assert.IsTrue(QueuedFileTransfer.SendToPeer(offer, (ID)202, "RECEIVER"));
            byte[] packetBytes = sourceWire.ToArray(); packetBytes[1] = packetBytes[2] = packetBytes[3] = 0;
            var packet = new DATA(packetBytes);
            Assert.AreEqual(PackageType.ClipboardDragDropOperation, packet.Type);
            Assert.AreEqual((ID)1, packet.Machine1, "Explicit send must commit; a preview cannot establish the receiver's drop.");
            Assert.AreEqual((ID)QueuedFileTransfer.Marker, packet.Machine3);
            Assert.AreEqual((ID)1, packet.Machine4, "A manual send uses the receiver's configured folder, never its cursor's Explorer window.");
            Assert.AreEqual((ID)101, packet.Src); Assert.AreEqual((ID)202, packet.Des);
            Role("RECEIVER", 202); pool.Clear(); pool.LearnMachine("SOURCE"); pool.TryUpdateMachineID("SOURCE", (ID)101, true);
            Encryption.MyKey = "synthetic-manual-transfer-key";
            peer.MachineId = 101; peer.MachineName = "SOURCE";
            string destination = Path.Combine(folder, "received"); Directory.CreateDirectory(destination);
            string journal = Path.Combine(folder, "journal.json"); DurableTransfers.ConfigureForTests(journal);
            server = Task.Run(async () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                    client.ReceiveTimeout = client.SendTimeout = 5000;
                    using var network = client.GetStream();
                    using var input = Encryption.GetDecryptedStream(network);
                    Common.SendOrReceiveARandomDataBlockPerInitialIV(input, false);
                    byte[] header = new byte[Package.PACKAGE_SIZE_EX]; input.ReadExactly(header);
                    using var output = Encryption.GetEncryptedStream(network);
                    Common.SendOrReceiveARandomDataBlockPerInitialIV(output);
                    var handshake = new DATA { Type = PackageType.ClipboardPush, Src = (ID)101, MachineName = "SOURCE", PostAction = ClipboardPostAction.DurableFiles };
                    output.Write(handshake.Bytes);
                    var request = TransferWire.Read(input);
                    if (i == 0)
                    {
                        Assert.AreEqual("Hello", request.Op);
                        TransferWire.Write(output, new TransferMessage { Op = "Ok", StartOfferSupported = true });
                    }
                    else
                    {
                        Assert.AreEqual("StartOffer", request.Op); Assert.AreEqual(offer, request.Offer);
                        var drop = TransferJournal.Load(journal).Drops.Single();
                        Assert.AreEqual(offer, drop.Offer); Assert.AreEqual("SOURCE", drop.Peer); Assert.AreEqual(destination, drop.Folder);
                        var manifest = TransferFolders.Scan(QueuedFileTransfer.TakeDurableOffer(offer), "RECEIVER", offer, timeout.Token);
                        using var declaration = new MemoryStream(); using var reply = new MemoryStream();
                        TransferWire.Write(declaration, new TransferMessage { Op = "Declare", Create = true, Offer = offer, Files = manifest.Jobs, Groups = manifest.Groups });
                        declaration.Position = 0;
                        DurableTransfers.Serve("SOURCE", new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), reply, declaration);
                        reply.Position = 0; Assert.AreEqual("Ok", DurableTransfers.ReadReply(reply).Op, "A valid declaration must be accepted after the receiver records its own drop.");
                        TransferWire.Write(output, new TransferMessage { Op = "Ok" });
                    }
                }
            });
            using var preparation = DurableTransfers.BeginPreparation(offer, "SOURCE", false);
            await Task.Run(() => DurableTransfers.RequestPreparedOffer(preparation, destination)).WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            var job = DurableTransfers.Jobs.Single(); Assert.IsFalse(job.Sending); Assert.AreEqual(destination, job.Folder);
            using var incoming = new MemoryStream(); using var responses = new MemoryStream();
            string hash = Convert.ToHexString(SHA256.HashData(contents));
            TransferWire.Write(incoming, new TransferMessage { Op = "Begin", Id = job.Id, Hash = hash });
            TransferWire.Write(incoming, new TransferMessage { Op = "Chunk", Id = job.Id, Size = contents.Length, Offset = 0, Hash = hash });
            incoming.Write(contents); FileTransferEngine.WritePadding(incoming, contents.Length);
            TransferWire.Write(incoming, new TransferMessage { Op = "Finish", Id = job.Id }); incoming.Position = 0;
            DurableTransfers.Serve("SOURCE", new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), responses, incoming);
            Assert.AreEqual("Completed", job.State);
            CollectionAssert.AreEqual(contents, File.ReadAllBytes(job.Destination)); CollectionAssert.AreEqual(contents, File.ReadAllBytes(source));
        }
        finally
        {
            timeout.Cancel(); listener.Stop();
            try { if (server != null) try { await server.WaitAsync(TimeSpan.FromSeconds(7)); } catch (OperationCanceledException) { } }
            finally
            {
                QueuedFileTransfer.RevokeOffer(offer); DurableTransfers.ResetAfterTests();
                Common.Sk = sockets; Common.MainForm = main; Common.MachineName = name; Encryption.MyKey = key; poolField.SetValue(null, oldPool);
            }
        }
    }

    [TestMethod]
    public void TargetDialogHasOneConnectedListAndNativePickerActionWithKeyboardCancel()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using var form = SendFilesDialog.Create(["GAME", "TRUMAN"]);
                var controls = form.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>()).ToArray();
                var targets = controls.OfType<ComboBox>().Single();
                Assert.AreEqual(ComboBoxStyle.DropDownList, targets.DropDownStyle);
                CollectionAssert.AreEqual(new[] { "GAME", "TRUMAN" }, targets.Items.Cast<string>().ToArray());
                Assert.AreEqual("GAME", targets.SelectedItem);
                Assert.IsFalse(string.IsNullOrWhiteSpace(targets.AccessibleName));
                Assert.AreSame(targets, form.ActiveControl);
                Assert.AreEqual(DialogResult.Cancel, ((Button)form.CancelButton!).DialogResult);
                Assert.AreEqual(DialogResult.None, ((Button)form.AcceptButton!).DialogResult, "Cancelling the native file picker must keep the target dialog open.");
                Assert.IsFalse(controls.Any(c => c is TextBox or CheckBox), "This flow needs no clipboard or connection settings.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void WithSettings(Action<string> action)
    {
        var original = Setting.Values;
        string folder = Path.Combine(Path.GetTempPath(), "mwb-send-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "synthetic-settings.json");
            PortableSettingsStore.Write(path, JsonSerializer.Serialize(new MouseWithoutBordersSettings(), SettingsUtils.SerializerOptions));
            Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
            action(folder);
        }
        finally
        {
            Setting.Values.SaveSettingsSynchronously();
            Setting.Values = original;
            Directory.Delete(folder, true);
        }
    }
}
