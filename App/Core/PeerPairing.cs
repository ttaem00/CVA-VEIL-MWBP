// Copyright (c) Mouse Without Borders Portable contributors.
// Licensed under the MIT license.
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Core;

internal sealed record PairingRequest(string Name, IPAddress Address, string Code);
internal sealed record PairingGroup(string Key, int Port, SharedSettingsSnapshot Settings);

/// <summary>A temporary, human-verified channel; it never authenticates normal input traffic.</summary>
internal sealed class PeerPairing : IDisposable
{
    internal const int Port = 15102;
    private const int MaximumFrame = SharedSettingsFrame.MaximumBytes * 2;
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("MWBP pairing 1\0");
    private sealed record Hello(int Version, string Name, bool HasGroup, byte[] Nonce);
    private readonly string name;
    private readonly Func<bool> hasGroup;
    private readonly Func<PairingRequest, CancellationToken, Task<bool>> confirm;
    private readonly Func<string, IPAddress, PairingGroup> exportGroup;
    private readonly Action<string, IPAddress, PairingGroup> adoptGroup;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim pending = new(1, 1);
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly Task listening;
    private TcpClient active;
    internal event Action<string> Failed;
    internal event Action<string, IPAddress, bool> Paired;
    internal IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

    internal PeerPairing(string localName, Func<bool> hasGroup,
        Func<PairingRequest, CancellationToken, Task<bool>> confirm,
        Func<string, IPAddress, PairingGroup> exportGroup,
        Action<string, IPAddress, PairingGroup> adoptGroup,
        IPEndPoint listenEndpoint = null)
    {
        name = localName; this.hasGroup = hasGroup; this.confirm = confirm;
        this.exportGroup = exportGroup; this.adoptGroup = adoptGroup;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MWBP temporary pairing", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        byte[] privateCertificate = generated.Export(X509ContentType.Pkcs12);
        try
        {
            // Windows Schannel needs a temporary user key container. No trust-store entry or PFX file is created.
            certificate = X509CertificateLoader.LoadPkcs12(privateCertificate, null, X509KeyStorageFlags.UserKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(privateCertificate); }
        listener = new TcpListener(listenEndpoint ?? new IPEndPoint(IPAddress.Any, Port));
        try { listener.Start(); }
        catch { certificate.Dispose(); throw; }
        listening = ListenAsync();
    }

    internal Task ConnectAsync(IPAddress address, CancellationToken token = default) =>
        ConnectAsync(new IPEndPoint(address, Port), token);

    internal async Task ConnectAsync(IPEndPoint endpoint, CancellationToken token = default)
    {
        if (!pending.Wait(0)) throw new InvalidOperationException("Another connection is awaiting approval.");
        try
        {
            using var client = new TcpClient(endpoint.AddressFamily);
            active = client;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(2));
            await client.ConnectAsync(endpoint.Address, endpoint.Port, lifetime.Token).ConfigureAwait(false);
            await ExchangeAsync(client, true, lifetime).ConfigureAwait(false);
        }
        finally { active = null; pending.Release(); }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (!pending.Wait(0)) { client.Dispose(); continue; }
                active = client;
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            lifetime.CancelAfter(TimeSpan.FromMinutes(2));
            try { await ExchangeAsync(client, false, lifetime).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException or SocketException) { }
            catch (Exception error) { Logger.Log("Pairing ended: " + error.GetType().Name); }
            finally { active = null; pending.Release(); }
        }
    }

    private async Task ExchangeAsync(TcpClient client, bool initiator, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        var address = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
        using var tls = new SslStream(client.GetStream(), false,
            // This temporary certificate is verified by the committed, human-compared code below.
            initiator ? (_, _, _, _) => true : null);
        bool approved = false;
        try
        {
            if (initiator)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "MWBP temporary pairing", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck },
                }, token).ConfigureAwait(false);
            else
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, token).ConfigureAwait(false);

            byte[] local = JsonSerializer.SerializeToUtf8Bytes(new Hello(1, name, hasGroup(), RandomNumberGenerator.GetBytes(32)));
            await WriteFrame(tls, SHA256.HashData(local), token).ConfigureAwait(false);
            byte[] commitment = await ReadFrame(tls, token).ConfigureAwait(false);
            if (commitment.Length != 32) throw new InvalidDataException("Invalid pairing commitment.");
            await WriteFrame(tls, local, token).ConfigureAwait(false);
            byte[] remote = await ReadFrame(tls, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(commitment, SHA256.HashData(remote)))
                throw new InvalidDataException("The pairing commitment changed.");
            Hello own = ReadHello(local), peer = ReadHello(remote);
            if (own.Name.Equals(peer.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The two PCs have the same name. Rename one PC in Windows before connecting.");
            if (own.HasGroup && peer.HasGroup) throw new InvalidOperationException("Both PCs already belong to connected groups.");
            byte[] serverCertificate = initiator ? tls.RemoteCertificate.GetRawCertData() : certificate.RawData;
            string code = VerificationCode(serverCertificate, initiator ? local : remote, initiator ? remote : local);

            var localApproval = confirm(new PairingRequest(peer.Name, address, code), token).WaitAsync(token);
            var remoteApproval = ReadFlag(tls, token);
            if (await Task.WhenAny(localApproval, remoteApproval).ConfigureAwait(false) == remoteApproval
                && !await remoteApproval.ConfigureAwait(false))
                throw new OperationCanceledException("The other PC declined the connection.");
            approved = await localApproval.ConfigureAwait(false);
            await WriteFlag(tls, approved, token).ConfigureAwait(false);
            if (!approved || !await remoteApproval.ConfigureAwait(false))
                throw new OperationCanceledException("The connection was declined.");

            token.ThrowIfCancellationRequested();
            bool authority = own.HasGroup || !peer.HasGroup && initiator;
            if (authority)
            {
                var group = exportGroup(peer.Name, address);
                await WriteFrame(tls, JsonSerializer.SerializeToUtf8Bytes(group), token).ConfigureAwait(false);
                if (!await ReadFlag(tls, token).ConfigureAwait(false)) throw new IOException("The other PC did not save the connection.");
            }
            else
            {
                var group = ReadJson<PairingGroup>(await ReadFrame(tls, token).ConfigureAwait(false));
                token.ThrowIfCancellationRequested();
                // The settings owner repeats this check while committing the adopted group.
                if (hasGroup()) throw new InvalidOperationException("This PC has joined another group while awaiting approval.");
                adoptGroup(peer.Name, address, group);
                await WriteFlag(tls, true, token).ConfigureAwait(false);
            }
            Paired?.Invoke(peer.Name, address, !authority);
        }
        catch
        {
            if (!initiator && approved) Failed?.Invoke("The approved connection could not be completed. Try connecting again.");
            throw;
        }
        finally { lifetime.Cancel(); }
    }

    private static Hello ReadHello(byte[] raw)
    {
        var hello = ReadJson<Hello>(raw);
        if (hello.Version != 1 || hello.Nonce?.Length != 32 || hello.Name?.Length is not (>= 1 and <= 32)
            || Uri.CheckHostName(hello.Name) != UriHostNameType.Dns)
            throw new InvalidDataException("Invalid pairing identity.");
        return hello;
    }

    private static T ReadJson<T>(byte[] raw) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(raw) ?? throw new InvalidDataException("Missing pairing message."); }
        catch (JsonException) { throw new InvalidDataException("Invalid pairing message."); }
    }

    internal static string VerificationCode(byte[] serverCertificate, byte[] clientHello, byte[] serverHello)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain); hash.AppendData(SHA256.HashData(serverCertificate));
        hash.AppendData(clientHello); hash.AppendData(serverHello);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(hash.GetHashAndReset());
        return (value % 1000000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static async Task WriteFrame(Stream stream, byte[] body, CancellationToken token)
    {
        if (body.Length is < 1 or > MaximumFrame) throw new InvalidDataException("Pairing message is too large or empty.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is < 1 or > MaximumFrame) throw new InvalidDataException("Pairing message is too large or empty.");
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return body;
    }

    private static Task WriteFlag(Stream stream, bool value, CancellationToken token) => WriteFrame(stream, [value ? (byte)1 : (byte)0], token);
    private static async Task<bool> ReadFlag(Stream stream, CancellationToken token)
    {
        byte[] body = await ReadFrame(stream, token).ConfigureAwait(false);
        if (body.Length != 1 || body[0] > 1) throw new InvalidDataException("Invalid pairing approval.");
        return body[0] == 1;
    }

    public void Dispose()
    {
        stop.Cancel(); listener.Stop(); active?.Dispose(); certificate.Dispose();
    }
}
