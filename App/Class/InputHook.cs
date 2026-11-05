// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// <summary>
//     Keyboard/Mouse hook callbacks, pre-process before calling to routines in Common.Event.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using Microsoft.PowerToys.Settings.UI.Library;
using MouseWithoutBorders.Core;

[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.InputHook.#MouseHookProc(System.Int32,System.Int32,System.IntPtr)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Scope = "member", Target = "MouseWithoutBorders.InputHook.#ProcessKeyEx(System.Int32,System.Int32)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Globalization", "CA1300:SpecifyMessageBoxOptions", Scope = "member", Target = "MouseWithoutBorders.InputHook.#Start()", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
    internal class InputHook
    {
        internal delegate bool MouseEvHandler(MOUSEDATA e, int dx, int dy);

        internal delegate void KeybdEvHandler(KEYBDDATA e);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHookStruct
        {
            internal NativeMethods.POINT Pt;
            internal int Hwnd;
            internal int WHitTestCode;
            internal int DwExtraInfo;
        }

        // http://msdn.microsoft.com/en-us/library/ms644970(VS.85).aspx
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseLLHookStruct
        {
            internal NativeMethods.POINT Pt;
            internal int MouseData;
            internal int Flags;
            internal int Time;
            internal IntPtr DwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHookStruct
        {
            internal int VkCode;
            internal int ScanCode;
            internal int Flags;
            internal int Time;
            internal IntPtr DwExtraInfo;
        }

        internal event MouseEvHandler MouseEvent;

        internal event KeybdEvHandler KeyboardEvent;

        private int hMouseHook;
        private int hKeyboardHook;
        private byte localModifiers;
        private static NativeMethods.HookProc mouseHookProcedure;
        private static NativeMethods.HookProc keyboardHookProcedure;

        private static MouseLLHookStruct mouseHookStruct;
        private static KeyboardHookStruct keyboardHookStruct;
        private static MOUSEDATA hookCallbackMouseData;
        private static KEYBDDATA hookCallbackKeybdData;

        private static bool altDown;

        internal static bool CtrlDown { get; private set; }

        private static readonly HeldKeyChord easyMouseKeys = new();
        private static volatile bool recordingKeys;
        internal static volatile Action<int, bool> KeyRecorder;
        internal static bool RecordingKeys
        {
            get => recordingKeys;
            set => Common.DoSomethingInTheInputCallbackThread(() =>
            {
                if (value)
                {
                    // Finish remote DOWNs before recording stops forwarding input to that PC.
                    foreach (int key in easyMouseKeys.DownKeys)
                    {
                        var release = new KEYBDDATA { wVk = (short)key, dwFlags = (int)WM.LLKHF.UP };
                        Common.Hook?.KeyboardEvent?.Invoke(release);
                    }
                    easyMouseKeys.Clear();
                    Event.SetMouseSwitchActivation(false);
                    Common.Hook?.ResetLastSwitchKeys();
                }
                recordingKeys = value;
            });
        }

        internal static bool EasyMouseKeyDown => easyMouseKeys.IsActive(
            (EasyMouseOption)Setting.Values.EasyMouse, Setting.Values.EasyMouseActivationKeys);

        internal InputHook()
        {
            Start();
        }

        ~InputHook()
        {
            Stop();
        }

        internal void Start()
        {
            int le;
            bool er = false;
            var initialPosition = new System.Drawing.Point();
            NativeMethods.GetCursorPos(ref initialPosition);
            Common.LastPos = initialPosition;
            Event.ResetMonitorPointer(initialPosition);

            // Install Mouse Hook
            mouseHookProcedure = new NativeMethods.HookProc(MouseHookProc);
            hMouseHook = NativeMethods.SetWindowsHookEx(
                WM.WH_MOUSE_LL,
                mouseHookProcedure,
#if PORTABLE_SINGLE_FILE
                NativeMethods.GetModuleHandle(null),
#else
                Marshal.GetHINSTANCE(
                    Assembly.GetExecutingAssembly().GetModules()[0]),
#endif
                0);

            if (hMouseHook == 0)
            {
                le = Marshal.GetLastWin32Error();
                Logger.Log("Error installing Mouse hook: " + le.ToString(CultureInfo.CurrentCulture));
                er = true;
                Stop();
            }

            // Install Keyboard Hook
            keyboardHookProcedure = new NativeMethods.HookProc(KeyboardHookProc);
            hKeyboardHook = NativeMethods.SetWindowsHookEx(
                WM.WH_KEYBOARD_LL,
                keyboardHookProcedure,
#if PORTABLE_SINGLE_FILE
                NativeMethods.GetModuleHandle(null),
#else
                Marshal.GetHINSTANCE(
                    Assembly.GetExecutingAssembly().GetModules()[0]),
#endif
                0);
            if (hKeyboardHook == 0)
            {
                le = Marshal.GetLastWin32Error();
                Logger.Log("Error installing keyboard hook: " + le.ToString(CultureInfo.CurrentCulture));
                er = true;
                Stop();
            }

            if (er)
            {
                if (!Common.RunOnLogonDesktop && !Common.RunOnScrSaverDesktop)
                {
                    _ = MessageBox.Show(
                        "Error installing keyboard/Mouse hook!",
                        Application.ProductName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            else
            {
                // Seed native holds after installation, before returning to the hook message loop.
                localModifiers = 0;
                foreach (VK key in new[] { VK.LSHIFT, VK.RSHIFT, VK.LCONTROL, VK.RCONTROL, VK.LMENU, VK.RMENU, VK.LWIN, VK.RWIN })
                    if ((NativeMethods.GetAsyncKeyState((IntPtr)key) & 0x8000) != 0) localModifiers |= ModifierBit((int)key);
                Common.InitLastInputEventCount();
            }
        }

        internal void Stop()
        {
            easyMouseKeys.Clear();
            Event.SetMouseSwitchActivation(false);
            if (hMouseHook != 0)
            {
                int retMouse = NativeMethods.UnhookWindowsHookEx(hMouseHook);
                hMouseHook = 0;
                if (retMouse == 0)
                {
                    int errorCode = Marshal.GetLastWin32Error();

                    // throw new Win32Exception(errorCode);
                    Logger.Log("Exception uninstalling Mouse hook, error code: " + errorCode.ToString(CultureInfo.CurrentCulture));
                }
            }

            if (hKeyboardHook != 0)
            {
                int retKeyboard = NativeMethods.UnhookWindowsHookEx(hKeyboardHook);
                hKeyboardHook = 0;
                if (retKeyboard == 0)
                {
                    int errorCode = Marshal.GetLastWin32Error();

                    // throw new Win32Exception(errorCode);
                    Logger.Log("Exception uninstalling keyboard hook, error code: " + errorCode.ToString(CultureInfo.CurrentCulture));
                }
            }
        }

        // Better performance, compared to Marshal.PtrToStructure.
        private static MouseLLHookStruct LParamToMouseLLHookStruct(IntPtr lParam)
        {
            unsafe
            {
                return *(MouseLLHookStruct*)lParam;
            }
        }

        private static KeyboardHookStruct LParamToKeyboardHookStruct(IntPtr lParam)
        {
            unsafe
            {
                return *(KeyboardHookStruct*)lParam;
            }
        }

        private int MouseHookProc(int nCode, int wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(hMouseHook, nCode, wParam, lParam);
            mouseHookStruct = LParamToMouseLLHookStruct(lParam);
            Event.InputEventCount++;
            if ((mouseHookStruct.Flags & 1) != 0 && mouseHookStruct.DwExtraInfo == InputSimulation.InjectedInputTag)
            {
                if (wParam == WM.WM_MOUSEMOVE && Common.DesMachineID == Common.MachineID)
                    Event.ResetMonitorPointer(new System.Drawing.Point(mouseHookStruct.Pt.x, mouseHookStruct.Pt.y));
                return NativeMethods.CallNextHookEx(hMouseHook, nCode, wParam, lParam);
            }
            Event.RealInputEventCount++;
            try
            {
                if (DragDrop.HandleCancelMouse(wParam)) return 1;
                hookCallbackMouseData = new MOUSEDATA
                {
                    X = mouseHookStruct.Pt.x, Y = mouseHookStruct.Pt.y, dwFlags = wParam,
                    WheelDelta = (short)((mouseHookStruct.MouseData >> 16) & 0xffff),
                };
                bool suppress = MouseEvent?.Invoke(hookCallbackMouseData,
                    mouseHookStruct.Pt.x - Common.LastPos.X, mouseHookStruct.Pt.y - Common.LastPos.Y) ?? false;
                DragDrop.DragDropStep01(wParam);
                DragDrop.DragDropStep09(wParam);
                return suppress ? 1 : NativeMethods.CallNextHookEx(hMouseHook, nCode, wParam, lParam);
            }
            catch (Exception e)
            {
                Logger.Log(e);
                return NativeMethods.CallNextHookEx(hMouseHook, nCode, wParam, lParam);
            }
        }

        private int KeyboardHookProc(int nCode, int wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeMethods.CallNextHookEx(hKeyboardHook, nCode, wParam, lParam);
            keyboardHookStruct = LParamToKeyboardHookStruct(lParam);
            byte modifier = ModifierBit(keyboardHookStruct.VkCode);
            bool up = (keyboardHookStruct.Flags & (int)WM.LLKHF.UP) != 0;
            Event.InputEventCount++;
            if (RecordingKeys)
            {
                easyMouseKeys.Update(keyboardHookStruct.VkCode, !up);
                var recorder = KeyRecorder;
                if (recorder != null)
                {
                    recorder(keyboardHookStruct.VkCode, !up);
                    if (!up) return 1;
                }
                // UP also finishes any Windows DOWN that preceded the recording dialog.
                return PassKeyboardEvent(nCode, wParam, lParam, modifier, up);
            }

            if ((keyboardHookStruct.Flags & 0x10) != 0 && keyboardHookStruct.DwExtraInfo == InputSimulation.InjectedInputTag)
                return PassKeyboardEvent(nCode, wParam, lParam, modifier, up);

            Event.RealInputEventCount++;

            hookCallbackKeybdData.dwFlags = keyboardHookStruct.Flags;
            hookCallbackKeybdData.wVk = (short)keyboardHookStruct.VkCode;

            if (KeyboardEvent != null)
            {
                if (!ProcessKeyEx(keyboardHookStruct.VkCode, keyboardHookStruct.Flags, hookCallbackKeybdData))
                {
                    return 1;
                }

                KeyboardEvent(hookCallbackKeybdData);
            }

            if (Common.DesMachineID == ID.NONE || Common.DesMachineID == ID.ALL || Common.DesMachineID == Common.MachineID
                || up && (localModifiers & modifier) != 0)
            {
                return PassKeyboardEvent(nCode, wParam, lParam, modifier, up);
            }
            else
            {
                return 1;
            }
        }

        private int PassKeyboardEvent(int nCode, int wParam, IntPtr lParam, byte modifier, bool up)
        {
            localModifiers = up ? (byte)(localModifiers & ~modifier) : (byte)(localModifiers | modifier);
            return NativeMethods.CallNextHookEx(hKeyboardHook, nCode, wParam, lParam);
        }

        private static byte ModifierBit(int key) => (VK)key switch
        {
            VK.SHIFT or VK.LSHIFT => 1,
            VK.RSHIFT => 2,
            VK.CONTROL or VK.LCONTROL => 4,
            VK.RCONTROL => 8,
            VK.MENU or VK.LMENU => 16,
            VK.RMENU => 32,
            VK.LWIN => 64,
            VK.RWIN => 128,
            _ => 0,
        };

        private bool ProcessKeyEx(int vkCode, int flags, KEYBDDATA hookCallbackKeybdData)
        {
            bool wasActive = EasyMouseKeyDown;
            bool firstPress = easyMouseKeys.Update(vkCode, (flags & (int)WM.LLKHF.UP) == 0);
            bool active = EasyMouseKeyDown;
            if (wasActive != active) Event.SetMouseSwitchActivation(active);
            if ((flags & (int)WM.LLKHF.UP) == (int)WM.LLKHF.UP)
            {
                switch ((VK)vkCode)
                {
                    case VK.LCONTROL:
                    case VK.RCONTROL:
                        CtrlDown = false;
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
                    case VK.LCONTROL:
                    case VK.RCONTROL:
                        CtrlDown = true;
                        break;

                    case VK.LMENU:
                    case VK.RMENU:
                        altDown = true;
                        break;

                    case VK.DELETE:
                        if (CtrlDown && altDown)
                        {
                            CtrlDown = altDown = false;
                            KeyboardEvent(hookCallbackKeybdData);

                            if (Common.DesMachineID != ID.ALL)
                            {
                                MachineStuff.SwitchToMachine(Common.MachineName.Trim());
                            }

                            /*
#if CUSTOMIZE_LOGON_SCREEN
                           Common.DoSomethingInUIThread(delegate()
                           {
                               Common.MainForm.LoadNewLogonBackground();
                           });
#endif
* */
                        }

                        break;

                    case VK.ESCAPE:
                        if (Common.IsTopMostMessageNotNull())
                        {
                            Common.HideTopMostMessage();
                        }

                        break;

                    default:
                        break;
                }
                return ProcessHotKeys(firstPress);
            }

            return true;
        }

        private static long lastHotKeyLockMachine;

        private bool ProcessHotKeys(bool firstPress)
        {
            if (RecordingKeys || !Setting.Values.KeyboardShortcutsEnabled) return true;
            KeyboardAction? match = null;
            foreach (var action in Enum.GetValues<KeyboardAction>())
                if (easyMouseKeys.MatchesExact(Setting.Values.GetShortcutKeys(action))) { match = action; break; }
            if (match == null) return true;
            if (!firstPress) return false; // Consume auto-repeat without cycling through PCs again.
            if (match <= KeyboardAction.Computer4) return !Switch2((int)match.Value);
            if (match == KeyboardAction.NextComputer)
            {
                string name = MachineStuff.NextMachineName();
                if (name != null) MachineStuff.SwitchToMachine(name);
            }
            else if (match == KeyboardAction.AllComputers)
            {
                ResetLastSwitchKeys();
                MachineStuff.SwitchToMultipleMode(Common.DesMachineID != ID.ALL, true);
            }
            else if (match == KeyboardAction.ToggleEdgeSwitching)
            {
                if (!Common.RunOnLogonDesktop && !Common.RunOnScrSaverDesktop)
                {
                    var option = (EasyMouseOption)Setting.Values.EasyMouse;
                    if (option is EasyMouseOption.Disable or EasyMouseOption.Enable)
                    {
                        Setting.Values.EasyMouse = (int)(option == EasyMouseOption.Disable ? EasyMouseOption.Enable : EasyMouseOption.Disable);
                        Setting.Values.CommitSharedSettings();
                        MachineStuff.SendMachineMatrix();
                    }
                }
            }
            else if (match == KeyboardAction.Reconnect)
            {
                Common.ShowToolTip("Reconnecting...", 2000);
                Common.LastReconnectByHotKeyTime = Common.GetTick();
                InitAndCleanup.PleaseReopenSocket = InitAndCleanup.REOPEN_WHEN_HOTKEY;
            }
            else if (match == KeyboardAction.LockComputers && !Common.RunOnLogonDesktop && !Common.RunOnScrSaverDesktop)
            {
                bool all = Common.GetTick() - lastHotKeyLockMachine < 500;
                if (all) MachineStuff.SwitchToMultipleMode(true, true);
                // Send the Windows lock command, independently of each peer's shortcut assignment.
                foreach (short key in new short[] { (short)VK.LWIN, (short)'L' })
                    KeyboardEvent(new KEYBDDATA { wVk = key });
                foreach (short key in new short[] { (short)'L', (short)VK.LWIN })
                    KeyboardEvent(new KEYBDDATA { wVk = key, dwFlags = (int)WM.LLKHF.UP });
                if (all)
                {
                    MachineStuff.SwitchToMultipleMode(false, true);
                    _ = NativeMethods.LockWorkStation();
                }
                lastHotKeyLockMachine = Common.GetTick();
            }
            return false;
        }
        private static bool Switch2(int index)
        {
            if (MachineStuff.MachineMatrix != null && MachineStuff.MachineMatrix.Length > index)
            {
                string mcName = MachineStuff.MachineMatrix[index].Trim();
                if (!string.IsNullOrEmpty(mcName))
                {
                    MachineStuff.SwitchToMachine(mcName);

                    if (!Common.RunOnLogonDesktop && !Common.RunOnScrSaverDesktop)
                    {
                        Common.ShowToolTip(Ui.Format("Control switched to {0}.", mcName), 3000);
                    }

                    return true;
                }
            }

            return false;
        }

        internal void ResetLastSwitchKeys()
        {
            CtrlDown = altDown = false;
        }
    }
}
