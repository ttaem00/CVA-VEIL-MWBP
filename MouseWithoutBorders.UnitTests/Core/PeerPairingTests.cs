using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PeerPairingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ApprovedConnectionPreservesTheExistingGroupInEitherDirection(bool ownerInitiates)
    {
        using var owner = new Node("MAIN", true);
        using var joiner = new Node("GAME", false);
        owner.BeforeExport = () => Assert.IsTrue(owner.Approval.Task.IsCompletedSuccessfully && owner.Approval.Task.Result
            && joiner.Approval.Task.IsCompletedSuccessfully && joiner.Approval.Task.Result, "Neither approval may be inferred from TLS or the first Connect click.");
        Task connecting = ownerInitiates ? owner.Service.ConnectAsync(joiner.Service.Endpoint) : joiner.Service.ConnectAsync(owner.Service.Endpoint);
        var shown = Task.WhenAll(owner.Request.Task, joiner.Request.Task);
        if (await Task.WhenAny(shown, connecting).WaitAsync(Deadline) == connecting) await connecting;
        await shown.WaitAsync(Deadline);
        Assert.AreEqual(owner.Request.Task.Result.Code, joiner.Request.Task.Result.Code);
        Assert.AreEqual(6, owner.Request.Task.Result.Code.Length);
        Assert.IsTrue(owner.Request.Task.Result.Code.All(char.IsAsciiDigit));
        Assert.AreEqual(0, owner.Exported); Assert.IsNull(joiner.Adopted);
        owner.Approval.SetResult(true); joiner.Approval.SetResult(true);
        await connecting.WaitAsync(Deadline);
        await Task.WhenAll(owner.Done.Task, joiner.Done.Task).WaitAsync(Deadline);
        Assert.AreEqual(1, owner.Exported); Assert.AreEqual(0, joiner.Exported);
        Assert.IsNull(owner.Adopted); Assert.AreEqual("synthetic-MAIN-key", joiner.Adopted!.Key);
        Assert.AreEqual(15200, joiner.Adopted.Port); Assert.IsFalse(owner.Done.Task.Result); Assert.IsTrue(joiner.Done.Task.Result);
    }

    [TestMethod]
    public async Task TwoFreshComputersUseTheInitiatorsGroup()
    {
        using var first = new Node("FIRST", false);
        using var second = new Node("SECOND", false);
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        first.Approval.SetResult(true); second.Approval.SetResult(true);
        await connecting.WaitAsync(Deadline);
        await Task.WhenAll(first.Done.Task, second.Done.Task).WaitAsync(Deadline);
        Assert.AreEqual(1, first.Exported); Assert.AreEqual(0, second.Exported);
        Assert.AreEqual("synthetic-FIRST-key", second.Adopted!.Key);
    }

    [TestMethod]
    public async Task RemoteDeclineCancelsTheOtherConfirmationWithoutExportingAKey()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("GAME", false);
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        second.Approval.SetResult(false);
        await Failure(connecting);
        await first.Cancelled.Task.WaitAsync(Deadline);
        Assert.AreEqual(0, first.Exported); Assert.AreEqual(0, second.Exported);
        Assert.IsNull(first.Adopted); Assert.IsNull(second.Adopted);
        Assert.IsFalse(first.Done.Task.IsCompleted); Assert.IsFalse(second.Done.Task.IsCompleted);
    }

    [TestMethod]
    public async Task CancellingTheInitiatorClosesBothPendingConfirmations()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("GAME", false);
        using var cancellation = new CancellationTokenSource();
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint, cancellation.Token);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        cancellation.Cancel();
        await Failure(connecting);
        await Task.WhenAll(first.Cancelled.Task, second.Cancelled.Task).WaitAsync(Deadline);
        Assert.AreEqual(0, first.Exported); Assert.IsNull(second.Adopted);
    }

    [TestMethod]
    public async Task OnePendingRequestCoversBothInboundAndOutboundConnections()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("GAME", false);
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        Assert.IsInstanceOfType<InvalidOperationException>(await Failure(second.Service.ConnectAsync(first.Service.Endpoint)));
        Assert.IsInstanceOfType<InvalidOperationException>(await Failure(first.Service.ConnectAsync(second.Service.Endpoint)));
        Assert.AreEqual(0, first.Exported);
        first.Approval.SetResult(true); second.Approval.SetResult(true);
        await connecting.WaitAsync(Deadline);
        await Task.WhenAll(first.Done.Task, second.Done.Task).WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task TwoExistingGroupsCannotSilentlyReplaceEachOthersKeys()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("OTHER", true);
        Assert.IsInstanceOfType<InvalidOperationException>(await Failure(first.Service.ConnectAsync(second.Service.Endpoint)));
        Assert.IsFalse(first.Request.Task.IsCompleted); Assert.IsFalse(second.Request.Task.IsCompleted);
        Assert.AreEqual(0, first.Exported); Assert.AreEqual(0, second.Exported);
        Assert.IsNull(first.Adopted); Assert.IsNull(second.Adopted);
    }

    [TestMethod]
    [DataRow("MAIN")]
    [DataRow("main")]
    [DataRow("MaIn")]
    public async Task EqualComputerNamesAreRejectedBeforeConfirmationOrKeyExport(string peerName)
    {
        using var first = new Node("MAIN", true);
        using var second = new Node(peerName, false);
        await Failure(first.Service.ConnectAsync(second.Service.Endpoint));
        Assert.IsFalse(first.Request.Task.IsCompleted); Assert.IsFalse(second.Request.Task.IsCompleted);
        Assert.AreEqual(0, first.Exported); Assert.AreEqual(0, second.Exported);
        Assert.IsNull(first.Adopted); Assert.IsNull(second.Adopted);
        Assert.IsFalse(first.Done.Task.IsCompleted); Assert.IsFalse(second.Done.Task.IsCompleted);
    }

    [TestMethod]
    public async Task AComputerThatJoinsAnotherGroupDuringApprovalCannotBeOverwritten()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("GAME", false);
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        second.HasGroup = true;
        first.Approval.SetResult(true); second.Approval.SetResult(true);
        await Failure(connecting);
        Assert.IsNull(second.Adopted); Assert.IsFalse(second.Done.Task.IsCompleted);
    }

    [TestMethod]
    public async Task AFailedGroupSaveDoesNotSendSuccessOrExposeItsErrorMessage()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("GAME", false);
        second.BeforeAdopt = _ => throw new IOException("synthetic confidential storage detail");
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        first.Approval.SetResult(true); second.Approval.SetResult(true);
        await Failure(connecting);
        string failure = await second.Failed.Task.WaitAsync(Deadline);
        Assert.IsFalse(failure.Contains("confidential")); Assert.IsNull(second.Adopted);
        Assert.IsFalse(first.Done.Task.IsCompleted); Assert.IsFalse(second.Done.Task.IsCompleted);
    }

    [TestMethod]
    public async Task DisposeDoesNotWaitForUnansweredUserConfirmation()
    {
        using var first = new Node("MAIN", true);
        using var second = new Node("GAME", false);
        Task connecting = first.Service.ConnectAsync(second.Service.Endpoint);
        await Task.WhenAll(first.Request.Task, second.Request.Task).WaitAsync(Deadline);
        await Task.Run(first.Service.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        await Failure(connecting);
        await Task.WhenAll(first.Cancelled.Task, second.Cancelled.Task).WaitAsync(Deadline);
        Assert.AreEqual(0, first.Exported); Assert.IsNull(second.Adopted);
    }

    [TestMethod]
    public void VerificationCodeBindsTheCertificateAndBothCommittedHellos()
    {
        byte[] certificate = Encoding.ASCII.GetBytes("server certificate");
        byte[] client = Encoding.ASCII.GetBytes("client hello"), server = Encoding.ASCII.GetBytes("server hello");
        string code = PeerPairing.VerificationCode(certificate, client, server);
        Assert.AreEqual("607541", code);
        Assert.AreNotEqual(code, PeerPairing.VerificationCode(Encoding.ASCII.GetBytes("MITM certificate"), client, server));
        Assert.AreNotEqual(code, PeerPairing.VerificationCode(certificate, Encoding.ASCII.GetBytes("changed client policy"), server));
        Assert.AreNotEqual(code, PeerPairing.VerificationCode(certificate, client, Encoding.ASCII.GetBytes("changed server identity")));
    }

    [TestMethod]
    [DataRow("ChangedHello")]
    [DataRow("InvalidIdentity")]
    [DataRow("OversizedFrame")]
    public async Task UnapprovedMalformedRequestsNeverReachConfirmationOrGroupExport(string attack)
    {
        using var target = new Node("MAIN", true);
        using var client = new TcpClient();
        await client.ConnectAsync(target.Service.Endpoint);
        using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "MWBP test", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }).WaitAsync(Deadline);
        if (attack == "OversizedFrame")
        {
            byte[] oversized = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(oversized, SharedSettingsFrame.MaximumBytes * 2 + 1);
            await tls.WriteAsync(oversized); await tls.FlushAsync();
            Assert.AreEqual(32, (await ReadPacket(tls)).Length); // The server already sent its public commitment.
        }
        else
        {
            byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = 1, Name = attack == "InvalidIdentity" ? "MAIN\ntrust me" : "GAME", HasGroup = false,
                Nonce = RandomNumberGenerator.GetBytes(32),
            });
            await WritePacket(tls, attack == "ChangedHello" ? new byte[32] : SHA256.HashData(hello));
            Assert.AreEqual(32, (await ReadPacket(tls)).Length);
            await WritePacket(tls, hello);
            _ = await ReadPacket(tls); // The server's public reveal contains no key.
        }
        try { Assert.AreEqual(0, await tls.ReadAsync(new byte[1], 0, 1).WaitAsync(Deadline)); }
        catch (IOException) { }
        Assert.IsFalse(target.Request.Task.IsCompleted); Assert.AreEqual(0, target.Exported);
        Assert.IsNull(target.Adopted); Assert.IsFalse(target.Done.Task.IsCompleted);
    }

    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException or InvalidOperationException) { return error; }
        Assert.Fail("The connection should not have succeeded.");
        throw new InvalidOperationException();
    }

    private static async Task WritePacket(Stream stream, byte[] body)
    {
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        await stream.WriteAsync(length); await stream.WriteAsync(body); await stream.FlushAsync();
    }
    private static async Task<byte[]> ReadPacket(Stream stream)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length).AsTask().WaitAsync(Deadline);
        int size = BinaryPrimitives.ReadInt32BigEndian(length);
        Assert.IsTrue(size is > 0 and <= SharedSettingsFrame.MaximumBytes * 2);
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body).AsTask().WaitAsync(Deadline);
        return body;
    }

    private sealed class Node : IDisposable
    {
        internal bool HasGroup;
        internal int Exported;
        internal PairingGroup? Adopted;
        internal Action? BeforeExport;
        internal Action<PairingGroup>? BeforeAdopt;
        internal readonly TaskCompletionSource<PairingRequest> Request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly PeerPairing Service;
        internal Node(string name, bool hasGroup)
        {
            HasGroup = hasGroup;
            Service = new PeerPairing(name, () => HasGroup, async (request, token) =>
            {
                Request.TrySetResult(request);
                try { return await Approval.Task.WaitAsync(token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(true); throw; }
            }, (peer, address) =>
            {
                BeforeExport?.Invoke(); Interlocked.Increment(ref Exported);
                return new PairingGroup("synthetic-" + name + "-key", 15200, SharedSettingsSnapshot.Capture(new MouseWithoutBordersProperties()));
            }, (peer, address, group) => { BeforeAdopt?.Invoke(group); Adopted = group; HasGroup = true; }, new IPEndPoint(IPAddress.Loopback, 0));
            Service.Paired += (_, _, adopted) => Done.TrySetResult(adopted);
            Service.Failed += error => Failed.TrySetResult(error);
        }
        public void Dispose() => Service.Dispose();
    }
}
