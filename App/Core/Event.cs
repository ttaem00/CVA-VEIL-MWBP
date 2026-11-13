// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;

// <summary>
//     Keyboard/Mouse hook callback implementation.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
namespace MouseWithoutBorders.Core;

internal static class Event
{
    private static readonly DATA KeybdPackage = new();
    private static readonly DATA MousePackage = new();
#pragma warning disable SA1307 // Accessible fields should begin with upper-case names
    internal static ulong inputEventCount;
    internal static ulong invalidPackageCount;
#pragma warning restore SA1307
    internal static int MOVE_MOUSE_RELATIVE = 100000;
    internal static int XY_BY_PIXEL = 300000;

    static Event()
    {
    }

    internal static ulong InvalidPackageCount
    {
        get => Event.invalidPackageCount;
        set => Event.invalidPackageCount = value;
    }

    internal static ulong InputEventCount
    {
        get => Event.inputEventCount;
        set => Event.inputEventCount = value;
    }

    internal static ulong RealInputEventCount
    {
        get;
        set;
    }

    internal static int myLastX;
    internal static int myLastY;
    private static MonitorKey? activeMonitor;
    private static Point monitorPoint;
    private static bool activationPending;

    internal static void SetMouseSwitchActivation(bool active) =>
        activationPending = active && Common.DesMachineID == Common.MachineID;

    internal static MonitorPosition ProbeActivationEdge(MonitorLayout layout, MonitorKey current, Point point, Func<uint, bool> canEnterPeer)
    {
        var monitor = layout.GetMonitor(current);
        var bounds = monitor.NativeBounds;
        int dx = point.X == bounds.Left ? 1 : point.X == bounds.Right - 1 ? -1 : 0;
        int dy = point.Y == bounds.Top ? 1 : point.Y == bounds.Bottom - 1 ? -1 : 0;
        var horizontal = layout.Route(current, new((int)Math.Clamp((long)point.X + dx, bounds.Left, bounds.Right - 1), point.Y), point, canEnterPeer);
        var vertical = layout.Route(current, new(point.X, (int)Math.Clamp((long)point.Y + dy, bounds.Top, bounds.Bottom - 1)), point, canEnterPeer);
        if (horizontal.Monitor.Key == current) return vertical;
        if (vertical.Monitor.Key == current) return horizontal;
        // A zero-delta corner provides no direction with which to choose between two exits.
        return new(monitor, point);
    }

    internal static MonitorPosition RouteMouseMove(MonitorLayout layout, MonitorKey current, Point previous, Point proposed, Func<uint, bool> canEnterPeer)
    {
        var position = activationPending && previous == proposed ? ProbeActivationEdge(layout, current, proposed, canEnterPeer)
            : layout.Route(current, previous, proposed, canEnterPeer);
        if (position.Monitor.Key != current) activationPending = false;
        return position;
    }

    // The controller owns the target position; Windows adjacency never chooses a peer.
    internal static void ResetMonitorPointer(Point nativePoint)
    {
        activeMonitor = null;
        monitorPoint = nativePoint;
        foreach (var monitor in MonitorTopology.Monitors)
            if (monitor.Key.Peer == (uint)Common.MachineID && monitor.NativeBounds.Contains(nativePoint))
            { activeMonitor = monitor.Key; break; }
    }

    // Do not wait for reconnects, a UI message, or SendInput on the hook thread.
    internal static void ReturnToLocalControl(Point nativePoint)
    {
        MachineStuff.NewDesMachineID = Common.DesMachineID = Common.MachineID;
        MachineStuff.SwitchLocation.Count = 0;
        activationPending = false;
        Common.LastPos = nativePoint;
        ResetMonitorPointer(nativePoint);
    }

    private static bool EnsureInputDestination(Point nativePoint, bool needsMonitor)
    {
        var target = Common.DesMachineID;
        if (target == Common.MachineID || target is ID.ALL or ID.NONE) return true;
        if (Common.IsConnectedTo(target))
        {
            if (!needsMonitor) return true;
            foreach (var monitor in MonitorTopology.Monitors)
                if (monitor.Key.Peer == (uint)target) return true;
        }
        ReturnToLocalControl(nativePoint);
        Logger.Log("Input destination unavailable; restored local control.");
        return false;
    }

    internal static bool MouseEvent(MOUSEDATA e, int dx, int dy)
    {
        Common.PaintCount = 0;
        if (!EnsureInputDestination(new Point(e.X, e.Y), true)) return false;
        var layout = MonitorTopology.Layout;
        uint peer = (uint)Common.DesMachineID;
        if (e.dwFlags == WM.WM_MOUSEMOVE && peer != (uint)Common.MachineID) activationPending = false;
        if (Common.DesMachineID == ID.ALL)
        {
            MousePackage.Des = ID.ALL;
            MousePackage.Type = PackageType.Mouse;
            MousePackage.Md = e;
            var desktop = MachineStuff.DesktopBounds;
            MousePackage.Md.X = (int)((long)(e.X - desktop.Left) * 65535 / (desktop.Right - desktop.Left));
            MousePackage.Md.Y = (int)((long)(e.Y - desktop.Top) * 65535 / (desktop.Bottom - desktop.Top));
            Common.SkSend(MousePackage, null, false);
            Common.LastPos = new Point(e.X, e.Y);
            return false;
        }

        bool allowSharing = IsSwitchingByMouseEnabled();
        if (peer == (uint)Common.MachineID && !allowSharing)
        {
            activationPending = false;
            var point = new Point(e.X, e.Y);
            ResetMonitorPointer(point);
            Common.LastPos = point;
            return false;
        }

        bool relative = Setting.Values.MoveMouseRelatively;
        if (relative && !allowSharing)
        {
            // The held key moves the layout position. Ordinary gameplay only sends distances.
            SendRelativeMouse((ID)peer, e, dx, dy);
            return Common.DesMachineID != Common.MachineID;
        }

        MonitorPlacement current = default;
        bool found = false;
        foreach (var monitor in layout.Monitors)
            if (monitor.Key.Peer == peer && (monitor.Key == activeMonitor || !found))
            { current = monitor; found = true; if (monitor.Key == activeMonitor) break; }
        if (!found) return peer != (uint)Common.MachineID;
        if (activeMonitor != current.Key)
        {
            monitorPoint = new Point(current.NativeBounds.Left + current.NativeBounds.Width / 2,
                current.NativeBounds.Top + current.NativeBounds.Height / 2);
            if (peer == (uint)Common.MachineID)
            {
                foreach (var monitor in layout.Monitors)
                    if (monitor.Key.Peer == peer && monitor.NativeBounds.Contains(Common.LastPos))
                    { current = monitor; monitorPoint = Common.LastPos; break; }
            }
            activeMonitor = current.Key;
        }

        var proposed = peer == (uint)Common.MachineID ? new Point(e.X, e.Y)
            : new Point(monitorPoint.X + dx, monitorPoint.Y + dy);
        MonitorPosition position;
        if (e.dwFlags == WM.WM_MOUSEMOVE)
        {
            position = RouteMouseMove(layout, current.Key, monitorPoint, proposed, CanEnterPeer);
            myLastX = e.X; myLastY = e.Y;
            if (position.Monitor.Key.Peer != peer &&
                !PrepareToSwitchToMachine((ID)position.Monitor.Key.Peer,
                    new Point(XY_BY_PIXEL + position.NativePoint.X, XY_BY_PIXEL + position.NativePoint.Y)))
                position = new(current, new Point(Math.Clamp(proposed.X, current.NativeBounds.Left, current.NativeBounds.Right - 1),
                    Math.Clamp(proposed.Y, current.NativeBounds.Top, current.NativeBounds.Bottom - 1)));
        }
        else position = layout.Route(current.Key, monitorPoint, monitorPoint);

        activeMonitor = position.Monitor.Key;
        monitorPoint = position.NativePoint;
        bool changedPeer = activeMonitor.Value.Peer != peer;
        if (changedPeer)
        {
            // The hook completes local arrival before exposing the following physical click/key.
            MachineStuff.SwitchLocation.Count = 0;
        }

        if (activeMonitor.Value.Peer == (uint)Common.MachineID)
        {
            bool warped = e.dwFlags == WM.WM_MOUSEMOVE && monitorPoint != proposed;
            if (changedPeer || warped) NativeMethods.SetCursorPos(monitorPoint.X, monitorPoint.Y);
            if (changedPeer) Helper.MainFormDot();
            Common.LastPos = monitorPoint;
            return changedPeer || warped;
        }

        if (relative)
        {
            // The crossing movement belongs to the previous PC.
            if (changedPeer) Common.HideMouseCursor();
            else SendRelativeMouse((ID)peer, e, dx, dy);
            return Common.DesMachineID != Common.MachineID;
        }

        Rectangle targetDesktop = position.Monitor.NativeBounds;
        foreach (var monitor in layout.Monitors)
            if (monitor.Key.Peer == activeMonitor.Value.Peer)
                targetDesktop = Rectangle.Union(targetDesktop, monitor.NativeBounds);
        MousePackage.Des = (ID)activeMonitor.Value.Peer;
        MousePackage.Type = PackageType.Mouse;
        MousePackage.Md = e;
        MousePackage.Md.X = (int)((long)(monitorPoint.X - targetDesktop.Left) * 65535 / targetDesktop.Width);
        MousePackage.Md.Y = (int)((long)(monitorPoint.Y - targetDesktop.Top) * 65535 / targetDesktop.Height);
        Common.SkSend(MousePackage, null, false);
        if (Common.DesMachineID == Common.MachineID) return false;
        if (changedPeer) Common.HideMouseCursor();
        return true;
    }

    private static void SendRelativeMouse(ID peer, MOUSEDATA mouse, int dx, int dy)
    {
        bool move = mouse.dwFlags == WM.WM_MOUSEMOVE;
        if (move)
        {
            if (dx == 0 && dy == 0) return;
            myLastX = mouse.X;
            myLastY = mouse.Y;
        }

        MousePackage.Des = peer;
        MousePackage.Type = PackageType.Mouse;
        MousePackage.Md = mouse;
        MousePackage.Md.X = move ? RelativeMouse.Encode(Math.Clamp(dx, -RelativeMouse.MaxDistance, RelativeMouse.MaxDistance)) : MOVE_MOUSE_RELATIVE;
        MousePackage.Md.Y = move ? RelativeMouse.Encode(Math.Clamp(dy, -RelativeMouse.MaxDistance, RelativeMouse.MaxDistance)) : MOVE_MOUSE_RELATIVE;
        Common.SkSend(MousePackage, null, false);
    }

    internal static bool IsSwitchingByMouseEnabled()
    {
        return (EasyMouseOption)Setting.Values.EasyMouse == EasyMouseOption.Enable || InputHook.EasyMouseKeyDown;
    }

    private static bool CanEnterPeer(uint peer) => IsSwitchingByMouseEnabled()
        && Common.IsEasyMouseSwitchAllowed() && Common.IsConnectedTo((ID)peer);

    internal static bool PrepareToSwitchToMachine(ID newDesMachineID, Point desMachineXY)
    {
        Logger.LogDebug($"PrepareToSwitchToMachine: newDesMachineID = {newDesMachineID}, desMachineXY = {desMachineXY}");

        if (MachineStuff.desMachineID == ID.ALL)
        {
            return false;
        }

        MachineStuff.lastJump = Common.GetTick();

        string newDesMachineName = MachineStuff.NameFromID(newDesMachineID);

        if (!Common.IsConnectedTo(newDesMachineID))
        {
            // Connection lost, cancel switching
            Logger.LogDebug("No active connection found for " + newDesMachineName);

            // ShowToolTip("No active connection found for [" + newDesMachineName + "]!", 500);
        }
        else
        {
            MachineStuff.newDesMachineID = newDesMachineID;
            MachineStuff.SwitchLocation.X = desMachineXY.X;
            MachineStuff.SwitchLocation.Y = desMachineXY.Y;
            MachineStuff.SwitchLocation.ResetCount();
            _ = Common.EvSwitch.Set();

            // PostMessage(mainForm.Handle, WM_SWITCH, IntPtr.Zero, IntPtr.Zero);
            if (newDesMachineID != DragDrop.DragMachine)
            {
                if (!DragDrop.IsDragging && !DragDrop.IsDropping)
                {
                    if (DragDrop.MouseDown && !Common.RunOnLogonDesktop && !Common.RunOnScrSaverDesktop)
                    {
                        DragDrop.DragDropStep02();
                    }
                }
                else if (DragDrop.DragMachine != (ID)1)
                {
                    DragDrop.ChangeDropMachine();
                }
            }
            else
            {
                DragDrop.DragDropStep11();
            }

            // Change des machine
            if (MachineStuff.desMachineID != newDesMachineID)
            {
                Logger.LogDebug("MouseEvent: Switching to new machine:" + newDesMachineName);

                ID previousMachine = MachineStuff.desMachineID;
                Common.DesMachineID = newDesMachineID;
                // The previous peer must not hold up input to the new peer.
                if (newDesMachineID != ID.ALL && previousMachine != Common.MachineID)
                    Common.SendPackage(previousMachine, PackageType.HideMouse);

                Clipboard.OnMachineSwitched();

                _ = Interlocked.Increment(ref Common.switchCount);
            }
        }
        return Common.DesMachineID == newDesMachineID;
    }

    internal static void SaveSwitchCount()
    {
        if (Common.SwitchCount > 0)
        {
            _ = Task.Run(() =>
            {
                Setting.Values.SwitchCount += Common.SwitchCount;
                _ = Interlocked.Exchange(ref Common.switchCount, 0);
            });
        }
    }

    internal static void KeybdEvent(KEYBDDATA e)
    {
        try
        {
            Common.PaintCount = 0;
            if (MachineStuff.desMachineID != MachineStuff.newDesMachineID)
            {
                Logger.LogDebug("KeybdEvent: Switching to new machine...");
                Common.DesMachineID = MachineStuff.newDesMachineID;
            }

            if (!EnsureInputDestination(Common.LastPos, false)) return;

            if (MachineStuff.desMachineID != Common.MachineID)
            {
                KeybdPackage.Des = MachineStuff.desMachineID;
                KeybdPackage.Type = PackageType.Keyboard;
                KeybdPackage.Kd = e;
                KeybdPackage.DateTime = Common.GetTick();
                Common.SkSend(KeybdPackage, null, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
        }
    }
}
