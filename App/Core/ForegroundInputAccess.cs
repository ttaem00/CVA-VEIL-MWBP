// Copyright (c) Microsoft Corporation
// Licensed under the MIT license. See LICENSE in the project root.
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

// SendInput's insertion count does not establish delivery to a higher-integrity window.
// Inspect foreground ownership before injection, querying tokens only on process changes.
internal static class ForegroundInputAccess
{
    private static readonly int? ownIntegrity = ReadOwnIntegrity();
    [ThreadStatic] private static uint cachedProcessId;
    [ThreadStatic] private static SafeProcessHandle cachedProcess;
    [ThreadStatic] private static bool cachedRequiresBroker;
    [ThreadStatic] private static bool cachedIntegrityKnown;
    [ThreadStatic] private static long retryUnknownAt;

    internal static bool RequiresBroker()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(foreground, out uint processId) == 0)
            return true; // Includes a desktop transition; the worker attaches to the input desktop.
        if (processId == cachedProcessId && (cachedProcess == null
            || NativeMethods.WaitForSingleObject(cachedProcess.DangerousGetHandle(), 0) == 258)
            && (cachedIntegrityKnown || Environment.TickCount64 < retryUnknownAt))
            return cachedRequiresBroker;

        cachedProcess?.Dispose();
        cachedProcess = null;
        cachedProcessId = processId;
        // Retaining a waitable handle prevents a recycled PID from reusing stale classification.
        IntPtr handle = NativeMethods.OpenProcess(0x00101000, false, processId); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        int? targetIntegrity = null;
        if (handle != IntPtr.Zero)
        {
            cachedProcess = new SafeProcessHandle(handle, true);
            targetIntegrity = ReadProcessIntegrity(handle);
        }
        cachedIntegrityKnown = ownIntegrity.HasValue && targetIntegrity.HasValue;
        retryUnknownAt = cachedIntegrityKnown ? 0 : Environment.TickCount64 + 1000;
        cachedRequiresBroker = !cachedIntegrityKnown || targetIntegrity > ownIntegrity;
        Logger.Log($"Input foreground classification: localIL={ownIntegrity?.ToString() ?? "unknown"}; targetIL={targetIntegrity?.ToString() ?? "unknown"}; protected={cachedRequiresBroker}.");
        return cachedRequiresBroker;
    }

    private static int? ReadOwnIntegrity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return ReadTokenIntegrity(identity.Token);
    }

    internal static int? ReadProcessIntegrity(IntPtr process)
    {
        IntPtr token = IntPtr.Zero;
        try { return NativeMethods.OpenProcessToken(process, 8, ref token) ? ReadTokenIntegrity(token) : null; }
        finally { if (token != IntPtr.Zero) NativeMethods.CloseHandle(token); }
    }

    private static int? ReadTokenIntegrity(IntPtr token)
    {
        GetTokenInformation(token, 25, IntPtr.Zero, 0, out int size);
        if (size == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, size, out _)) return null;
            // TOKEN_MANDATORY_LABEL begins with SID_AND_ATTRIBUTES; the last SID RID is the IL.
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            int count = Marshal.ReadByte(sid, 1);
            return count == 0 ? null : Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr buffer, int length, out int required);
}
