// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace MouseWithoutBorders.Core;

internal readonly record struct MonitorKey(uint Peer, string DisplayId);

// Native geometry belongs to the authenticated peer; WorldOrigin belongs to the applied layout.
internal readonly record struct MonitorPlacement(MonitorKey Key, Rectangle NativeBounds, Point WorldOrigin)
{
    internal Rectangle WorldBounds => new(WorldOrigin, NativeBounds.Size);
}

internal readonly record struct MonitorPosition(MonitorPlacement Monitor, Point NativePoint);

internal sealed class MonitorLayout
{
    private readonly MonitorPlacement[] monitors;
    internal IReadOnlyList<MonitorPlacement> Monitors { get; }

    internal MonitorLayout(IEnumerable<MonitorPlacement> placements)
    {
        monitors = placements.ToArray();
        Monitors = Array.AsReadOnly(monitors);
        for (int i = 0; i < monitors.Length; i++)
        {
            var monitor = monitors[i];
            if (monitor.NativeBounds.Width <= 0 || monitor.NativeBounds.Height <= 0)
                throw new ArgumentException("A monitor must contain addressable pixels.", nameof(placements));
            // Overflow would turn a right/bottom edge into a different side of the layout.
            _ = checked(monitor.NativeBounds.X + monitor.NativeBounds.Width);
            _ = checked(monitor.NativeBounds.Y + monitor.NativeBounds.Height);
            _ = checked(monitor.WorldOrigin.X + monitor.NativeBounds.Width);
            _ = checked(monitor.WorldOrigin.Y + monitor.NativeBounds.Height);
            for (int j = 0; j < i; j++)
                if (monitors[j].Key == monitor.Key || monitors[j].WorldBounds.IntersectsWith(monitor.WorldBounds))
                    throw new ArgumentException("Monitor identities and occupied world areas must be distinct.", nameof(placements));
        }
    }

    // Both points use the current monitor's native coordinates. The proposed point may
    // already lie on a different Windows monitor; that does not authorize a world crossing.
    internal MonitorPosition Route(MonitorKey current, Point previousNativePoint, Point proposedNativePoint, Func<uint, bool> canEnterPeer = null)
    {
        var monitor = GetMonitor(current);
        double startX = (long)monitor.WorldOrigin.X + previousNativePoint.X - monitor.NativeBounds.X;
        double startY = (long)monitor.WorldOrigin.Y + previousNativePoint.Y - monitor.NativeBounds.Y;
        double endX = (long)monitor.WorldOrigin.X + proposedNativePoint.X - monitor.NativeBounds.X;
        double endY = (long)monitor.WorldOrigin.Y + proposedNativePoint.Y - monitor.NativeBounds.Y;
        while (true)
        {
            var bounds = monitor.WorldBounds;
            double dx = endX - startX, dy = endY - startY, first = double.PositiveInfinity;
            int sides = 0;
            void Edge(double time, int candidate)
            {
                time = Math.Min(time, 1);
                if (time >= 0 && time <= first)
                {
                    if (time < first) { first = time; sides = 0; }
                    sides |= 1 << candidate;
                }
            }
            // Windows can stop at R-1/B-1. Waiting for R/B loses that last reachable MOVE.
            if (dx > 0 && endX >= bounds.Right - 1) Edge((bounds.Right - startX) / dx, 1);
            if (dx < 0 && endX <= bounds.Left) Edge((bounds.Left - startX) / dx, 2);
            if (dy > 0 && endY >= bounds.Bottom - 1) Edge((bounds.Bottom - startY) / dy, 3);
            if (dy < 0 && endY <= bounds.Top) Edge((bounds.Top - startY) / dy, 4);
            if (sides == 0) return Position(monitor, endX, endY);

            double hitX = startX + dx * first, hitY = startY + dy * first;
            for (int side = 1; side <= 4; side++)
            {
                if ((sides & (1 << side)) == 0) continue;
                foreach (var next in monitors)
                {
                    var other = next.WorldBounds;
                    if (!TrySharedEdge(bounds, other, side, out int low, out int high)) continue;
                    // A shared interval, not just a corner or an overlapping projection, is required.
                    double coordinate = side <= 2 ? hitY : hitX;
                    if (coordinate < low || coordinate >= high) continue;
                    // A denied intermediate PC cannot be skipped by one large MOVE.
                    if (next.Key.Peer != monitor.Key.Peer && canEnterPeer != null && !canEnterPeer(next.Key.Peer))
                        return Position(monitor, endX, endY);
                    if (first == 1) return Position(next, endX, endY);
                    monitor = next;
                    startX = hitX; startY = hitY;
                    goto Crossed;
                }
            }
            return Position(monitor, endX, endY);
        Crossed:;
        }
    }

    internal static bool TrySharedEdge(Rectangle bounds, Rectangle other, int side, out int low, out int high)
    {
        low = side <= 2 ? Math.Max(bounds.Top, other.Top) : Math.Max(bounds.Left, other.Left);
        high = side <= 2 ? Math.Min(bounds.Bottom, other.Bottom) : Math.Min(bounds.Right, other.Right);
        return low < high && (side switch
        {
            1 => bounds.Right == other.Left,
            2 => bounds.Left == other.Right,
            3 => bounds.Bottom == other.Top,
            _ => bounds.Top == other.Bottom,
        });
    }

    internal MonitorPlacement GetMonitor(MonitorKey key)
    {
        foreach (var monitor in monitors)
            if (monitor.Key == key) return monitor;
        throw new ArgumentException("The current monitor is not in the applied layout.", nameof(key));
    }

    private static MonitorPosition Position(MonitorPlacement monitor, double worldX, double worldY)
    {
        var bounds = monitor.NativeBounds;
        return new(monitor, new Point(
            (int)Math.Clamp(worldX - monitor.WorldOrigin.X + bounds.X, bounds.Left, bounds.Right - 1),
            (int)Math.Clamp(worldY - monitor.WorldOrigin.Y + bounds.Y, bounds.Top, bounds.Bottom - 1)));
    }
}
