// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

internal static class PointerCapture
{
    internal const int CheckIntervalMs = 50;
    internal const int RepeatIntervalMs = 1000;

    // ---- Controlled PC: notice the hidden pointer and tell the controlling PC. ----
    private static readonly object ReportGate = new();
    private static long nextCheck;
    private static long nextRepeat;
    private static bool reportedCapture;
    private static bool localCapture;
    private static TcpSk reportedOn;
    private static IntPtr sampledForeground;
    private static Point? lastOffCenterRequest;
    private static int recenterSamples;
    private static bool wasOffCenter;

    internal static bool LocalCapture => Volatile.Read(ref localCapture);

    /// <summary>Repeat either transition when the controller is still using the other mode.</summary>
    internal static bool ShouldReport(bool captured, bool reportedCapture, bool sameConnection, long now, long nextRepeat, bool receivingDistances) =>
        captured != reportedCapture || !sameConnection && (captured || receivingDistances)
        || captured != receivingDistances && now >= nextRepeat;

    /// <summary>Sample capture after applying input and tell the controller over its existing FIFO.
    /// The same result tells the receiver whether to suppress its desktop cursor overlay.</summary>
    internal static bool Observe(ID controller, TcpSk connection, bool receivingDistances, MOUSEDATA? requestedMove)
    {
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref nextCheck)) return LocalCapture;
        lock (ReportGate)
        {
            if (now < nextCheck) return localCapture;
            Interlocked.Exchange(ref nextCheck, now + CheckIntervalMs);
            var cursor = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref cursor)) return localCapture;
            bool captured = IsLocallyCaptured(cursor, requestedMove);
            bool hideOverlay = captured && !localCapture;
            Volatile.Write(ref localCapture, captured);
            if (hideOverlay) CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
            if (connection == null || controller == Common.MachineID ||
                !ShouldReport(localCapture, reportedCapture, ReferenceEquals(connection, reportedOn), now, nextRepeat, receivingDistances))
                return localCapture;

            var notice = new DATA { Type = PackageType.PointerCapture, Des = controller };
            notice.Md.dwFlags = localCapture ? 1 : 0;
            // The relative-mode notice has no wheel action. Stamp observation order before
            // enqueueing, since an older notice can drain later on a second live TCP channel.
            int sequence = unchecked((int)now);
            notice.Md.WheelDelta = sequence == 0 ? 1 : sequence;
            var desktop = MachineStuff.DesktopBounds;
            notice.Md.X = notice.Md.Y = -1;
            if (!localCapture && desktop != null && desktop.Right > desktop.Left && desktop.Bottom > desktop.Top)
            {
                notice.Md.X = (int)Math.Clamp((long)(cursor.ptScreenPos.x - desktop.Left) * 65535 / (desktop.Right - desktop.Left), 0, 65535);
                notice.Md.Y = (int)Math.Clamp((long)(cursor.ptScreenPos.y - desktop.Top) * 65535 / (desktop.Bottom - desktop.Top), 0, 65535);
            }

            // Enqueue under the sampling lock so a later release cannot overtake this notice.
            if (Common.Sk?.EnqueueInput(notice, null) == true)
            {
                reportedCapture = localCapture;
                reportedOn = connection;
                nextRepeat = now + RepeatIntervalMs;
                Logger.Log(localCapture ? "Foreground pointer capture detected; asking for relative movement."
                    : "Foreground pointer capture ended; asking for absolute movement.");
            }
            return localCapture;
        }
    }

    private static bool IsLocallyCaptured(NativeMethods.CURSORINFO cursor, MOUSEDATA? requestedMove)
    {
        var foreground = cursor.flags == 0 ? NativeMethods.GetForegroundWindow() : IntPtr.Zero;
        if (foreground != sampledForeground)
        {
            sampledForeground = foreground;
            recenterSamples = 0;
            lastOffCenterRequest = null;
            wasOffCenter = false;
        }
        if (foreground == IntPtr.Zero || foreground == NativeMethods.GetShellWindow() || foreground == NativeMethods.GetDesktopWindow())
            return false;

        // A game can confine the pointer to its window or to one monitor of a larger desktop.
        var actual = new Point(cursor.ptScreenPos.x, cursor.ptScreenPos.y);
        var desktop = MachineStuff.DesktopBounds;
        if (desktop != null && NativeMethods.GetClipCursor(out var clip) &&
            IsViewportClip(Rectangle.FromLTRB(desktop.Left, desktop.Top, desktop.Right, desktop.Bottom),
                Rectangle.FromLTRB(clip.Left, clip.Top, clip.Right, clip.Bottom)))
            return true;

        // Some games recenter instead of clipping. A fullscreen window alone is not evidence:
        // require two different off-center requests while the actual pointer stays at its center.
        if (!NativeMethods.GetWindowRect(foreground, out var window)) return false;
        var center = new Point(window.Left + (window.Right - window.Left) / 2, window.Top + (window.Bottom - window.Top) / 2);
        Point? requestedPosition = requestedMove is { X: >= 0 and <= 65535, Y: >= 0 and <= 65535 } move && desktop != null
            ? new Point(desktop.Left + (int)((long)move.X * (desktop.Right - desktop.Left) / 65535),
                desktop.Top + (int)((long)move.Y * (desktop.Bottom - desktop.Top) / 65535))
            : null;
        return ObserveRecentering(actual, center, requestedPosition);
    }

    internal static bool ObserveRecentering(Point actual, Point center, Point? requestedPosition)
    {
        if (!NearCenter(actual, center))
        {
            // A sample just after injecting a game delta can see the pointer before the next
            // game frame recenters it. Require a second off-center sample before releasing.
            if (recenterSamples >= 2 && !wasOffCenter)
            {
                wasOffCenter = true;
                return true;
            }
            recenterSamples = 0;
            lastOffCenterRequest = null;
            return false;
        }

        wasOffCenter = false;
        if (recenterSamples < 2 && requestedPosition is { } requested && !NearCenter(requested, center) &&
            requested != lastOffCenterRequest)
        {
            recenterSamples = Math.Min(2, recenterSamples + 1);
            lastOffCenterRequest = requested;
        }

        return recenterSamples >= 2;
    }

    private static bool NearCenter(Point point, Point center) =>
        Math.Abs(point.X - center.X) <= 24 && Math.Abs(point.Y - center.Y) <= 24;

    internal static bool IsViewportClip(Rectangle desktop, Rectangle clip) =>
        clip.Width > 0 && clip.Height > 0 && clip != desktop && desktop.Contains(clip);

    // ---- Controlling PC: remember which peer asked, on which connection. ----
    private sealed record Capture(TcpSk Connection, int Sequence, bool Active, Point? Resume);
    private static readonly ConcurrentDictionary<uint, Capture> PeerModes = new();

    internal static bool IsCaptured(uint peer) => PeerModes.TryGetValue(peer, out var state)
        && state.Active && IsLive(state.Connection);

    private static bool IsLive(TcpSk connection) => connection.Status == SocketStatus.Connected && connection.BackingSocket != null;

    /// <summary>Where that PC's pointer was when it became visible again; taken once, on the hook thread.</summary>
    internal static Point? TakeResume(uint peer)
    {
        return PeerModes.TryGetValue(peer, out var state) && state.Resume.HasValue && IsLive(state.Connection) &&
            PeerModes.TryUpdate(peer, state with { Resume = null }, state) ? state.Resume : null;
    }

    /// <summary>Called on a socket thread when a PC reports its pointer state.</summary>
    internal static void Receive(DATA package, TcpSk connection)
    {
        if (package.Des != Common.MachineID || connection == null || connection.MachineId == 0 ||
            connection.MachineId == (uint)Common.MachineID || !IsLive(connection) || package.Md.dwFlags is not (0 or 1)) return;
        uint peer = connection.MachineId;
        bool active = package.Md.dwFlags == 1;
        int sequence = package.Md.WheelDelta; // RC19 sent zero and remains accepted.
        bool wasActive;
        lock (PeerModes)
        {
            if (!IsLive(connection)) return;
            bool known = PeerModes.TryGetValue(peer, out var state);
            if (known && state.Sequence != 0 && (sequence == 0 || unchecked(sequence - state.Sequence) <= 0)) return;
            wasActive = known && state.Active;
            Point? resume = !active && (wasActive || known && state.Resume.HasValue) && package.Md.X is >= 0 and <= 65535 && package.Md.Y is >= 0 and <= 65535
                ? new Point(package.Md.X, package.Md.Y)
                : null;
            PeerModes[peer] = new Capture(connection, sequence, active, resume);
        }
        if (active != wasActive) Logger.Log(active ? "Peer pointer is captured: sending relative movement. Hold the configured edge key to switch."
            : "Peer pointer capture ended: absolute movement resumes.");
    }

    internal static void PeerDisconnected(uint peer)
    {
        lock (PeerModes) PeerModes.TryRemove(peer, out _);
    }

    internal static void TransportClosed()
    {
        lock (PeerModes) PeerModes.Clear();
    }

    internal static void ResetForTest()
    {
        TransportClosed();
        lock (ReportGate)
        {
            reportedCapture = false; reportedOn = null; nextRepeat = 0;
            sampledForeground = IntPtr.Zero; lastOffCenterRequest = null; recenterSamples = 0; wasOffCenter = false;
            Volatile.Write(ref localCapture, false);
            Interlocked.Exchange(ref nextCheck, 0);
        }
    }

    // ---- Wire form of a distance: the existing relative marker of the mouse packet. ----
    internal const int MaxDistance = 50000;

    internal static int Encode(int distance) => distance < 0 ? distance - Event.MOVE_MOUSE_RELATIVE : distance + Event.MOVE_MOUSE_RELATIVE;

    internal static int Decode(int value) => value < 0 ? value + Event.MOVE_MOUSE_RELATIVE : value - Event.MOVE_MOUSE_RELATIVE;

    internal static bool IsRelativeMove(DATA packet) => packet.Type == PackageType.Mouse && packet.Md.dwFlags == WM.WM_MOUSEMOVE
        && Math.Abs((long)packet.Md.X) >= Event.MOVE_MOUSE_RELATIVE && Math.Abs((long)packet.Md.Y) >= Event.MOVE_MOUSE_RELATIVE
        && Math.Abs((long)packet.Md.X) <= Event.MOVE_MOUSE_RELATIVE + MaxDistance && Math.Abs((long)packet.Md.Y) <= Event.MOVE_MOUSE_RELATIVE + MaxDistance;

    /// <summary>Adds a later distance to an earlier one that has not been sent yet. Distances must
    /// add up; replacing the earlier one, as is right for positions, would lose movement.</summary>
    internal static bool TryMerge(DATA earlier, DATA later, out DATA merged)
    {
        merged = earlier;
        if (!IsRelativeMove(earlier) || !IsRelativeMove(later) || earlier.Des != later.Des) return false;
        long x = (long)Decode(earlier.Md.X) + Decode(later.Md.X), y = (long)Decode(earlier.Md.Y) + Decode(later.Md.Y);
        if (Math.Abs(x) > MaxDistance || Math.Abs(y) > MaxDistance) return false;
        merged = new DATA(later.Bytes);
        merged.Md.X = Encode((int)x); merged.Md.Y = Encode((int)y);
        return true;
    }
}
