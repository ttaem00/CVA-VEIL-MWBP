using System.Drawing;
using System.Reflection;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class MonitorTopologyTests
{
    private readonly FieldInfo state = typeof(MonitorTopology).GetField("state", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object originalState = null!;
    private MouseWithoutBorders.Class.Settings originalSettings = null!;
    private readonly List<MouseWithoutBorders.Class.Settings> settings = new();
    private string folder = null!;

    [TestInitialize]
    public void Setup()
    {
        originalState = state.GetValue(null)!; originalSettings = Setting.Values;
        state.SetValue(null, Activator.CreateInstance(state.FieldType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [new Dictionary<uint, MonitorDescriptor[]>(), Array.Empty<MonitorOrigin>(), Array.Empty<uint>(), 0u, new MonitorLayout([])], null));
        folder = Path.Combine(Path.GetTempPath(), "mwb-monitors-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        Setting.Values = Create("A", 101);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var item in settings) item.SaveSettingsSynchronously();
        Setting.Values = originalSettings; state.SetValue(null, originalState);
        Directory.Delete(folder, true);
    }

    private MouseWithoutBorders.Class.Settings Create(string name, int id)
    {
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "synthetic-monitor-key";
        document.Properties.MachineID.Value = id;
        document.Properties.MachineMatrixString = ["A", "B", "C", ""];
        string path = Path.Combine(folder, name + ".json");
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        var item = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false); settings.Add(item); return item;
    }

    [TestMethod]
    public void StaleSharedRevisionStillUpdatesOnlyTheAuthenticatedPeersGeometry()
    {
        var receiver = Setting.Values;
        receiver.SetMonitorOrigins([new(101, "A1", 0, 0)]);
        var source = Create("B", 202);
        var snapshot = source.SharedSnapshot();
        // A forged JSON peer property cannot change ownership: the descriptor has no such field.
        snapshot.Displays = JsonSerializer.Deserialize<MonitorDescriptor[]>("""
            [{"DisplayId":"B1","X":-1920,"Y":0,"Width":1920,"Height":1080,"Peer":999}]
            """)!;
        using var tcp = new TcpSk(false, null!, SocketStatus.Connected, "B") { MachineId = 202, ReceivedSharedSettings = snapshot };
        long revision = receiver.SharedSnapshot().Revision; receiver.Changed = false;
        Receiver.ProcessPackage(new DATA { Type = PackageType.SharedSettings, Src = (ID)999 }, tcp);
        var monitor = MonitorTopology.Monitors.Single();
        Assert.AreEqual(new MonitorKey(202, "B1"), monitor.Key);
        Assert.AreEqual(new Rectangle(-1920, 0, 1920, 1080), monitor.NativeBounds);
        Assert.AreEqual(revision, receiver.SharedSnapshot().Revision); Assert.IsFalse(receiver.Changed);
        snapshot.Displays = [new("B1", -2560, 0, 2560, 1440)]; tcp.ReceivedSharedSettings = snapshot;
        Receiver.ProcessPackage(new DATA { Type = PackageType.SharedSettings }, tcp);
        Assert.AreEqual(2560, MonitorTopology.Monitors.Single().NativeBounds.Width);
        Assert.AreEqual(revision, receiver.SharedSnapshot().Revision);
    }

    [DataTestMethod]
    [DataRow("version")]
    [DataRow("missing")]
    [DataRow("type")]
    public void InvalidSharedPreferencesDoNotDiscardIndependentMetadataOrTheInputConnection(string invalid)
    {
        var snapshot = Setting.Values.SharedSnapshot();
        snapshot.Displays = [new("B1", 0, 0, 100, 100)];
        if (invalid == "version") snapshot.Version = 999;
        else if (invalid == "missing") snapshot.Values.Remove("ShareClipboard");
        else snapshot.Values["MonitorOrigins"] = JsonSerializer.SerializeToElement("not an origin list");
        using var tcp = new TcpSk(false, null!, SocketStatus.Connected, "B") { MachineId = 202, ReceivedSharedSettings = snapshot };
        var before = Setting.Values.SharedSnapshot();
        Receiver.ProcessPackage(new DATA { Type = PackageType.SharedSettings }, tcp);
        Assert.AreEqual(new MonitorKey(202, "B1"), MonitorTopology.Monitors.Single().Key);
        Assert.AreEqual(SocketStatus.Connected, tcp.Status);
        Assert.AreEqual(before.Revision, Setting.Values.SharedSnapshot().Revision);
        Assert.AreEqual(before.Writer, Setting.Values.SharedSnapshot().Writer);
    }

    [TestMethod]
    public void DefaultsPreserveNativeOffsetsAndPutPeerGroupsInSeparateWorldAreas()
    {
        MonitorTopology.AcceptPeer(101, [new("A1", 0, 0, 100, 100), new("A2", -10, -100, 100, 100)]);
        MonitorTopology.AcceptPeer(202, [new("B1", 0, 0, 100, 100)]);
        var a = MonitorTopology.Monitors.Single(item => item.Key.DisplayId == "A1");
        var b = MonitorTopology.Monitors.Single(item => item.Key.DisplayId == "A2");
        Assert.AreEqual(new Point(-10, -100), new Point(b.WorldOrigin.X - a.WorldOrigin.X, b.WorldOrigin.Y - a.WorldOrigin.Y));
        var remote = MonitorTopology.Monitors.Single(item => item.Key.Peer == 202);
        Assert.IsTrue(remote.WorldBounds.Left >= Math.Max(a.WorldBounds.Right, b.WorldBounds.Right));
        Assert.AreEqual(0, a.WorldOrigin.Y, "Upper native monitors must not move the primary below the other PCs.");
        Assert.AreEqual(remote.Key, MonitorTopology.Layout.Route(a.Key, new(98, 50), new(99, 50)).Monitor.Key,
            "A fresh primary screen must reach the neighbouring PC at its right edge.");
        _ = new MonitorLayout(MonitorTopology.Monitors);
        var unchanged = MonitorTopology.Layout;
        MonitorTopology.AcceptPeer(202, [new("B1", 0, 0, 100, 100)]);
        Assert.AreSame(unchanged, MonitorTopology.Layout, "Repeated metadata must not reset an in-progress editor draft.");
        MonitorTopology.AcceptPeer(202, null!); Assert.AreSame(unchanged, MonitorTopology.Layout);
    }

    [TestMethod]
    public void OriginsAreSharedPersistentAndMergeOfflineKeysWithoutSharingNativeMetadata()
    {
        var current = Setting.Values;
        current.SetMonitorOrigins([new(101, "A1", 0, 0), new(202, "offline", 100, 0)]);
        long revision = current.SharedSnapshot().Revision;
        current.SetMonitorOrigins([new(101, "A1", -100, 20)]);
        CollectionAssert.AreEquivalent(new MonitorOrigin[] { new(101, "A1", -100, 20), new(202, "offline", 100, 0) }, current.MonitorOrigins);
        Assert.AreEqual(revision + 1, current.SharedSnapshot().Revision);
        current.SetMonitorOrigins([new(101, "A1", -100, 20)]);
        Assert.AreEqual(revision + 1, current.SharedSnapshot().Revision, "Reapplying the same editor coordinates is not a new edit.");
        current.SaveSettingsSynchronously();
        var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(Path.Combine(folder, "A.json")), false); settings.Add(reloaded);
        CollectionAssert.AreEqual(current.MonitorOrigins, reloaded.MonitorOrigins);
        var peer = Create("B", 202);
        Assert.IsTrue(peer.AcceptSharedSettings(current.SharedSnapshot()));
        CollectionAssert.AreEqual(current.MonitorOrigins, peer.MonitorOrigins);
        Assert.IsNull(current.SharedSnapshot().Displays, "Only the authenticated sender attaches its own current metadata.");
        Assert.IsFalse(current.SharedSnapshot().Values.ContainsKey("Displays"));
    }

    [TestMethod]
    public void ChangedGeometryRepairsOnlyConflictsWithoutRewritingTheSavedOrigins()
    {
        foreach (uint peer in new uint[] { 101, 202, 303 }) MonitorTopology.AcceptPeer(peer, [new("one", 0, 0, 100, 100)]);
        Setting.Values.SetMonitorOrigins([new(101, "one", 0, 0), new(202, "one", 100, 0), new(303, "one", 200, 0)]);
        var saved = Setting.Values.MonitorOrigins;
        long revision = Setting.Values.SharedSnapshot().Revision;
        MonitorTopology.AcceptPeer(202, [new("one", 0, 0, 150, 100)]);
        CollectionAssert.AreEqual(saved, Setting.Values.MonitorOrigins);
        Assert.AreEqual(revision, Setting.Values.SharedSnapshot().Revision);
        Assert.AreEqual(new Point(0, 0), MonitorTopology.Monitors.Single(item => item.Key.Peer == 101).WorldOrigin);
        Assert.AreEqual(150, MonitorTopology.Monitors.Single(item => item.Key.Peer == 202).NativeBounds.Width);
        _ = new MonitorLayout(MonitorTopology.Monitors);
    }

    [TestMethod]
    public void InvalidNativeGeometryIsIgnoredAtItsOwnerIncludingOverflowingDesktopUnions()
    {
        MonitorTopology.AcceptPeer(202, [new("B1", 0, 0, 100, 100)]);
        var valid = MonitorTopology.Layout;
        foreach (var invalid in new MonitorDescriptor[][]
        {
            [new("B1", 0, 0, 0, 100)],
            [new("B1", int.MaxValue, 0, 100, 100)],
            [new("B1", 0, 0, 100, 100), new("B1", 100, 0, 100, 100)],
            [new("B1", int.MinValue, 0, 100, 100), new("B2", 0, 0, 100, 100)],
        })
        {
            MonitorTopology.AcceptPeer(202, invalid);
            Assert.AreSame(valid, MonitorTopology.Layout);
        }
        Assert.ThrowsException<InvalidDataException>(() => MonitorTopology.ApplyOrigins([new(202, "B1", 0, 0), new(202, "B1", 100, 0)]));
        Assert.AreSame(valid, MonitorTopology.Layout);
    }

    [TestMethod]
    public void MalformedBoundedJsonConsumesItsFrameAndLeavesTheNextFrameReadable()
    {
        byte[] valid = SharedSettingsFrame.Encode(Setting.Values.SharedSnapshot());
        using var stream = new MemoryStream();
        stream.Write(new byte[32]); stream.Write(valid); stream.Write(new byte[SharedSettingsFrame.PaddedLength(valid.Length) - valid.Length]);
        stream.Position = 0;
        Assert.IsNull(SharedSettingsFrame.Read(stream, 1, true)); Assert.AreEqual(32L, stream.Position);
        Assert.IsNotNull(SharedSettingsFrame.Read(stream, valid.Length, true)); Assert.AreEqual(stream.Length, stream.Position);
        stream.Position = 0;
        Assert.ThrowsException<InvalidDataException>(() => SharedSettingsFrame.Read(stream, 1, false));
        Assert.AreEqual(0L, stream.Position, "Unauthenticated frames are still rejected before body consumption.");
    }
}
