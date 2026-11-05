// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Core;

internal sealed class LanDiscovery : IDisposable
{
    private const int DiscoveryPort = 15102;
    private readonly UdpClient socket;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, Peer> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string machineName;
    private readonly Func<int> currentPort;
    private readonly Task running;

    internal sealed record Peer(string Name, IPAddress Address, long LastSeen)
    {
        public override string ToString() => $"{Name} — {Address}";
    }

    private LanDiscovery(string name, Func<int> port)
    {
        machineName = name; currentPort = port;
        socket = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };
        running = Task.Run(RunAsync);
    }

    internal static LanDiscovery TryStart(string name, Func<int> port)
    {
        try { return new LanDiscovery(name, port); }
        catch (SocketException error)
        {
            // Discovery is optional; a port conflict must not prevent configured TCP connections.
            Logger.Log("LAN discovery unavailable: " + error.Message);
            return null;
        }
    }

    internal Peer[] Snapshot() => peers.Values.OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static byte[] Announcement(string name, int port) =>
        Encoding.UTF8.GetBytes($"MWB-Portable/1|{port.ToString(CultureInfo.InvariantCulture)}|{name}");

    internal static bool TryRead(byte[] packet, out string name)
    {
        name = string.Empty;
        if (packet.Length > 256) return false;
        string[] fields = Encoding.UTF8.GetString(packet).Split('|');
        if (fields.Length != 3 || fields[0] != "MWB-Portable/1"
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65534
            || Uri.CheckHostName(fields[2]) != UriHostNameType.Dns) return false;
        name = fields[2];
        return true;
    }

    private async Task ReceiveAsync()
    {
        while (true)
        {
            UdpReceiveResult message;
            try { message = await socket.ReceiveAsync(stop.Token).ConfigureAwait(false); }
            catch (SocketException error)
            {
                Logger.Log("LAN discovery receive: " + error.Message);
                await Task.Delay(3000, stop.Token).ConfigureAwait(false);
                continue;
            }
            if (!TryRead(message.Buffer, out string name)
                || name.Equals(machineName, StringComparison.OrdinalIgnoreCase)) continue;
            // Unauthenticated advertisements may suggest peers, but cannot grow retained state without bound.
            if (peers.Count < 64 || peers.ContainsKey(name))
                peers[name] = new Peer(name, message.RemoteEndPoint.Address, Environment.TickCount64);
        }
    }

    private async Task AnnounceAsync()
    {
        var destination = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        while (true)
        {
            foreach (var peer in peers)
                if (Environment.TickCount64 - peer.Value.LastSeen >= 12000) peers.TryRemove(peer.Key, out _);
            try { await socket.SendAsync(Announcement(machineName, currentPort()), destination, stop.Token).ConfigureAwait(false); }
            catch (SocketException error) { Logger.Log("LAN discovery announce: " + error.Message); }
            await Task.Delay(3000, stop.Token).ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.WhenAll(ReceiveAsync(), AnnounceAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { socket.Dispose(); }
    }

    public void Dispose()
    {
        stop.Cancel();
        running.GetAwaiter().GetResult();
        stop.Dispose();
    }
}
