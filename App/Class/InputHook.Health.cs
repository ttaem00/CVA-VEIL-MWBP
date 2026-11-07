// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Runtime.InteropServices;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Class;

internal partial class InputHook
{
    private long mouseHookSeenAt, keyboardHookSeenAt;
    private long pendingMouseSince, pendingKeyboardSince, latestMouseRaw, latestKeyboardRaw;
    private long lastHealthRepair;
    private const int DeliveryGraceMilliseconds = 250;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { internal ushort Page, Usage; internal uint Flags; internal IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader { internal uint Type, Size; internal IntPtr Device, Parameter; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr input, uint command, out RawHeader header, ref uint size, uint headerSize);

    internal static bool ObserveDevices(IntPtr window) => RegisterRawInputDevices(
        [new() { Page = 1, Usage = 2, Flags = window == IntPtr.Zero ? 1u : 0x100u, Target = window },
         new() { Page = 1, Usage = 6, Flags = window == IntPtr.Zero ? 1u : 0x100u, Target = window }],
        2, (uint)Marshal.SizeOf<RawDevice>());

    internal void ObserveRawInput(IntPtr input)
    {
        uint size = (uint)Marshal.SizeOf<RawHeader>();
        if (GetRawInputData(input, 0x10000005, out var header, ref size, size) != size) return;
        // Only the device kind is needed. Do not read or store keys, text or positions.
        ObservePhysicalActivity(header.Type, Environment.TickCount64);
    }

    internal void ObservePhysicalActivity(uint kind, long now)
    {
        if (kind == 0)
        {
            latestMouseRaw = now;
            if (now - mouseHookSeenAt <= DeliveryGraceMilliseconds) pendingMouseSince = 0;
            else if (pendingMouseSince == 0) pendingMouseSince = now;
        }
        else if (kind == 1)
        {
            latestKeyboardRaw = now;
            if (now - keyboardHookSeenAt <= DeliveryGraceMilliseconds) pendingKeyboardSince = 0;
            else if (pendingKeyboardSince == 0) pendingKeyboardSince = now;
        }
    }

    private int MissedDeviceKinds(long now) =>
        (pendingMouseSince != 0 && now - pendingMouseSince >= DeliveryGraceMilliseconds && now - latestMouseRaw <= 4000
            && mouseHookSeenAt < latestMouseRaw - DeliveryGraceMilliseconds ? 1 : 0)
        | (pendingKeyboardSince != 0 && now - pendingKeyboardSince >= DeliveryGraceMilliseconds && now - latestKeyboardRaw <= 4000
            && keyboardHookSeenAt < latestKeyboardRaw - DeliveryGraceMilliseconds ? 2 : 0);

    internal bool HasMissedPhysicalInput(long now) => MissedDeviceKinds(now) != 0;

    internal bool RecoverMissedInput(bool force = false)
    {
        long now = Environment.TickCount64;
        if (Common.CurrentProcess.SessionId != NativeMethods.WTSGetActiveConsoleSessionId()) return false;
        // One deliberately suppressed remapper key is not evidence of lost capture.
        // Automatic repair requires independent keyboard AND mouse activity;
        // the OS-registered escape also covers a single missing hook.
        if ((!force && (MissedDeviceKinds(now) != 3 || now - lastHealthRepair < 2000)) || !WinAPI.IsMyDesktopActive()) return false;
        lastHealthRepair = now;
        RestoreLocalControl();
        Stop();
        Start(showError: false);
        pendingMouseSince = pendingKeyboardSince = 0;
        // A lost UP must not permanently consume the next ordinary Backspace.
        emergencyBackspaceDown = (emergencyBackspaceDown || force) && (NativeMethods.GetAsyncKeyState((IntPtr)VK.BACK) & 0x8000) != 0;
        bool restored = hMouseHook != 0 && hKeyboardHook != 0;
        Logger.Log($"Input capture recovery {(restored ? "completed" : "failed")}: {(force ? "emergency request" : "keyboard and mouse capture missed activity")}; returned to local control.");
        return restored;
    }
}
