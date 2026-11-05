// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using MouseWithoutBorders.Class;
using PowerToys.GPOWrapper;

namespace MouseWithoutBorders.Core;

// Only the input capability runs as SYSTEM. These roles never initialize Settings or Forms.
internal static class PrivilegedInput
{
    internal const string ServiceName = "MouseWithoutBordersPlus.Input";
    internal const string ServiceArgument = "--mwb-input-service";
    internal const string AgentArgument = "--mwb-input-agent";
    internal const string InstallArgument = "--mwb-input-install";
    internal const string RemoveArgument = "--mwb-input-remove";
    internal const string UninstallArgument = "--mwb-input-uninstall";
    internal const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    internal const string AuthorizedUserSidValue = "AuthorizedUserSid";
    private const uint DesktopJournalPlayback = 0x0020;

    internal static string NormalizeAuthorizedUserSid(string value) => new SecurityIdentifier(value).Value;

    internal static string ReadAuthorizedUserSid(Func<object> readValue)
    {
        try { return readValue() is string value ? NormalizeAuthorizedUserSid(value) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return null; }
    }

    private static string AuthorizedUserSid() => ReadAuthorizedUserSid(() =>
    {
        using var parameters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServiceRegistryPath + @"\Parameters");
        return parameters?.GetValue(AuthorizedUserSidValue);
    });
    internal static string ProtectedDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MouseWithoutBordersPlus");
    internal static string ProtectedExecutable => Path.Combine(ProtectedDirectory, "MouseWithoutBordersPlus.exe");
    private static string Executable => Environment.ProcessPath ?? throw new IOException("The application executable is unavailable.");
    private static string PipeName(int session) => "MouseWithoutBordersPlus/Input/" + session;
    internal static bool IsProtectedCopy => Path.GetFullPath(Executable).Equals(ProtectedExecutable, StringComparison.OrdinalIgnoreCase);
    private static NamedPipeClientStream inputPipe;
    private static SafeProcessHandle inputAgent;
    private static bool brokerOwnsInput;
    internal static bool HasHeldInput
    {
        get
        {
            if (brokerOwnsInput) DiscardExitedAgent();
            return brokerOwnsInput;
        }
    }

    private static void DiscardExitedAgent()
    {
        // An exited owner cannot deliver a late cleanup; its cached pipe is stale even when no key is held.
        if (inputAgent == null || NativeMethods.WaitForSingleObject(inputAgent.DangerousGetHandle(), 0) != 0) return;
        inputPipe?.Dispose(); inputPipe = null;
        inputAgent.Dispose(); inputAgent = null;
        brokerOwnsInput = false;
    }

    // Called under InputSimulationLock. Disconnect makes the agent release its own
    // keys even when this desktop cannot observe them through GetAsyncKeyState.
    internal static void ReleaseConnection()
    {
        inputPipe?.Dispose(); inputPipe = null;
        // Keep uncertain ownership until that agent drains it or exits.
    }

    private static readonly (int Down, int Up, int Data)[] MouseButtons = [(2, 4, 0), (8, 16, 0), (32, 64, 0), (128, 256, 1), (128, 256, 2)];

    internal static bool ServiceModeAllowed => GPOWrapper.GetConfiguredMwbAllowServiceModeValue() != GpoRuleConfigured.Disabled;

    internal static bool PolicyAllowsRole(string role, GpoRuleConfigured appPolicy, GpoRuleConfigured servicePolicy) =>
        role is RemoveArgument or UninstallArgument || appPolicy != GpoRuleConfigured.Disabled
            && (role == InstallArgument || servicePolicy != GpoRuleConfigured.Disabled);

    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[1] is not (ServiceArgument or AgentArgument or InstallArgument or RemoveArgument or UninstallArgument)) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!PolicyAllowsRole(args[1], GPOWrapper.GetConfiguredMouseWithoutBordersEnabledValue(), GPOWrapper.GetConfiguredMwbAllowServiceModeValue()))
                throw new UnauthorizedAccessException("Protected input is disabled by administrator policy.");
            if (args[1] is InstallArgument or RemoveArgument or UninstallArgument)
            {
                bool remove = args[1] != InstallArgument;
                if (args.Length != (remove ? 2 : 3) || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Administrator approval is required to manage protected input.");
                PortableInstallLifecycle.ConfigurePrivilegedInput(remove, remove ? null : args[2], args[1] == UninstallArgument);
            }
            else
            {
                if (!identity.IsSystem || !IsProtectedCopy) throw new UnauthorizedAccessException("The input role requires the protected SYSTEM installation.");
                if (args[1] == ServiceArgument)
                {
                    if (args.Length != 2 || Process.GetCurrentProcess().SessionId != 0) throw new ArgumentException("Invalid service invocation.");
                    ServiceBase.Run(new InputService());
                }
                else
                {
                    if (args.Length != 4 || Process.GetCurrentProcess().SessionId == 0) throw new ArgumentException("Invalid input-agent invocation.");
                    // The STA entry thread can own COM windows, which prevent desktop changes.
                    System.Threading.Tasks.Task.Run(() => RunAgent(args[2], new SecurityIdentifier(args[3]))).GetAwaiter().GetResult();
                }
            }
        }
        catch (Exception error)
        {
            // A service/agent failure must never open a privileged first-launch/settings UI.
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 1;
        }
        return true;
    }

    // Called inside the existing InputSimulationLock, after native SendInput returned zero.
    internal static bool TrySend(NativeMethods.INPUT64 input, out uint sent)
    {
        sent = 0;
        if (!IsProtectedCopy) return false;
        DiscardExitedAgent();
        try
        {
            // A revoked policy blocks new ownership, while already-owned input can still drain.
            if (!brokerOwnsInput && Setting.Values.AllowServiceModeIsGpoConfigured) return false;
            if (inputPipe == null)
            {
                inputPipe = new NamedPipeClientStream(".", PipeName(Process.GetCurrentProcess().SessionId), PipeDirection.InOut, PipeOptions.Asynchronous);
                inputPipe.Connect(0);
                var owner = inputPipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                if (!Equals(owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
                    throw new UnauthorizedAccessException("The input endpoint is not owned by SYSTEM.");
                if (!GetNamedPipeServerProcessId(inputPipe.SafePipeHandle, out uint server)) throw new Win32Exception();
                var verified = VerifyExecutable((int)server, Process.GetCurrentProcess().SessionId);
                inputAgent?.Dispose(); inputAgent = verified;
            }
            sent = ExchangeInput(input);
            return true;
        }
        catch (Exception error) when (error is IOException or System.TimeoutException or UnauthorizedAccessException or Win32Exception or OperationCanceledException)
        {
            inputPipe?.Dispose(); inputPipe = null;
            return false;
        }
    }

    private static uint ExchangeInput(NativeMethods.INPUT64 input)
    {
        // Match the existing input TCP send deadline; a stalled agent must release the input lock.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        byte[] data = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()];
        MemoryMarshal.Write(data.AsSpan(), in input);
        // Until the reply, this stream may own a DOWN even if delivery times out.
        brokerOwnsInput = true;
        inputPipe.WriteAsync(data, 0, data.Length, deadline.Token).GetAwaiter().GetResult();
        byte[] reply = new byte[sizeof(uint)];
        inputPipe.ReadExactlyAsync(reply, deadline.Token).AsTask().GetAwaiter().GetResult();
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        brokerOwnsInput = (status >> 1) != 0;
        return status & 1;
    }

    private static SafeProcessHandle VerifyExecutable(int processId, int session)
    {
        var process = new SafeProcessHandle(NativeMethods.OpenProcess(0x101000, false, (uint)processId), true);
        if (process.IsInvalid) { process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            char[] path = new char[32768];
            uint length = (uint)path.Length;
            if (!ProcessIdToSessionId((uint)processId, out uint actualSession)
                || !NativeMethods.QueryFullProcessImageName(process.DangerousGetHandle(), 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (actualSession != session || !new string(path, 0, (int)length).Equals(ProtectedExecutable, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The input endpoint belongs to a different application or Windows session.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static SecurityIdentifier InteractiveUser(int session)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (NativeMethods.WTSQueryUserToken((uint)session, ref token) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var user = new WindowsIdentity(token);
            return user.User ?? throw new UnauthorizedAccessException("No interactive Windows user is available.");
        }
        finally { if (token != IntPtr.Zero) NativeMethods.CloseHandle(token); }
    }

    private static NamedPipeServerStream CreateAgentPipe(SecurityIdentifier user, int session)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.ReadWrite, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(PipeName(session), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    private static void RunAgent(string stopEventName, SecurityIdentifier user)
    {
        int session = Process.GetCurrentProcess().SessionId;
        using var stop = EventWaitHandleAcl.OpenExisting(stopEventName, EventWaitHandleRights.Synchronize);
        NamedPipeServerStream server = null;
        var registered = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => Volatile.Read(ref server)?.Dispose(), null, Timeout.Infinite, true);
        byte[] bytes = new byte[Marshal.SizeOf<NativeMethods.INPUT64>()];
        byte[] reply = new byte[sizeof(uint)];
        var held = new Dictionary<long, NativeMethods.INPUT64>();
        try
        {
            while (!stop.WaitOne(0))
            {
                using var connection = server = CreateAgentPipe(user, session);
                if (stop.WaitOne(0)) break;
                bool recoveringPreviousConnection = held.Count != 0;
                try
                {
                    connection.WaitForConnection();
                    if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out uint client)) throw new Win32Exception();
                    using var verified = VerifyExecutable((int)client, session);
                    while (true)
                    {
                        connection.ReadExactly(bytes);
                        var input = MemoryMarshal.Read<NativeMethods.INPUT64>(bytes);
                        if (input.type is not (0 or 1)) throw new InvalidDataException("Only keyboard and mouse input is supported.");
                        if (recoveringPreviousConnection)
                            recoveringPreviousConnection = !ReleaseHeldInput(held, Inject);
                        uint count = recoveringPreviousConnection ? 0 : Inject(input);
                        if (count == 0 && !recoveringPreviousConnection && IsOwnedRelease(held, input)) break;
                        if (count != 0) TrackHeldInput(held, input);
                        BinaryPrimitives.WriteUInt32LittleEndian(reply, count | ((uint)held.Count << 1));
                        connection.Write(reply);
                    }
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException) { }
                finally
                {
                    // Release only inputs owned by this connection, including an interrupted drag.
                    ReleaseHeldInput(held, Inject);
                }
            }
        }
        finally { registered.Unregister(null); server?.Dispose(); }
    }

    private static bool ReleaseHeldInput(Dictionary<long, NativeMethods.INPUT64> held, Func<NativeMethods.INPUT64, uint> inject)
    {
        // Desktop transitions can reject an UP. Keep its ownership until an UP actually succeeds.
        foreach (var entry in new List<KeyValuePair<long, NativeMethods.INPUT64>>(held))
            if (inject(entry.Value) != 0) held.Remove(entry.Key);
        return held.Count == 0;
    }

    private static void TrackHeldInput(Dictionary<long, NativeMethods.INPUT64> held, NativeMethods.INPUT64 input)
    {
        if (input.type == 1)
        {
            long key = KeyIdentity(input);
            if ((input.ki.dwFlags & (int)NativeMethods.KEYEVENTF.KEYUP) != 0) held.Remove(key);
            else { input.ki.dwFlags |= (int)NativeMethods.KEYEVENTF.KEYUP; held[key] = input; }
            return;
        }
        for (int i = 0; i < MouseButtons.Length; i++)
        {
            var (down, up, data) = MouseButtons[i];
            if (data != 0 && (input.mi.mouseData & data) == 0) continue;
            if ((input.mi.dwFlags & up) != 0) held.Remove(-i - 1);
            else if ((input.mi.dwFlags & down) != 0)
            {
                var release = input; release.mi.dwFlags = up; release.mi.mouseData = data;
                held[-i - 1] = release;
            }
        }
    }

    private static long KeyIdentity(NativeMethods.INPUT64 input) => ((long)(ushort)input.ki.wVk << 17)
        | ((long)(ushort)input.ki.wScan << 1) | ((input.ki.dwFlags & (int)NativeMethods.KEYEVENTF.EXTENDEDKEY) != 0 ? 1L : 0L);

    private static bool IsOwnedRelease(Dictionary<long, NativeMethods.INPUT64> held, NativeMethods.INPUT64 input)
    {
        if (input.type == 1) return (input.ki.dwFlags & (int)NativeMethods.KEYEVENTF.KEYUP) != 0 && held.ContainsKey(KeyIdentity(input));
        for (int i = 0; i < MouseButtons.Length; i++)
        {
            var (_, up, data) = MouseButtons[i];
            if ((input.mi.dwFlags & up) != 0 && (data == 0 || (input.mi.mouseData & data) != 0) && held.ContainsKey(-i - 1)) return true;
        }
        return false;
    }

    private static uint Inject(NativeMethods.INPUT64 input)
    {
        IntPtr original = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS | NativeMethods.DESKTOP_WRITEOBJECTS | DesktopJournalPlayback);
        if (desktop == IntPtr.Zero) return 0;
        try
        {
            if (!SetThreadDesktop(desktop)) return 0;
            if (input.type == 0) input.mi.dwExtraInfo = InputSimulation.InjectedInputTag;
            else input.ki.dwExtraInfo = InputSimulation.InjectedInputTag;
            return NativeMethods.SendInput64(1, [input], Marshal.SizeOf(input));
        }
        finally { SetThreadDesktop(original); CloseDesktop(desktop); }
    }

    private sealed class InputService : ServiceBase
    {
        private readonly object gate = new();
        private Process agent;
        private EventWaitHandle stopAgent;
        private bool stopping;
        internal InputService() { ServiceName = PrivilegedInput.ServiceName; CanHandleSessionChangeEvent = true; AutoLog = false; }
        protected override void OnStart(string[] args) => EnsureAgent();
        protected override void OnSessionChange(SessionChangeDescription changeDescription)
        {
            if (changeDescription.Reason == SessionChangeReason.SessionLogoff)
            {
                lock (gate)
                    if (agent != null && agent.SessionId == changeDescription.SessionId) StopAgent();
                return; // Console ID can still name the logged-off session; SessionLogon creates the next owner.
            }
            EnsureAgent();
        }
        protected override void OnStop() { lock (gate) { stopping = true; StopAgent(); } }
        private void AgentExited(object sender, EventArgs args)
        { lock (gate) { if (!stopping && ReferenceEquals(sender, agent)) Environment.Exit(1); } }
        private void StopAgent()
        {
            if (agent != null)
            {
                agent.Exited -= AgentExited;
                stopAgent?.Set();
                if (!agent.HasExited && !agent.WaitForExit(5000)) agent.Kill();
                agent.Dispose(); agent = null;
            }
            stopAgent?.Dispose(); stopAgent = null;
        }
        private void EnsureAgent()
        {
            lock (gate)
            {
                int session = (int)NativeMethods.WTSGetActiveConsoleSessionId();
                if (stopping) return;
                string authorized = AuthorizedUserSid();
                SecurityIdentifier user = null;
                // Before interactive logon there is no user client to serve. SessionLogon retries.
                if (session != -1)
                    try { user = InteractiveUser(session); } catch (Win32Exception) { }
                if (authorized == null || user == null || !user.Value.Equals(authorized, StringComparison.Ordinal))
                { StopAgent(); return; }
                if (agent != null && !agent.HasExited && agent.SessionId == session) return;
                StopAgent();
                string eventName = @"Global\MWBP.Input.Stop." + Guid.NewGuid().ToString("N");
                var security = new EventWaitHandleSecurity();
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                security.SetAccessRuleProtection(true, false); security.SetOwner(system);
                security.AddAccessRule(new EventWaitHandleAccessRule(system, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                stopAgent = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, eventName, out bool created, security);
                if (!created) throw new IOException("The input stop signal already exists.");
                agent = LaunchAgent(session, eventName, user);
                agent.Exited += AgentExited;
                agent.EnableRaisingEvents = true;
            }
        }
    }

    // Adapted from Microsoft PowerToys App/Service/NativeMethods.cs: keep only session launch.
    private static Process LaunchAgent(int session, string stopEventName, SecurityIdentifier user)
    {
        using var winlogon = Array.Find(Process.GetProcessesByName("winlogon"), process => process.SessionId == session)
            ?? throw new IOException("The console session is not ready.");
        IntPtr process = IntPtr.Zero, token = IntPtr.Zero, duplicate = IntPtr.Zero;
        NativeMethods.PROCESS_INFORMATION launched = default;
        try
        {
            process = NativeMethods.OpenProcess(0x1000, false, (uint)winlogon.Id);
            if (process == IntPtr.Zero || !NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY, ref token)) throw new Win32Exception();
            var attributes = new NativeMethods.SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>() };
            if (!NativeMethods.DuplicateTokenEx(token, NativeMethods.MAXIMUM_ALLOWED, ref attributes, 2, 1, ref duplicate)) throw new Win32Exception();
            string access = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00101000;;;" + user.Value + ")";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(access, 1, out attributes.lpSecurityDescriptor, out _)) throw new Win32Exception();
            var threadAttributes = new NativeMethods.SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>() };
            var startup = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            string command = '"' + ProtectedExecutable + '"' + " " + AgentArgument + " " + stopEventName + " " + user.Value;
            try
            {
                if (!NativeMethods.CreateProcessAsUser(duplicate, ProtectedExecutable, command, ref attributes, ref threadAttributes, false, 0,
                    IntPtr.Zero, ProtectedDirectory, ref startup, out launched)) throw new Win32Exception();
                return Process.GetProcessById((int)launched.dwProcessId);
            }
            finally { LocalFree(attributes.lpSecurityDescriptor); }
        }
        finally
        {
            if (launched.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(launched.hProcess);
            if (launched.hThread != IntPtr.Zero) NativeMethods.CloseHandle(launched.hThread);
            if (duplicate != IntPtr.Zero) NativeMethods.CloseHandle(duplicate);
            if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            if (process != IntPtr.Zero) NativeMethods.CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
}

