using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class InputBacklogTests
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly MethodInfo PreProcess = typeof(Receiver).GetMethod("PreProcess", StaticPrivate)!;
    private Array previousRecent = null!;
    private readonly Dictionary<FieldInfo, object?> previousCounters = new();

    [TestInitialize]
    public void Setup()
    {
        var recent = (Array)typeof(Receiver).GetField("RecentProcessedPackageIDs", StaticPrivate)!.GetValue(null)!;
        previousRecent = (Array)recent.Clone(); Array.Clear(recent);
        foreach (string name in new[] { "recentProcessedPackageIndex", "processedPackageCount", "skippedPackageCount" })
        {
            var field = typeof(Receiver).GetField(name, StaticPrivate)!;
            previousCounters.Add(field, field.GetValue(null));
            field.SetValue(null, Activator.CreateInstance(field.FieldType));
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        previousRecent.CopyTo((Array)typeof(Receiver).GetField("RecentProcessedPackageIDs", StaticPrivate)!.GetValue(null)!, 0);
        foreach (var item in previousCounters) item.Key.SetValue(null, item.Value);
    }

    private static bool Accept(DATA packet) => (bool)PreProcess.Invoke(null, [packet, 202u])!;
    private static DATA Move(int id, int x = 1000) => new()
    {
        Type = PackageType.Mouse, Id = id, Src = (ID)202, Des = (ID)101,
        Md = new MOUSEDATA { X = x, Y = 2000, dwFlags = WM.WM_MOUSEMOVE },
    };

    private static DATA ReadAndCollapse(Wire wire)
    {
        DATA first = SocketStuff.TcpReceiveData(wire.Tcp, out int count);
        Assert.AreEqual(32, count); Assert.IsTrue(Accept(first));
        return Receiver.CoalesceMouseMoves(first, wire.Tcp);
    }

    [TestMethod]
    public void AlreadyBufferedAbsoluteMovesUseTheLatestPositionAndRememberEveryAcceptedId()
    {
        using var wire = new Wire(); wire.Send(Move(11), Move(12, 3000), Move(13, 4000));
        var latest = ReadAndCollapse(wire);
        Assert.AreEqual(13, latest.Id); Assert.AreEqual(4000, latest.Md.X);
        foreach (int id in new[] { 11, 12, 13 }) Assert.IsFalse(Accept(Move(id)), "Discarded positions must still participate in existing duplicate suppression.");
    }

    [DataTestMethod]
    [DataRow("key")]
    [DataRow("button")]
    [DataRow("wheel")]
    [DataRow("control")]
    [DataRow("relative")]
    [DataRow("source")]
    [DataRow("destination")]
    [DataRow("negative")]
    [DataRow("overflow")]
    [DataRow("invalid")]
    public void NonMatchingFramesRemainOrderedAndDoNotHideTheMoveBeforeThem(string kind)
    {
        DATA barrier = Move(22);
        switch (kind)
        {
            case "key": barrier.Type = PackageType.Keyboard; barrier.Kd = new KEYBDDATA { wVk = 65 }; break;
            case "button": barrier.Md.dwFlags = WM.WM_LBUTTONUP; break;
            case "wheel": barrier.Md.dwFlags = WM.WM_MOUSEWHEEL; barrier.Md.WheelDelta = 120; break;
            case "control": barrier.Type = PackageType.ClipboardDragDropOperation; break;
            case "relative": barrier.Md.X = barrier.Md.Y = Event.MOVE_MOUSE_RELATIVE + 5; break;
            case "source": barrier.Src = (ID)303; break;
            case "destination": barrier.Des = (ID)303; break;
            case "negative": barrier.Md.X = -1; break;
            case "overflow": barrier.Md.Y = 65536; break;
            case "invalid": barrier.Type = PackageType.Invalid; break;
        }
        using var wire = new Wire(); wire.Send(Move(21), barrier, Move(23));
        Assert.AreEqual(21, ReadAndCollapse(wire).Id);
        DATA next = SocketStuff.TcpReceiveData(wire.Tcp, out int count);
        Assert.AreEqual(32, count); Assert.AreEqual(barrier.Type, next.Type); Assert.AreEqual(22, next.Id);
        Assert.AreEqual(barrier.Src, next.Src); Assert.AreEqual(barrier.Des, next.Des);
        Assert.AreEqual(23, SocketStuff.TcpReceiveData(wire.Tcp, out _).Id);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void APartialBigOrSharedSettingsFrameCannotDelayAnAlreadyCompleteMove(bool shared)
    {
        var barrier = new DATA { Type = shared ? PackageType.SharedSettings : PackageType.Heartbeat,
            Id = 32, Src = (ID)202, Des = (ID)101, Machine1 = (ID)2 };
        byte[] complete = Encode(barrier);
        using var wire = new Wire();
        wire.SendBytes(Encode(Move(31)).Concat(complete.Take(32)).ToArray());
        Assert.AreEqual(31, ReadAndCollapse(wire).Id, "Only the next header is available; waiting for its body would delay input.");
        Assert.IsNull(wire.Tcp.ReceivedSharedSettings, "Looking ahead must not apply a settings frame early.");
        wire.SendBytes(shared ? new byte[] { (byte)'{', (byte)'}' }.Concat(new byte[30]).ToArray() : complete[32..]);
        DATA next = SocketStuff.TcpReceiveData(wire.Tcp, out _);
        Assert.AreEqual(barrier.Type, next.Type); Assert.AreEqual(32, next.Id);
        if (shared) Assert.IsNotNull(wire.Tcp.ReceivedSharedSettings);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnAlreadyProcessedLookaheadCannotEraseAValidPosition(bool laterValidMove)
    {
        Assert.IsTrue(Accept(Move(40)));
        using var wire = new Wire();
        wire.Send(laterValidMove ? [Move(41), Move(40), Move(42)] : [Move(41), Move(40)]);
        Assert.AreEqual(laterValidMove ? 42 : 41, ReadAndCollapse(wire).Id);
        Assert.IsFalse(Accept(Move(41)));
    }

    [TestMethod]
    public void AHandshakeConnectionDoesNotConsumeLookahead()
    {
        using var wire = new Wire(); wire.Tcp.Status = SocketStatus.Handshaking;
        wire.Send(Move(51), Move(52));
        Assert.AreEqual(51, ReadAndCollapse(wire).Id);
        Assert.AreEqual(52, SocketStuff.TcpReceiveData(wire.Tcp, out _).Id);
    }

    [DataTestMethod]
    [DataRow(10054, true)]
    [DataRow(10060, false)]
    public void ReadAheadNetworkErrorsRetainTheExistingReconnectPolicy(int error, bool reconnect)
    {
        int previous = InitAndCleanup.PleaseReopenSocket;
        var owner = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
        var flag = typeof(SocketStuff).GetMethod("FlagReopenSocketIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (Exception failure in new Exception[] { new SocketException(error), new IOException("synthetic", new SocketException(error)) })
            {
                InitAndCleanup.PleaseReopenSocket = 0;
                flag.Invoke(owner, [failure]);
                Assert.AreEqual(reconnect ? InitAndCleanup.REOPEN_WHEN_WSAECONNRESET : 0, InitAndCleanup.PleaseReopenSocket);
            }
        }
        finally { InitAndCleanup.PleaseReopenSocket = previous; }
    }

    private static byte[] Encode(DATA packet)
    {
        byte[] bytes = packet.Bytes;
        bytes[3] = (byte)(Encryption.MagicNumber >> 24); bytes[2] = (byte)(Encryption.MagicNumber >> 16); bytes[1] = 0;
        for (int i = 2; i < 32; i++) bytes[1] = unchecked((byte)(bytes[1] + bytes[i]));
        return bytes;
    }

    // The production parser sees a real socket and CBC/Zeros stream without touching any profile or shared secret.
    private sealed class Wire : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly TcpClient sender = new(), receiver;
        private readonly Aes aes = Aes.Create();
        private readonly CryptoStream encrypted;
        internal readonly TcpSk Tcp;

        internal Wire()
        {
            listener.Start(); sender.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            receiver = listener.AcceptTcpClient(); receiver.ReceiveTimeout = 1000;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.Zeros;
            encrypted = new CryptoStream(sender.GetStream(), aes.CreateEncryptor(), CryptoStreamMode.Write, true);
            var decrypted = new CryptoStream(receiver.GetStream(), aes.CreateDecryptor(), CryptoStreamMode.Read, true);
            Tcp = new TcpSk(false, receiver.Client, SocketStatus.Connected, "synthetic-source") { MachineId = 202 };
            typeof(TcpSk).GetField("decryptedStream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Tcp, decrypted);
        }

        internal void Send(params DATA[] packets) => SendBytes(packets.SelectMany(Encode).ToArray());
        internal void SendBytes(byte[] bytes)
        {
            int before = receiver.Available; encrypted.Write(bytes); encrypted.Flush();
            Assert.IsTrue(SpinWait.SpinUntil(() => receiver.Available >= before + bytes.Length, TimeSpan.FromSeconds(3)), "The synthetic encrypted frames did not reach the loopback receive buffer.");
        }

        public void Dispose() { encrypted.Dispose(); Tcp.Dispose(); sender.Dispose(); receiver.Dispose(); listener.Stop(); aes.Dispose(); }
    }
}
