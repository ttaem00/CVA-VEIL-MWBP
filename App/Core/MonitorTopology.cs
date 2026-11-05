// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Forms;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

// A descriptor deliberately has no peer identity: the authenticated socket owns it.
internal sealed record MonitorDescriptor(string DisplayId, int X, int Y, int Width, int Height)
{
    [JsonIgnore] internal Rectangle NativeBounds => new(X, Y, Width, Height);
}

internal sealed record MonitorOrigin(uint Peer, string DisplayId, int X, int Y)
{
    [JsonIgnore] internal MonitorKey Key => new(Peer, DisplayId);
}

internal static class MonitorTopology
{
    private sealed record State(Dictionary<uint, MonitorDescriptor[]> Peers, MonitorOrigin[] Origins,
        uint[] Order, uint LocalPeer, MonitorLayout Layout);
    private static readonly object Sync = new();
    private static State state = new(new(), [], [], 0, new MonitorLayout([]));

    internal static MonitorLayout Layout => Volatile.Read(ref state).Layout;
    internal static IReadOnlyList<MonitorPlacement> Monitors => Layout.Monitors;
    internal static MonitorDescriptor[] LocalDisplays
    {
        get
        {
            var current = Volatile.Read(ref state);
            return current.Peers.TryGetValue(current.LocalPeer, out var displays) ? displays.ToArray() : [];
        }
    }

    internal static void RefreshLocal()
    {
        uint peer = (uint)Common.MachineID;
        var displays = Screen.AllScreens.Select(screen => new MonitorDescriptor(screen.DeviceName,
            screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height)).ToArray();
        UpdatePeer(peer, displays, PeerOrder(), peer, Setting.Values.MonitorOrigins);
    }

    internal static void AcceptPeer(uint peer, MonitorDescriptor[] displays)
    {
        if (displays == null) return; // Older frames have no metadata; they must not erase it.
        UpdatePeer(peer, displays, PeerOrder());
    }

    private static void UpdatePeer(uint peer, MonitorDescriptor[] displays, uint[] order,
        uint localPeer = 0, MonitorOrigin[] localOrigins = null)
    {
        // Bound native descriptors once at their owner. The existing frame bounds wire bytes.
        if (peer is (uint)ID.NONE or (uint)ID.ALL
            || displays.Any(display => display == null || string.IsNullOrWhiteSpace(display.DisplayId)
                || display.Width <= 0 || display.Height <= 0
                || (long)display.X + display.Width > int.MaxValue || (long)display.Y + display.Height > int.MaxValue)
            || displays.Select(display => display.DisplayId).Distinct(StringComparer.Ordinal).Count() != displays.Length
            || displays.Length != 0 && (displays.Max(display => (long)display.X + display.Width) - displays.Min(display => display.X) > int.MaxValue
                || displays.Max(display => (long)display.Y + display.Height) - displays.Min(display => display.Y) > int.MaxValue))
        {
            Logger.Log("Ignoring invalid monitor descriptors from authenticated peer " + peer);
            return;
        }
        displays = displays.OrderBy(display => display.DisplayId, StringComparer.Ordinal).ToArray();
        lock (Sync)
        {
            var previous = state;
            var origins = localOrigins ?? previous.Origins;
            if ((localPeer == 0 || previous.LocalPeer == localPeer) && previous.Origins.SequenceEqual(origins)
                && previous.Order.SequenceEqual(order) && previous.Peers.TryGetValue(peer, out var known) && known.SequenceEqual(displays)) return;
            var peers = new Dictionary<uint, MonitorDescriptor[]>(previous.Peers) { [peer] = displays };
            if (localPeer != 0 && previous.LocalPeer != 0 && previous.LocalPeer != localPeer) peers.Remove(previous.LocalPeer);
            try { Volatile.Write(ref state, Build(peers, origins.ToArray(), order, localPeer == 0 ? previous.LocalPeer : localPeer)); }
            catch (Exception error) when (error is ArgumentException or OverflowException or InvalidDataException)
            { Logger.Log("Ignoring unusable monitor geometry: " + error.Message); }
        }
    }

    internal static void ApplyOrigins(MonitorOrigin[] origins, string machineMatrix = null)
    {
        ValidateOrigins(origins);
        var order = PeerOrder(machineMatrix);
        lock (Sync)
        {
            var previous = state;
            if (previous.Origins.SequenceEqual(origins) && previous.Order.SequenceEqual(order)) return;
            Volatile.Write(ref state, Build(previous.Peers, origins.ToArray(), order, previous.LocalPeer));
        }
    }

    internal static void ValidateOrigins(MonitorOrigin[] origins)
    {
        if (origins == null || origins.Any(origin => origin == null
            || origin.Peer is (uint)ID.NONE or (uint)ID.ALL || string.IsNullOrWhiteSpace(origin.DisplayId))
            || origins.Select(origin => origin.Key).Distinct().Count() != origins.Length)
            throw new InvalidDataException("Invalid monitor origins.");
    }

    // Capture settings before Sync: topology publication never takes a settings/network lock.
    private static uint[] PeerOrder(string machineMatrix = null) => (machineMatrix ?? Setting.Values.MachineMatrixString).Split(',')
        .Select(name => string.Equals(name, Common.MachineName, StringComparison.OrdinalIgnoreCase)
            ? (uint)Common.MachineID : (uint)MachineStuff.IdFromName(name))
        .Where(peer => peer is not ((uint)ID.NONE) and not ((uint)ID.ALL)).Distinct().ToArray();

    private static State Build(Dictionary<uint, MonitorDescriptor[]> peers, MonitorOrigin[] origins, uint[] order, uint localPeer)
    {
        var desired = origins.ToDictionary(origin => origin.Key);
        var placed = new List<MonitorPlacement>();
        var pending = new List<MonitorPlacement>();
        var orderedPeers = order.Concat(peers.Keys.OrderBy(peer => peer)).Distinct().Where(peers.ContainsKey).ToArray();
        // Reserve requested positions before assigning defaults to newly discovered monitors.
        foreach (uint peer in orderedPeers)
            foreach (var display in peers[peer])
            {
                var key = new MonitorKey(peer, display.DisplayId);
                if (!desired.TryGetValue(key, out var origin)) continue;
                var point = new Point(origin.X, origin.Y);
                if (Fits(display, point, placed)) placed.Add(new(key, display.NativeBounds, point));
                else pending.Add(new(key, display.NativeBounds, Point.Empty));
            }
        foreach (uint peer in orderedPeers)
        {
            if (!peers.TryGetValue(peer, out var displays) || displays.Length == 0) continue;
            int left = displays.Min(display => display.X);
            var anchor = displays.FirstOrDefault(display => desired.ContainsKey(new(peer, display.DisplayId)));
            long dx = anchor == null ? 0 : (long)desired[new(peer, anchor.DisplayId)].X - anchor.X;
            long dy = anchor == null ? 0 : (long)desired[new(peer, anchor.DisplayId)].Y - anchor.Y;
            int groupRight = Math.Max(0, placed.Select(display => display.WorldBounds.Right).DefaultIfEmpty().Max());
            foreach (var display in displays)
            {
                var key = new MonitorKey(peer, display.DisplayId);
                if (!desired.ContainsKey(key))
                {
                    long x = anchor == null ? (long)groupRight + display.X - left : display.X + dx;
                    long y = anchor == null ? display.Y : display.Y + dy;
                    if (x <= int.MaxValue && x >= int.MinValue && y <= int.MaxValue && y >= int.MinValue
                        && Fits(display, new((int)x, (int)y), placed)) placed.Add(new(key, display.NativeBounds, new((int)x, (int)y)));
                    else pending.Add(new(key, display.NativeBounds, Point.Empty));
                }
            }
        }
        // A hotplug/resolution change can invalidate an old origin. Keep its preference but
        // move only conflicting displays into free space so input and metadata stay usable.
        foreach (var display in pending)
        {
            long x = Math.Max(0, placed.Select(item => item.WorldBounds.Right).DefaultIfEmpty().Max());
            if (x + display.NativeBounds.Width > int.MaxValue)
                x = (long)placed.Min(item => item.WorldBounds.Left) - display.NativeBounds.Width;
            placed.Add(display with { WorldOrigin = new(checked((int)x), 0) });
        }
        return new(peers, origins, order, localPeer, new MonitorLayout(placed));
    }

    private static bool Fits(MonitorDescriptor display, Point point, List<MonitorPlacement> placed) =>
        (long)point.X + display.Width <= int.MaxValue && (long)point.Y + display.Height <= int.MaxValue
        && !placed.Any(other => other.WorldBounds.IntersectsWith(new Rectangle(point, display.NativeBounds.Size)));
}
