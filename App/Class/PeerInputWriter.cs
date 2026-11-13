// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Class;

// One FIFO per selected connection. The gate protects only in-memory state;
// neither encryption, socket I/O nor failure cleanup runs while holding it.
internal sealed class PeerInputWriter(SocketStuff owner, TcpSk peer)
{
    internal const int Capacity = 512;
    private readonly object gate = new();
    private readonly LinkedList<DATA> pending = new();
    private readonly Dictionary<int, KEYBDDATA> keys = new();
    private readonly Dictionary<(int Message, int Button), MOUSEDATA> buttons = new();
    private bool running, stopped;
    internal bool IsIdle { get { lock (gate) return !running; } }

    internal bool Enqueue(DATA packet)
    {
        // DATA is a mutable union reused by Event. Freeze it before returning.
        var copy = new DATA(packet.Bytes) { Src = Common.MachineID };
        bool overflow = false;
        lock (gate)
        {
            if (stopped) return false;
            if (PureMove(copy) && pending.Last is { } tail && PureMove(tail.Value)) tail.Value = copy;
            // Unsent distances add up; they are never replaced, and never grow the queue.
            else if (pending.Last is { } distance && RelativeMouse.TryMerge(distance.Value, copy, out var merged)) distance.Value = merged;
            else if (pending.Count < Capacity) pending.AddLast(copy);
            else { StopLocked(); overflow = true; }
            if (!overflow)
            {
                RememberHold(copy);
                StartLocked();
                return true;
            }
        }
        // An UP must never be silently lost. Retire this connection off-hook;
        // the receiving endpoint releases its applied holds on disconnect.
        _ = Task.Run(() => owner.FailQueuedInput(peer));
        return false;
    }

    internal bool ReleaseHeldInput(bool hide)
    {
        bool overflow = false;
        lock (gate)
        {
            if (stopped) return false;
            if (pending.Count + keys.Count + buttons.Count + (hide ? 1 : 0) > Capacity)
            { StopLocked(); overflow = true; }
            else
            {
                foreach (var held in keys.Values)
                {
                    var up = held; up.dwFlags |= (int)WM.LLKHF.UP;
                    pending.AddLast(new DATA { Type = PackageType.Keyboard, Src = Common.MachineID, Des = (ID)peer.MachineId, Kd = up });
                }
                foreach (var held in buttons.Values)
                    pending.AddLast(new DATA { Type = PackageType.Mouse, Src = Common.MachineID, Des = (ID)peer.MachineId, Md = held });
                if (hide) pending.AddLast(new DATA { Type = PackageType.HideMouse, Src = Common.MachineID, Des = (ID)peer.MachineId });
                keys.Clear(); buttons.Clear();
                StartLocked();
            }
        }
        if (overflow) _ = Task.Run(() => owner.FailQueuedInput(peer));
        return !overflow;
    }

    private static bool PureMove(DATA packet) => packet.Type == PackageType.Mouse && packet.Md.dwFlags == WM.WM_MOUSEMOVE
        && packet.Md.X is >= 0 and <= 65535 && packet.Md.Y is >= 0 and <= 65535;

    private void RememberHold(DATA packet)
    {
        if (packet.Type == PackageType.Keyboard)
        {
            if ((packet.Kd.dwFlags & (int)WM.LLKHF.UP) != 0) keys.Remove(packet.Kd.wVk);
            else keys[packet.Kd.wVk] = packet.Kd;
        }
        else if (packet.Type == PackageType.Mouse)
        {
            int up = packet.Md.dwFlags switch
            {
                WM.WM_LBUTTONDOWN or WM.WM_LBUTTONUP => WM.WM_LBUTTONUP,
                WM.WM_RBUTTONDOWN or WM.WM_RBUTTONUP => WM.WM_RBUTTONUP,
                WM.WM_MBUTTONDOWN or WM.WM_MBUTTONUP => WM.WM_MBUTTONUP,
                WM.WM_XBUTTONDOWN or WM.WM_XBUTTONUP => WM.WM_XBUTTONUP,
                _ => 0,
            };
            if (up == 0) return;
            var key = (up, up == WM.WM_XBUTTONUP ? packet.Md.WheelDelta : 0);
            if (packet.Md.dwFlags == up) buttons.Remove(key);
            else buttons[key] = new MOUSEDATA { X = Event.MOVE_MOUSE_RELATIVE, Y = Event.MOVE_MOUSE_RELATIVE,
                dwFlags = up, WheelDelta = key.Item2 };
        }
    }

    private void StartLocked()
    {
        if (running || pending.Count == 0) return;
        running = true;
        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        try
        {
            while (true)
            {
                DATA packet;
                lock (gate)
                {
                    if (stopped || pending.First == null) { running = false; return; }
                    packet = pending.First.Value; pending.RemoveFirst();
                }
                lock (peer)
                {
                    lock (gate) if (stopped) { running = false; return; }
                    if (!owner.OwnsQueuedInput(peer)) { Stop(); lock (gate) running = false; return; }
                    packet.Id = Interlocked.Increment(ref Package.PackageID);
                    owner.TcpSend(peer, packet);
                }
            }
        }
        catch (Exception error)
        {
            Stop();
            owner.FailQueuedInput(peer);
            Logger.Log(error);
            lock (gate) running = false;
        }
    }

    internal void Stop() { lock (gate) StopLocked(); }
    private void StopLocked() { stopped = true; pending.Clear(); keys.Clear(); buttons.Clear(); }
}
