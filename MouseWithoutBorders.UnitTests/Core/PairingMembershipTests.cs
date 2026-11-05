// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class PairingMembershipTests
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private string folder = null!, oldName = null!;
    private SocketStuff oldTransport = null!;

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), "mwb-pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        oldName = Common.MachineName; oldTransport = Common.Sk;
        Common.MachineName = "MAIN"; Common.Sk = null!;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Common.MachineName = oldName; Common.Sk = oldTransport;
        Directory.Delete(folder, true);
    }

    private MouseWithoutBorders.Class.Settings Create(string name, int id, long revision = 4)
    {
        var document = new MouseWithoutBordersSettings { AppMode = PortableApplication.AppModeInstalled };
        document.Properties.SecurityKey.Value = "synthetic-key-for-" + name;
        document.Properties.TCPPort.Value = name == "MAIN" ? 15200 : 15100;
        document.Properties.MachineID.Value = id;
        document.Properties.MachineMatrixString = name == "MAIN" ? ["MAIN", "TRUMAN", "", ""] : [name, "", "", ""];
        document.Properties.Name2IP.Value = "TRUMAN 192.0.2.2\r\nGAME 192.0.2.99";
        document.Properties.DefaultReceivingFolder = Path.Combine(folder, name, "received");
        document.Properties.DrawMouseCursor = name != "MAIN";
        document.Properties.SharedRevision = revision; document.Properties.SharedWriter = (uint)id;
        PortableSettingsStore.Write(PathFor(name), JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        return new MouseWithoutBorders.Class.Settings(new SettingsUtils(PathFor(name)), false);
    }

    private string PathFor(string name) => Path.Combine(folder, name + ".json");
    private static string MemoryDocument(MouseWithoutBorders.Class.Settings settings) => (string)typeof(MouseWithoutBorders.Class.Settings)
        .GetMethod("SnapshotDocument", InstanceFields)!.Invoke(settings, null)!;

    [TestMethod]
    public void ExportPreservesTheExistingGroupAndUpdatesOnlyTheApprovedPeerAddress()
    {
        var authority = Create("MAIN", 101);
        string key = authority.MyKey;
        var group = authority.ExportPairedGroup("GAME", IPAddress.Parse("192.0.2.3"));
        Assert.AreEqual(key, group.Key); Assert.AreEqual(15200, group.Port);
        CollectionAssert.AreEqual(new[] { "MAIN", "TRUMAN", "GAME", "" }, group.Settings.ReadValues().MachineMatrixString);
        Assert.AreEqual(5L, group.Settings.Revision); Assert.AreEqual(101u, group.Settings.Writer);
        Assert.AreEqual("TRUMAN 192.0.2.2" + Environment.NewLine + "GAME 192.0.2.3", authority.Name2IP);
        var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(PathFor("MAIN")), false);
        Assert.AreEqual(key, reloaded.MyKey); Assert.AreEqual(101, reloaded.MachineId);
        Assert.AreEqual(authority.MachineMatrixString, reloaded.MachineMatrixString);
        Assert.IsFalse(reloaded.DrawMouse);
        Assert.AreEqual(PortableApplication.AppModeInstalled, PortableSettingsStore.Read(PathFor("MAIN")).AppMode);
    }

    [TestMethod]
    public void AFullAuthorityMatrixCannotReplaceAnExistingMemberToMakeRoom()
    {
        _ = Create("MAIN", 101);
        var document = PortableSettingsStore.Read(PathFor("MAIN"));
        document.Properties.MachineMatrixString = ["MAIN", "TRUMAN", "THIRD", "FOURTH"];
        PortableSettingsStore.Write(PathFor("MAIN"), JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        var authority = new MouseWithoutBorders.Class.Settings(new SettingsUtils(PathFor("MAIN")), false);
        string before = MemoryDocument(authority);
        Assert.ThrowsException<InvalidOperationException>(() => authority.ExportPairedGroup("GAME", IPAddress.Loopback));
        Assert.AreEqual(before, MemoryDocument(authority));
        Assert.AreEqual(before, File.ReadAllText(PathFor("MAIN")));
    }

    [TestMethod]
    public void JoiningForcesTheApprovedAuthorityDespiteAnOlderRevisionAndKeepsLocalData()
    {
        var authority = Create("MAIN", 101);
        var group = authority.ExportPairedGroup("GAME", IPAddress.Parse("192.0.2.3"));
        var game = Create("GAME", 303, 9000);
        Common.MachineName = "GAME";
        string localFolder = game.DefaultReceivingFolder;
        string journal = Path.Combine(folder, "MouseWithoutBorders.transfers.json");
        File.WriteAllText(journal, "synthetic pending transfers");
        game.AdoptPairedGroup("MAIN", IPAddress.Parse("192.0.2.1"), group);
        var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(PathFor("GAME")), false);
        Assert.AreEqual(group.Key, reloaded.MyKey); Assert.AreEqual(15200, reloaded.TcpPort);
        Assert.AreEqual(303, reloaded.MachineId); Assert.AreEqual(localFolder, reloaded.DefaultReceivingFolder);
        Assert.IsTrue(reloaded.DrawMouse);
        Assert.AreEqual("MAIN,TRUMAN,GAME,", reloaded.MachineMatrixString);
        Assert.AreEqual(group.Settings.Revision, reloaded.SharedSnapshot().Revision);
        Assert.AreEqual(group.Settings.Writer, reloaded.SharedSnapshot().Writer);
        StringAssert.Contains(reloaded.Name2IP, "MAIN 192.0.2.1");
        StringAssert.Contains(reloaded.Name2IP, "TRUMAN 192.0.2.2");
        Assert.AreEqual(PortableApplication.AppModeInstalled, PortableSettingsStore.Read(PathFor("GAME")).AppMode);
        Assert.AreEqual("synthetic pending transfers", File.ReadAllText(journal));
    }

    [TestMethod]
    public void AnAuthenticatedPeerPreventsAdoptingAnotherGroup()
    {
        var authority = Create("MAIN", 101); var game = Create("GAME", 303);
        var group = authority.ExportPairedGroup("GAME", IPAddress.Parse("192.0.2.3"));
        Common.MachineName = "GAME";
        string before = File.ReadAllText(PathFor("GAME"));
        using var peer = new TcpSk(false, null!, SocketStatus.Connected, "EXISTING") { MachineId = 404 };
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        GC.SuppressFinalize(transport);
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer });
        Common.Sk = transport;
        Assert.ThrowsException<InvalidOperationException>(() => game.AdoptPairedGroup("MAIN", IPAddress.Loopback, group));
        Assert.AreEqual(before, MemoryDocument(game));
        Assert.AreEqual(before, File.ReadAllText(PathFor("GAME")));
    }

    [TestMethod]
    public void AuthenticationAndDelayedAdoptionCannotBothClaimTheExistingGroup()
    {
        var authority = Create("MAIN", 101); var game = Create("GAME", 303);
        var group = new PairingGroup(authority.MyKey, authority.TcpPort, authority.SharedSnapshot());
        Common.MachineName = "GAME";
        string before = MemoryDocument(game), disk = File.ReadAllText(PathFor("GAME")), key = game.MyKey;
        using var peer = new TcpSk(false, null!, SocketStatus.Handshaking, "EXISTING") { MachineId = 404 };
        var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        GC.SuppressFinalize(transport);
        transport.TcpSocketsLock = new object();
        typeof(SocketStuff).GetProperty("TcpSockets", InstanceFields)!.SetValue(transport, new List<TcpSk> { peer });
        Common.Sk = transport;
        var settingsLock = (System.Threading.Lock)typeof(MouseWithoutBorders.Class.Settings)
            .GetField("_loadingSettingsLock", InstanceFields)!.GetValue(game)!;
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var adoption = new System.Threading.Thread(() =>
        {
            try { game.AdoptPairedGroup("MAIN", IPAddress.Parse("192.0.2.1"), group); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        bool waiting, authenticated = false;
        using (settingsLock.EnterScope())
        {
            adoption.Start();
            // This dedicated thread has no other blocking work before the held settings lock.
            waiting = SpinWait.SpinUntil(() => (adoption.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3));
            // A transaction may own the existing TCP lock until its commit. Never invert
            // that order in this fixture: authentication then remains pending instead.
            if (waiting && Monitor.TryEnter(transport.TcpSocketsLock))
            {
                try { peer.Status = SocketStatus.Connected; authenticated = true; }
                finally { Monitor.Exit(transport.TcpSocketsLock); }
            }
        }
        Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5)), "The adoption thread must finish after the settings lock is released.");
        adoption.Join();
        Assert.IsTrue(waiting, "The test must place authentication inside the settings-lock wait, rather than before adoption starts.");
        if (!authenticated)
        {
            Assert.IsNull(failure, "Adoption holding the authentication owner may finish before that peer authenticates.");
            Assert.AreEqual(group.Key, game.MyKey);
            return;
        }
        Assert.AreEqual(key, game.MyKey, "An existing authenticated group's key must survive the delayed adoption.");
        Assert.AreEqual(before, MemoryDocument(game));
        Assert.AreEqual(disk, File.ReadAllText(PathFor("GAME")));
        Assert.IsInstanceOfType<InvalidOperationException>(failure);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SaveFailureCannotPublishCandidateMembershipOrReplaceTheOriginalFile(bool joining)
    {
        var authority = Create("MAIN", 101); var game = Create("GAME", 303, 9000);
        var group = new PairingGroup(authority.MyKey, authority.TcpPort, authority.SharedSnapshot());
        var settings = joining ? game : authority;
        string path = PathFor(joining ? "GAME" : "MAIN");
        Common.MachineName = joining ? "GAME" : "MAIN";
        string before = MemoryDocument(settings), disk = File.ReadAllText(path);
        typeof(MouseWithoutBorders.Class.Settings).GetField("_pendingDocument", InstanceFields)!.SetValue(settings, before);
        object? shared = typeof(MouseWithoutBorders.Class.Settings).GetField("sharedValues", InstanceFields)!.GetValue(settings);
        object? pending = typeof(MouseWithoutBorders.Class.Settings).GetField("_pendingDocument", InstanceFields)!.GetValue(settings);
        object? last = typeof(MouseWithoutBorders.Class.Settings).GetField("_lastDocument", InstanceFields)!.GetValue(settings);
        using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsException<IOException>(() =>
            {
                if (joining) settings.AdoptPairedGroup("MAIN", IPAddress.Parse("192.0.2.1"), group);
                else settings.ExportPairedGroup("GAME", IPAddress.Parse("192.0.2.3"));
            });
            Assert.AreEqual(before, MemoryDocument(settings));
            Assert.AreEqual(disk, File.ReadAllText(path));
            Assert.AreEqual(shared, typeof(MouseWithoutBorders.Class.Settings).GetField("sharedValues", InstanceFields)!.GetValue(settings));
            Assert.AreEqual(pending, typeof(MouseWithoutBorders.Class.Settings).GetField("_pendingDocument", InstanceFields)!.GetValue(settings));
            Assert.AreEqual(last, typeof(MouseWithoutBorders.Class.Settings).GetField("_lastDocument", InstanceFields)!.GetValue(settings));
        }
    }

    [TestMethod]
    public void InvalidPortIsRejectedByTheExistingStorageOwnerWithoutChangingTheGroup()
    {
        var authority = Create("MAIN", 101); var game = Create("GAME", 303);
        var invalid = new PairingGroup(authority.MyKey, 65535, authority.SharedSnapshot());
        Common.MachineName = "GAME";
        string before = MemoryDocument(game);
        Assert.ThrowsException<InvalidDataException>(() => game.AdoptPairedGroup("MAIN", IPAddress.Loopback, invalid));
        Assert.AreEqual(before, MemoryDocument(game));
        Assert.AreEqual(before, File.ReadAllText(PathFor("GAME")));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateMonitorOriginsCannotPublishOrPersistPairingMembership(bool joining)
    {
        _ = Create("MAIN", 101);
        var document = PortableSettingsStore.Read(PathFor("MAIN"));
        document.Properties.MonitorOrigins = [new(101, "DISPLAY1", 0, 0), new(101, "DISPLAY1", 1920, 0)];
        PortableSettingsStore.Write(PathFor("MAIN"), JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        var authority = new MouseWithoutBorders.Class.Settings(new SettingsUtils(PathFor("MAIN")), false);
        var game = Create("GAME", 303);
        var group = new PairingGroup(authority.MyKey, authority.TcpPort, authority.SharedSnapshot());
        var settings = joining ? game : authority;
        string path = PathFor(joining ? "GAME" : "MAIN");
        Common.MachineName = joining ? "GAME" : "MAIN";
        string before = MemoryDocument(settings), disk = File.ReadAllText(path);
        bool changed = settings.Changed;
        Assert.ThrowsException<InvalidDataException>(() =>
        {
            if (joining) settings.AdoptPairedGroup("MAIN", IPAddress.Parse("192.0.2.1"), group);
            else settings.ExportPairedGroup("GAME", IPAddress.Parse("192.0.2.3"));
        });
        Assert.AreEqual(before, MemoryDocument(settings));
        Assert.AreEqual(disk, File.ReadAllText(path));
        Assert.AreEqual(changed, settings.Changed);
    }
}
