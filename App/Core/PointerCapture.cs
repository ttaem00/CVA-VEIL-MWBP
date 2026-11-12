// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
// A program that hides the pointer (a 3D game steering its camera) reads how far the mouse moved,
// not where the pointer is. Sent absolute positions, it keeps re-centring the pointer and turns the
// camera by the distance to that position on every packet. While the controlled PC's pointer is
// hidden, that PC says so, and the controlling PC sends movement as distances instead and does not
// leave by the screen edge. Switching by hotkey and the emergency return are unaffected.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    private static bool reportedHidden;
    private static TcpSk reportedOn;

    /// <summary>What to send now, if anything. A hidden pointer is repeated so that a controller
    /// that reconnected, or missed the first notice, catches up; a visible one is said once.</summary>
    internal static bool ShouldReport(bool hidden, bool reportedHidden, bool sameConnection, long now, long nextRepeat, bool acknowledged) =>
        hidden != reportedHidden || !sameConnection && (hidden || reportedHidden) || hidden && !acknowledged && now >= nextRepeat;

    /// <summary>Called for each mouse packet applied on this PC. Costs one time comparison;
    /// the pointer state is read at most every <see cref="CheckIntervalMs"/>. A packet that already
    /// carries a distance shows that the controller knows, so nothing is repeated to it.</summary>
    internal static void Observe(ID controller, TcpSk connection, bool acknowledged)
    {
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref nextCheck) || connection == null || controller == Common.MachineID) return;
        Interlocked.Exchange(ref nextCheck, now + CheckIntervalMs);
        bool hidden = IsPointerHidden();
        Point pointer = default;
        lock (ReportGate)
        {
            if (!ShouldReport(hidden, reportedHidden, ReferenceEquals(connection, reportedOn), now, nextRepeat, acknowledged)) return;
            reportedHidden = hidden; reportedOn = connection; nextRepeat = now + RepeatIntervalMs;
        }

        if (!hidden) _ = NativeMethods.GetCursorPos(ref pointer);
        var desktop = MachineStuff.DesktopBounds;
        var notice = new DATA { Type = PackageType.PointerCapture, Des = controller };
        notice.Md.dwFlags = hidden ? 1 : 0;
        if (!hidden && desktop != null && desktop.Right > desktop.Left && desktop.Bottom > desktop.Top)
        {
            // Where the pointer really is, so the controller resumes from there and not from
            // wherever its own idea of the position was left when the pointer was hidden.
            notice.Md.X = (int)Math.Clamp((long)(pointer.X - desktop.Left) * 65535 / (desktop.Right - desktop.Left), 0, 65535);
            notice.Md.Y = (int)Math.Clamp((long)(pointer.Y - desktop.Top) * 65535 / (desktop.Bottom - desktop.Top), 0, 65535);
        }
        else notice.Md.X = notice.Md.Y = -1;
        Logger.Log(hidden ? "Pointer is hidden by the foreground program; asking for relative movement."
            : "Pointer is visible again; absolute movement resumes.");
        // Never send from the thread that applies input.
        _ = Task.Run(() => Common.SkSend(notice, null, false));
    }

    private static bool IsPointerHidden()
    {
        var cursor = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        // 0 is hidden; 1 is showing; 2 means Windows suppresses it for touch or pen, which is not a capture.
        return NativeMethods.GetCursorInfo(ref cursor) && cursor.flags == 0;
    }

    // ---- Controlling PC: remember which peer asked, on which connection. ----
    private sealed record Capture(uint Peer, TcpSk Connection);
    internal sealed record Resume(uint Peer, int X, int Y);

    private static volatile Capture captured;
    private static Resume pendingResume;

    /// <summary>True while that PC's pointer is hidden, according to a connection that is still alive.
    /// Read on the hook thread for every mouse event: two field reads and no call into Windows.</summary>
    internal static bool IsCaptured(uint peer)
    {
        var state = captured;
        return state != null && state.Peer == peer && state.Connection.Status == SocketStatus.Connected && state.Connection.BackingSocket != null;
    }

    /// <summary>Where that PC's pointer was when it became visible again; taken once, on the hook thread.</summary>
    internal static Resume TakeResume(uint peer)
    {
        var resume = Volatile.Read(ref pendingResume);
        return resume != null && resume.Peer == peer && ReferenceEquals(Interlocked.CompareExchange(ref pendingResume, null, resume), resume) ? resume : null;
    }

    /// <summary>Called on a socket thread when a PC reports its pointer state.</summary>
    internal static void Receive(DATA package, TcpSk connection)
    {
        if (package.Des != Common.MachineID || connection == null || connection.MachineId == 0 || connection.MachineId == (uint)Common.MachineID) return;
        uint peer = connection.MachineId;
        if (package.Md.dwFlags == 1)
        {
            bool already = IsCaptured(peer);
            captured = new Capture(peer, connection);
            if (!already) Logger.Log("Peer pointer is hidden: sending relative movement and holding the screen edge.");
            return;
        }

        var state = captured;
        if (state == null || state.Peer != peer) return;
        captured = null;
        if (package.Md.X is >= 0 and <= 65535 && package.Md.Y is >= 0 and <= 65535)
            Volatile.Write(ref pendingResume, new Resume(peer, package.Md.X, package.Md.Y));
        Logger.Log("Peer pointer is visible: absolute movement resumes.");
    }

    internal static void ResetForTest()
    {
        captured = null; Volatile.Write(ref pendingResume, null);
        lock (ReportGate) { reportedHidden = false; reportedOn = null; nextRepeat = 0; }
        Interlocked.Exchange(ref nextCheck, 0);
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
