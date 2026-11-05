using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class ReceiverDedupTests
{
    [TestMethod]
    public void DifferentPeersCanReuseNumbersWhileDuplicateSocketsAreIgnored()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var recent = (Array)typeof(Receiver).GetField("RecentProcessedPackageIDs", flags)!.GetValue(null)!;
        var savedRecent = (Array)recent.Clone();
        var index = typeof(Receiver).GetField("recentProcessedPackageIndex", flags)!;
        object savedIndex = index.GetValue(null)!;
        var savedMonitor = Package.PackageReceived;
        long savedProcessed = Receiver.processedPackageCount, savedSkipped = Receiver.skippedPackageCount;
        try
        {
            Array.Clear(recent);
            index.SetValue(null, 0);
            Package.PackageReceived = default;
            using var first = new TcpSk(true, null!, SocketStatus.Connected, "FIRST") { MachineId = 101 };
            using var duplicate = new TcpSk(false, null!, SocketStatus.Connected, "FIRST") { MachineId = 101 };
            using var second = new TcpSk(true, null!, SocketStatus.Connected, "SECOND") { MachineId = 202 };
            var packet = new DATA { Type = PackageType.Hi, Id = 51, Src = (ID)1 };
            Receiver.ProcessPackage(packet, first);
            Receiver.ProcessPackage(packet, second);
            Assert.AreEqual(2UL, Package.PackageReceived.Hello, "Another authenticated PC's same number is a distinct packet.");
            Receiver.ProcessPackage(packet, duplicate);
            Assert.AreEqual(2UL, Package.PackageReceived.Hello, "The other socket for the same PC must not deliver a second copy.");

            var preprocess = typeof(Receiver).GetMethod("PreProcess", flags)!;
            bool Accept(DATA data, uint sender) => (bool)preprocess.Invoke(null, new object[] { data, sender })!;
            for (int slot = 1; slot <= 4; slot++)
            {
                // Matrix Src identifies a slot, so it cannot identify the sending PC.
                var matrix = new DATA { Type = PackageType.Matrix, Id = 100 + slot, Src = (ID)slot };
                Assert.IsTrue(Accept(matrix, first.MachineId));
                Assert.IsTrue(Accept(matrix, second.MachineId), $"Peer two's matrix slot {slot} must reach its update handler.");
                Assert.IsFalse(Accept(matrix, duplicate.MachineId));
            }
            foreach (var type in new[] { PackageType.ClipboardText, PackageType.ClipboardImage, PackageType.Handshake, PackageType.HandshakeAck })
            {
                var body = new DATA { Type = type, Id = 51 };
                Assert.IsTrue(Accept(body, first.MachineId));
                Assert.IsTrue(Accept(body, first.MachineId));
            }
        }
        finally
        {
            Array.Copy(savedRecent, recent, recent.Length);
            index.SetValue(null, savedIndex);
            Package.PackageReceived = savedMonitor;
            Receiver.processedPackageCount = savedProcessed;
            Receiver.skippedPackageCount = savedSkipped;
        }
    }
}
