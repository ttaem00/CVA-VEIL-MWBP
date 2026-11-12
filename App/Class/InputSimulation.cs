// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// <summary>
//     Keyboard/Mouse simulation.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using MouseWithoutBorders.Core;

[module: SuppressMessage("Microsoft.Usage", "CA1806:DoNotIgnoreMethodResults", Scope = "member", Target = "MouseWithoutBorders.InputSimulation.#InputProcessKeyEx(System.Int32,System.Int32,System.Boolean&)", MessageId = "MouseWithoutBorders.NativeMethods.LockWorkStation", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
    internal class InputSimulation
    {
        // Identifies our own injection, not input from CRD or another local source.
        internal static readonly System.IntPtr InjectedInputTag = new(0x4D574250);

        private sealed class RemoteHolds
        {
            internal TcpSk Owner;
            internal readonly Dictionary<long, NativeMethods.INPUT64> Releases = new();
        }
        private static readonly Dictionary<uint, RemoteHolds> remoteHolds = new();
        [ThreadStatic] private static RemoteHolds receiving;

        internal static void ReceiveHeldInput(TcpSk sender, Action apply)
        {
            Common.DoSomethingInTheInputSimulationThread(() =>
            {
                if (!remoteHolds.TryGetValue(sender.MachineId, out var state)) remoteHolds[sender.MachineId] = state = new();
                if (state.Owner != null && !IsConnected(state.Owner) && !ReleaseRemoteHolds(state, SendInputEx)) return;
                state.Owner = sender;
                var previous = receiving;
                try { receiving = state; apply(); }
                finally { receiving = previous; }
            });
        }

        private static bool IsConnected(TcpSk peer) => peer.Status == SocketStatus.Connected && peer.BackingSocket != null;

        internal static void PeerDisconnected(uint sender) => _ = Task.Run(() =>
            Common.DoSomethingInTheInputSimulationThread(() =>
            {
                if (remoteHolds.TryGetValue(sender, out var state) && state.Owner != null && !IsConnected(state.Owner))
                    ReleaseRemoteHolds(state, SendInputEx);
            }));

        private static bool ReleaseRemoteHolds(RemoteHolds state, Func<NativeMethods.INPUT64, uint> inject)
        {
            foreach (var entry in state.Releases.ToArray())
            {
                // Another live controller still owns this DOWN; its own UP will release it.
                if (remoteHolds.Values.Any(other => !ReferenceEquals(other, state) && other.Releases.ContainsKey(entry.Key))
                    || inject(entry.Value) != 0) state.Releases.Remove(entry.Key);
            }
            return state.Releases.Count == 0;
        }

        private InputSimulation()
        {
        }

        [ThreadStatic] private static long nextNativeRejectionLog;
        private static readonly NativeInputProgress nativeProgress = new();
        private static long lastNativeProgressCheck;

        // The existing settings timer samples counters; input never waits for the observer.
        internal sealed class NativeInputProgress
        {
            private long started, completed, previousCompleted;
            private bool previouslyPending, reported;
            internal void Enter() => System.Threading.Interlocked.Increment(ref started);
            internal void Exit() => System.Threading.Interlocked.Increment(ref completed);
            internal bool ObserveStall()
            {
                long done = System.Threading.Interlocked.Read(ref completed);
                bool pending = System.Threading.Interlocked.Read(ref started) != done;
                bool stalled = pending && previouslyPending && done == previousCompleted;
                bool notify = stalled && !reported;
                previousCompleted = done; previouslyPending = pending; reported = stalled;
                return notify;
            }
        }

        internal static void CheckNativeInputProgress()
        {
            long now = Environment.TickCount64;
            if (now - lastNativeProgressCheck < 1000) return;
            lastNativeProgressCheck = now;
            if (nativeProgress.ObserveStall())
                Logger.Log("Native input pending without completed calls across health checks; capture receiver stacks while input is blocked.");
        }

        private static uint SendNativeInput(NativeMethods.INPUT64 input)
        {
            uint sent;
            int error = 0;
            nativeProgress.Enter();
            try
            {
                sent = NativeMethods.SendInput64(1, [input], Marshal.SizeOf(input));
                if (sent == 0) error = Marshal.GetLastWin32Error();
            }
            finally { nativeProgress.Exit(); }
            if (sent == 0)
            {
                long now = Environment.TickCount64;
                if (now >= nextNativeRejectionLog)
                {
                    Logger.Log($"Native input rejected: Win32={error}; trying protected input.");
                    nextNativeRejectionLog = now + 5000;
                }
            }
            return sent;
        }

        private static uint SendInputEx(NativeMethods.INPUT64 input)
            => SendInputOnDesktop(input, ForegroundInputAccess.RequiresBroker()
                || (PressesOrScrolls(input) && TryPointerTarget(input, MachineStuff.DesktopBounds, out int x, out int y)
                    && ForegroundInputAccess.PointerTargetRequiresBroker(x, y)));

        private const int PressAndWheelFlags = (int)(NativeMethods.MOUSEEVENTF.LEFTDOWN | NativeMethods.MOUSEEVENTF.RIGHTDOWN
            | NativeMethods.MOUSEEVENTF.MIDDLEDOWN | NativeMethods.MOUSEEVENTF.XDOWN | NativeMethods.MOUSEEVENTF.WHEEL | NativeMethods.MOUSEEVENTF.HWHEEL);

        // A press or wheel is delivered to the window under the pointer. Movement and releases are
        // not looked up: movement stays on the direct path, and a release follows its press.
        internal static bool PressesOrScrolls(NativeMethods.INPUT64 input) =>
            input.type == 0 && (input.mi.dwFlags & PressAndWheelFlags) != 0;

        // Button and wheel packets carry their absolute position (0..65535 over the desktop).
        internal static bool TryPointerTarget(NativeMethods.INPUT64 input, MyRectangle desktop, out int x, out int y)
        {
            x = y = 0;
            const int absolute = (int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE);
            if (desktop == null || (input.mi.dwFlags & absolute) != absolute) return false;
            int width = desktop.Right - desktop.Left, height = desktop.Bottom - desktop.Top;
            if (width <= 0 || height <= 0) return false;
            x = desktop.Left + (int)((long)input.mi.dx * width / 65535);
            y = desktop.Top + (int)((long)input.mi.dy * height / 65535);
            return true;
        }

        private static uint SendInputOnDesktop(NativeMethods.INPUT64 input, bool protectedForeground)
        {
            Common.PaintCount = 0;

            if (input.type == 1) input.ki.dwExtraInfo = InjectedInputTag;
            else input.mi.dwExtraInfo = InjectedInputTag;
            // Native movement does not change held input; a broker round trip must not delay it.
            bool movementOnly = input.type == 0 && (input.mi.dwFlags & (int)NativeMethods.MOUSEEVENTF.MOVE) != 0
                && (input.mi.dwFlags & ~(int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE | NativeMethods.MOUSEEVENTF.VIRTUALDESK)) == 0;
            uint sent = movementOnly && !protectedForeground ? SendNativeInput(input) : 0;
            if (sent != 0) return sent;

            Common.DoSomethingInTheInputSimulationThread(() =>
            {
                try
                {
                if (protectedForeground || !movementOnly)
                {
                    if (protectedForeground || PrivilegedInput.HasHeldInput)
                    {
                        if (PrivilegedInput.TrySend(input, out sent)) return;
                        if (protectedForeground || PrivilegedInput.HasHeldInput) return;
                    }
                    sent = SendNativeInput(input);
                }
                if (sent == 0) PrivilegedInput.TrySend(input, out sent);
                }
                finally { if (sent != 0 && receiving != null) PrivilegedInput.TrackHeldInput(receiving.Releases, input); }
            });
            return sent;
        }

        internal static void SendKey(KEYBDDATA kd)
        {
            string log = string.Empty;
            NativeMethods.KEYEVENTF dwFlags = NativeMethods.KEYEVENTF.KEYDOWN;
            uint scanCode = 0;

            // http://msdn.microsoft.com/en-us/library/ms644967(VS.85).aspx
            if ((kd.dwFlags & (int)WM.LLKHF.UP) == (int)WM.LLKHF.UP)
            {
                dwFlags = NativeMethods.KEYEVENTF.KEYUP;
            }

            if ((kd.dwFlags & (int)WM.LLKHF.EXTENDED) == (int)WM.LLKHF.EXTENDED)
            {
                dwFlags |= NativeMethods.KEYEVENTF.EXTENDEDKEY;
            }

            scanCode = NativeMethods.MapVirtualKey((uint)kd.wVk, 0);

            InputProcessKeyEx(kd.wVk, kd.dwFlags, out bool eatKey);

            if (!eatKey)
            {
                NativeMethods.INPUT64 structInput;
                structInput = default;
                structInput.type = 1;
                structInput.ki.wScan = (short)scanCode;
                structInput.ki.time = 0;
                structInput.ki.wVk = (short)kd.wVk;
                structInput.ki.dwFlags = (int)dwFlags;

                SendInputEx(structInput);

            }

            log += "*"; // ((Keys)kd.wVk).ToString(CultureInfo.InvariantCulture);
            Logger.LogDebug(log);
        }

        // Md.X, Md.Y is from 0 to 65535
        internal static uint SendMouse(MOUSEDATA md)
        {
            NativeMethods.INPUT64 mouse_input = default;

            mouse_input.type = 0;

            if (md.dwFlags != WM.WM_MOUSEMOVE)
            {
                Logger.LogDebug($"InputSimulation.SendMouse: x = {md.X}, y = {md.Y}, WheelDelta = {md.WheelDelta}, dwFlags = {md.dwFlags}.");
            }

            mouse_input.mi = MouseInput(md);

            uint rv = SendInputEx(mouse_input);

            if (Common.MainFormVisible && !DragDrop.IsDropping)
            {
                Helper.MainFormDot();
            }

            return rv;
        }

        // Button/wheel packets carry their own arrival, even if no MOVE preceded them.
        internal static NativeMethods.MOUSEINPUT MouseInput(MOUSEDATA md)
        {
            var input = AbsoluteMouseInput(md.X, md.Y);
            input.mouseData = md.WheelDelta;
            switch (md.dwFlags)
            {
                case WM.WM_MOUSEMOVE:
                    break;
                case WM.WM_LBUTTONDOWN:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.LEFTDOWN;
                    break;
                case WM.WM_LBUTTONUP:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.LEFTUP;
                    break;
                case WM.WM_RBUTTONDOWN:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.RIGHTDOWN;
                    break;
                case WM.WM_RBUTTONUP:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.RIGHTUP;
                    break;
                case WM.WM_MBUTTONDOWN:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.MIDDLEDOWN;
                    break;
                case WM.WM_MBUTTONUP:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.MIDDLEUP;
                    break;
                case WM.WM_MOUSEWHEEL:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.WHEEL;
                    break;
                case WM.WM_MOUSEHWHEEL:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.HWHEEL;
                    break;
                case WM.WM_XBUTTONUP:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.XUP;
                    break;
                case WM.WM_XBUTTONDOWN:
                    input.dwFlags |= (int)NativeMethods.MOUSEEVENTF.XDOWN;
                    break;

                default:
                    return default;
            }

            return input;
        }

        internal static NativeMethods.MOUSEINPUT AbsoluteMouseInput(int x, int y) => new()
        {
            dx = x,
            dy = y,
            dwFlags = (int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE | NativeMethods.MOUSEEVENTF.VIRTUALDESK),
        };

        internal static void MoveMouseEx(int x, int y)
        {
            NativeMethods.INPUT64 mouse_input = default;
            mouse_input.type = 0;
            mouse_input.mi = AbsoluteMouseInput(x, y);

            Logger.LogDebug($"InputSimulation.MoveMouseEx: x = {x}, y = {y}.");

            SendInputEx(mouse_input);
        }

        // x, y is in pixel
        internal static void MoveMouse(int x, int y)
        {
            var bounds = MachineStuff.DesktopBounds;
            MoveMouseEx((int)((long)(x - bounds.Left) * 65535 / (bounds.Right - bounds.Left)),
                (int)((long)(y - bounds.Top) * 65535 / (bounds.Bottom - bounds.Top)));
        }

        // dx, dy is in pixel, relative
        internal static void MoveMouseRelative(int dx, int dy)
        {
            NativeMethods.INPUT64 mouse_input = default;
            mouse_input.type = 0;
            mouse_input.mi.dx = dx; // *65535 / Common.ScreenWidth;
            mouse_input.mi.dy = dy; // *65535 / Common.ScreenHeight;
            mouse_input.mi.mouseData = 0;
            mouse_input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE;

            Logger.LogDebug($"InputSimulation.MoveMouseRelative: x = {dx}, y = {dy}.");

            SendInputEx(mouse_input);
        }

        internal static NativeMethods.INPUT64[] OleDragCancelInputs()
        {
            var escapeDown = new NativeMethods.INPUT64
            {
                type = 1,
                ki = new NativeMethods.KEYBDINPUT { wVk = (short)VK.ESCAPE, dwExtraInfo = InjectedInputTag },
            };
            var escapeUp = escapeDown;
            escapeUp.ki.dwFlags = (int)NativeMethods.KEYEVENTF.KEYUP;
            return [escapeDown, escapeUp, new NativeMethods.INPUT64
            {
                type = 0,
                mi = new NativeMethods.MOUSEINPUT
                {
                    dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTUP,
                    dwExtraInfo = InjectedInputTag,
                },
            }];
        }

        internal static bool CancelLocalOleDrag()
        {
            // Called while the helper owns DragEnter, before hiding its drop target.
            var inputs = OleDragCancelInputs();
            return NativeMethods.SendInput64((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT64>()) == inputs.Length;
        }

        internal static void MouseUp()
        {
            NativeMethods.INPUT64 input = default;
            input.type = 0;
            input.mi.dx = 0;
            input.mi.dy = 0;
            input.mi.mouseData = 0;
            input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTUP;

            _ = SendInputEx(input);
            Logger.LogDebug("MouseUp() called");
        }

        private static bool winDown;
        private static bool ctrlDown;
        private static bool altDown;
        internal static readonly string[] Args = new string[] { "CAD" };

        private static void InputProcessKeyEx(int vkCode, int flags, out bool eatKey)
        {
            eatKey = false;

            if ((flags & (int)WM.LLKHF.UP) == (int)WM.LLKHF.UP)
            {
                switch ((VK)vkCode)
                {
                    case VK.LWIN:
                    case VK.RWIN:
                        winDown = false;
                        break;

                    case VK.LCONTROL:
                    case VK.RCONTROL:
                        ctrlDown = false;
                        break;

                    case VK.LMENU:
                    case VK.RMENU:
                        altDown = false;
                        break;

                    default:
                        break;
                }
            }
            else
            {
                switch ((VK)vkCode)
                {
                    case VK.LWIN:
                    case VK.RWIN:
                        winDown = true;
                        break;

                    case VK.LCONTROL:
                    case VK.RCONTROL:
                        ctrlDown = true;
                        break;

                    case VK.LMENU:
                    case VK.RMENU:
                        altDown = true;
                        break;

                    case VK.DELETE:
                        if (ctrlDown && altDown)
                        {
                            ctrlDown = altDown = false;
                            eatKey = true;
                            InitAndCleanup.ReleaseAllKeys();
                        }

                        break;

                    case (VK)'L':
                        if (winDown)
                        {
                            winDown = false;
                            eatKey = true;
                            InitAndCleanup.ReleaseAllKeys();
                            uint rv = NativeMethods.LockWorkStation();
                            Logger.LogDebug("LockWorkStation returned " + rv.ToString(CultureInfo.CurrentCulture));
                        }

                        break;

                    case VK.END:
                        if (ctrlDown && altDown)
                        {
                            ctrlDown = altDown = false;
                            new ServiceController("MouseWithoutBordersSvc").Start(Args);
                        }

                        break;

                    default:
                        break;
                }
            }
        }

        internal static void ResetSystemKeyFlags()
        {
            ctrlDown = winDown = altDown = false;
        }
    }
}
