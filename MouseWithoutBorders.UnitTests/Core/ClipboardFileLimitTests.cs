using System.Reflection;
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
    private Dictionary<FieldInfo, object?> clipboardState = null!;
    private readonly List<MouseWithoutBorders.Class.Settings> settings = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), "mwb-clipboard-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        previousSettings = Setting.Values; previousSockets = Common.Sk;
        previousUserMode = Common.RunWithNoAdminRight; previousDestination = Common.DesMachineID;
        previousIcons = Common.toggleIcons; previousIconIndex = Common.toggleIconsIndex;
        previousTopology = typeof(MonitorTopology).GetField("state", PrivateStatic)!.GetValue(null);
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
        Common.toggleIcons = previousIcons!; Common.toggleIconsIndex = previousIconIndex;
        typeof(MonitorTopology).GetField("state", PrivateStatic)!.SetValue(null, previousTopology);
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
        Assert.IsInstanceOfType<ByteArrayOrString>(typeof(SharedClipboard).GetField("lastClipboardObject", PrivateStatic)!.GetValue(null));

        Setting.Values.LimitAutomaticClipboardFileSize = false;
        typeof(SharedClipboard).GetField("lastClipboardEventTime", PrivateStatic)!.SetValue(null, 0L);
        SharedClipboard.CheckClipboardEx(new ByteArrayOrString(path), true);
        Assert.AreEqual(path, SharedClipboard.LastDragDropFile);
        Assert.IsFalse(SharedClipboard.HasSwitchedMachineSinceLastCopy);
        Assert.AreEqual(314572801L, new FileInfo(path).Length);
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
