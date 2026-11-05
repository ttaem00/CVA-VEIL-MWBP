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

using MouseWithoutBorders.Core;

[module: SuppressMessage("Microsoft.Usage", "CA1806:DoNotIgnoreMethodResults", Scope = "member", Target = "MouseWithoutBorders.InputSimulation.#InputProcessKeyEx(System.Int32,System.Int32,System.Boolean&)", MessageId = "MouseWithoutBorders.NativeMethods.LockWorkStation", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
    internal class InputSimulation
    {
        // Identifies our own injection, not input from CRD or another local source.
        internal static readonly System.IntPtr InjectedInputTag = new(0x4D574250);

        private InputSimulation()
        {
        }

        private static uint SendInputEx(NativeMethods.INPUT64 input)
        {
            Common.PaintCount = 0;

            if (input.type == 1) input.ki.dwExtraInfo = InjectedInputTag;
            else input.mi.dwExtraInfo = InjectedInputTag;
            // Native movement does not change held input; a broker round trip must not delay it.
            bool movementOnly = input.type == 0 && (input.mi.dwFlags & (int)NativeMethods.MOUSEEVENTF.MOVE) != 0
                && (input.mi.dwFlags & ~(int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE | NativeMethods.MOUSEEVENTF.VIRTUALDESK)) == 0;
            uint sent = movementOnly ? NativeMethods.SendInput64(1, [input], Marshal.SizeOf(input)) : 0;
            if (sent != 0) return sent;

            Common.DoSomethingInTheInputSimulationThread(() =>
            {
                if (!movementOnly)
                {
                    if (PrivilegedInput.HasHeldInput)
                    {
                        if (PrivilegedInput.TrySend(input, out sent)) return;
                        if (PrivilegedInput.HasHeldInput) return;
                    }
                    sent = NativeMethods.SendInput64(1, [input], Marshal.SizeOf(input));
                }
                if (sent == 0) PrivilegedInput.TrySend(input, out sent);
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
