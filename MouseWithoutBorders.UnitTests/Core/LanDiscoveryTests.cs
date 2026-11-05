using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class LanDiscoveryTests
{
    [TestMethod]
    public void AnnouncementContainsOnlyProtocolPortAndComputerName()
    {
        byte[] packet = LanDiscovery.Announcement("OFFICE-PC", 15100);
        Assert.AreEqual("MWB-Portable/1|15100|OFFICE-PC", Encoding.UTF8.GetString(packet));
        Assert.IsTrue(LanDiscovery.TryRead(packet, out string name));
        Assert.AreEqual("OFFICE-PC", name);
        Assert.IsTrue(LanDiscovery.TryRead(LanDiscovery.Announcement("OTHER-PC", 15200), out name),
            "Pairing discovery must not hide a PC merely because its current TCP base port differs.");
        Assert.AreEqual("OTHER-PC", name);
    }

    [TestMethod]
    public void InvalidAdvertisementsNeverBecomeConnectionCandidates()
    {
        foreach (string packet in new[] { "", "OTHER|15100|PC", "MWB-Portable/1|15100|", "MWB-Portable/1|15100|PC|extra",
            "MWB-Portable/1|15100|bad/name", "MWB-Portable/1|15100|PC\nforged",
            "MWB-Portable/1|0|PC", "MWB-Portable/1|65535|PC", "MWB-Portable/1|-1|PC",
            "MWB-Portable/1|not-a-port|PC", "MWB-Portable/1||PC" })
            Assert.IsFalse(LanDiscovery.TryRead(Encoding.UTF8.GetBytes(packet), out _), packet);
        Assert.IsFalse(LanDiscovery.TryRead(new byte[257], out _));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(65534)]
    public void AdvertisedPortsMustLeaveRoomForTheInputPort(int port)
        => Assert.IsTrue(LanDiscovery.TryRead(LanDiscovery.Announcement("PC", port), out _));
}
