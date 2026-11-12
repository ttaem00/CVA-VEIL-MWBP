// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using MouseWithoutBorders.Class;
using StreamJsonRpc;

namespace MouseWithoutBorders.Core;

internal static class PortableInstallLifecycle
{
    internal const string LaunchArgument = "--mwb-launch-installed";
    internal const string NetworkArgument = "--mwb-configure-network";
    private const string SettingsPipe = "MouseWithoutBorders/SettingsSync";
    private const string FirewallRuleName = "MouseWithoutBordersPlus LAN";
    private const string NetworkFirewallRuleName = "MouseWithoutBordersPlus Network";

    internal static ProcessStartInfo CreateNetworkSetupStartInfo(bool elevated = false)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = elevated, CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden };
        if (elevated) info.Verb = "runas";
        info.ArgumentList.Add(NetworkArgument);
        return info;
    }

    internal static bool TryRunNetworkSetup(string[] args)
    {
        if (args.Length < 2 || args[1] != NetworkArgument) return false;
        try
        {
            if (args.Length != 2) throw new ArgumentException("Invalid network setup invocation.");
            if (PowerToys.GPOWrapper.GPOWrapper.GetConfiguredMouseWithoutBordersEnabledValue() == PowerToys.GPOWrapper.GpoRuleConfigured.Disabled)
                throw new UnauthorizedAccessException("Mouse Without Borders is disabled by administrator policy.");
            using var identity = WindowsIdentity.GetCurrent();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (!ConfigureFirewall(false, Environment.ProcessPath!, NetworkFirewallRuleName, elevated))
            {
                using var setup = Process.Start(CreateNetworkSetupStartInfo(elevated: true))
                    ?? throw new IOException("Windows did not start network setup.");
                setup.WaitForExit();
                Environment.ExitCode = setup.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 1;
        }
        return true;
    }

    internal static ProcessStartInfo CreatePrivilegedInputStartInfo(bool remove = false, string authorizedUserSid = null)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(remove ? PrivilegedInput.RemoveArgument : PrivilegedInput.InstallArgument);
        if (!remove) info.ArgumentList.Add(authorizedUserSid);
        return info;
    }

    internal static ProcessStartInfo CreateApprovalScreensStartInfo(bool allow)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(PrivilegedInput.ApprovalScreensArgument);
        info.ArgumentList.Add(allow ? "on" : "off");
        return info;
    }

    // Runs only in the explicitly elevated role. The worker reads the value when it starts,
    // so the service is restarted; nothing is checked per input beyond what the worker does.
    internal static void ConfigureApprovalScreens(bool allow)
    {
        string image = "\"" + PrivilegedInput.ProtectedExecutable + "\" " + PrivilegedInput.ServiceArgument;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PrivilegedInput.ServiceRegistryPath))
            if (key == null || !string.Equals(key.GetValue("ImagePath") as string, image, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Install MWBP on this PC before changing approval screen control.");
        StopInputService();
        try
        {
            using var parameters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PrivilegedInput.ServiceRegistryPath + @"\Parameters", true)
                ?? throw new IOException("The input service configuration is missing. Choose Repair installation.");
            if (allow) parameters.DeleteValue(PrivilegedInput.ApprovalScreensValue, false);
            else parameters.SetValue(PrivilegedInput.ApprovalScreensValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        finally { RunServiceCommand("start", PrivilegedInput.ServiceName); }
    }

    // Runs only in the early, explicitly elevated installer role; it never reads preferences.
    internal static void ConfigurePrivilegedInput(bool remove, string authorizedUserSid = null, bool uninstall = false)
    {
        bool installService = PrivilegedInput.ServiceModeAllowed;
        if (!remove && installService) authorizedUserSid = PrivilegedInput.NormalizeAuthorizedUserSid(authorizedUserSid);
        string image = "\"" + PrivilegedInput.ProtectedExecutable + "\" " + PrivilegedInput.ServiceArgument;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PrivilegedInput.ServiceRegistryPath);
        bool exists = key != null;
        if (exists && !string.Equals(key.GetValue("ImagePath") as string, image, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The input service name belongs to another installation.");
        if (exists) StopInputService();
        if (remove || !installService)
        {
            if (exists) RunServiceCommand("delete", PrivilegedInput.ServiceName);
            if (remove)
            {
                if (uninstall) ConfigureFirewall(remove: true);
                return; // The normal app and its user-owned preferences remain installed.
            }
        }

        Directory.CreateDirectory(PrivilegedInput.ProtectedDirectory);
        if ((File.GetAttributes(PrivilegedInput.ProtectedDirectory) & FileAttributes.ReparsePoint) != 0
            || File.Exists(PrivilegedInput.ProtectedExecutable) && (File.GetAttributes(PrivilegedInput.ProtectedExecutable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The protected input installation cannot be a symbolic link or junction.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(true, false); directorySecurity.SetOwner(admins);
        foreach (var sid in new[] { system, admins, users })
            directorySecurity.AddAccessRule(new FileSystemAccessRule(sid, sid == users ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(PrivilegedInput.ProtectedDirectory).SetAccessControl(directorySecurity);
        bool created = false;
        try
        {
            using var transaction = new PortableInstallTransaction();
            if (!PrivilegedInput.IsProtectedCopy)
            {
                transaction.TrackFile(PrivilegedInput.ProtectedExecutable);
                File.Copy(Environment.ProcessPath!, PrivilegedInput.ProtectedExecutable, true);
            }
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false); fileSecurity.SetOwner(admins);
            foreach (var sid in new[] { system, admins, users })
                fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, sid == users ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(PrivilegedInput.ProtectedExecutable).SetAccessControl(fileSecurity);
            if (installService)
            {
                if (!exists)
                {
                    RunServiceCommand("create", PrivilegedInput.ServiceName, "binPath=", image, "start=", "auto", "DisplayName=", "Mouse Without Borders Plus Input");
                    created = true;
                }
                using (var parameters = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PrivilegedInput.ServiceRegistryPath + @"\Parameters", true))
                {
                    string previous = parameters.GetValue(PrivilegedInput.AuthorizedUserSidValue) as string;
                    transaction.OnRollback(() =>
                    {
                        using var restore = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PrivilegedInput.ServiceRegistryPath + @"\Parameters", true);
                        if (previous == null) restore.DeleteValue(PrivilegedInput.AuthorizedUserSidValue, false);
                        else restore.SetValue(PrivilegedInput.AuthorizedUserSidValue, previous, Microsoft.Win32.RegistryValueKind.String);
                    });
                    var registrySecurity = new RegistrySecurity();
                    registrySecurity.SetAccessRuleProtection(true, false); registrySecurity.SetOwner(admins);
                    foreach (var sid in new[] { system, admins, users })
                        registrySecurity.AddAccessRule(new RegistryAccessRule(sid, sid == users ? RegistryRights.ReadKey : RegistryRights.FullControl,
                            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                    parameters.SetAccessControl(registrySecurity);
                    parameters.SetValue(PrivilegedInput.AuthorizedUserSidValue, authorizedUserSid, Microsoft.Win32.RegistryValueKind.String);                }
                // SCM owns recovery; the app does not add an input retry/polling loop.
                RunServiceCommand("failure", PrivilegedInput.ServiceName, "reset=", "86400", "actions=", "restart/5000");
                // The running service must release the protected image before file rollback.
                transaction.OnRollback(StopInputService);
                RunServiceCommand("start", PrivilegedInput.ServiceName);
            }
            ConfigureFirewall(remove: false);
            transaction.Complete();
        }
        catch
        {
            // The file transaction has restored the previous executable before restart.
            if (created) RunServiceCommand("delete", PrivilegedInput.ServiceName);
            else if (exists && installService) RunServiceCommand("start", PrivilegedInput.ServiceName);
            throw;
        }
    }

    private static void StopInputService()
    {
        var hosts = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(PrivilegedInput.ProtectedExecutable)))
            {
                bool keep = false;
                try
                {
                    if (process.SessionId == 0 && !process.HasExited && string.Equals(process.MainModule.FileName,
                        PrivilegedInput.ProtectedExecutable, StringComparison.OrdinalIgnoreCase)) { hosts.Add(process); keep = true; }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally { if (!keep) process.Dispose(); }
            }
            using var service = new ServiceController(PrivilegedInput.ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); }
            foreach (var process in hosts)
                if (!process.WaitForExit(15000)) throw new IOException("The previous input service has not exited.");
        }
        finally { foreach (var process in hosts) process.Dispose(); }
    }

    // One application-scoped LAN rule covers discovery and the existing configurable TCP ports.
    private static bool ConfigureFirewall(bool remove, string executablePath = null, string ruleName = FirewallRuleName, bool allowWrite = true)
    {
        executablePath ??= PrivilegedInput.ProtectedExecutable;
        dynamic policy = null, rules = null, previous = null, protectedRule = null, rule = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!);
            rules = policy.Rules;
            if (ruleName == NetworkFirewallRuleName)
            {
                try { protectedRule = rules.Item(FirewallRuleName); }
                catch (FileNotFoundException) { }
                if (NetworkRuleAllows(protectedRule, executablePath)) return true;
            }
            try { previous = rules.Item(ruleName); }
            catch (FileNotFoundException) { }
            if (previous != null && !string.Equals((string)previous.ApplicationName, executablePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The firewall rule name belongs to another application.");
            if (remove)
            {
                if (previous != null) rules.Remove(ruleName);
                return true;
            }
            if (NetworkRuleAllows(previous, executablePath)) return true;
            if (!allowWrite) return false;
            rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!);
            rule.Name = ruleName;
            rule.ApplicationName = executablePath;
            rule.Direction = 1; rule.Action = 1; rule.Enabled = true;
            rule.Profiles = int.MaxValue; rule.Protocol = 256; rule.RemoteAddresses = "LocalSubnet";
            // Add replaces this identifier atomically; do not partly edit an existing live rule.
            rules.Add(rule);
            return true;
        }
        finally
        {
            foreach (object value in new object[] { rule, previous, protectedRule, rules, policy })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    internal static bool NetworkRuleAllows(object candidate, string executablePath)
    {
        if (candidate == null) return false;
        dynamic rule = candidate;
        return string.Equals((string)rule.ApplicationName, executablePath, StringComparison.OrdinalIgnoreCase)
            && (bool)rule.Enabled && (int)rule.Direction == 1 && (int)rule.Action == 1
            && (int)rule.Protocol == 256 && ((int)rule.Profiles & 7) == 7
            && string.Equals((string)rule.RemoteAddresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase);
    }

    private static void RunServiceCommand(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Windows did not start service management.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Input service management failed (" + process.ExitCode + ").");
    }

    internal static ProcessStartInfo CreateLaunchHelperStartInfo(string executable, string directory, int parentId, long parentStarted, string sourceSettings = null)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false };
        info.ArgumentList.Add(LaunchArgument);
        info.ArgumentList.Add(parentId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(parentStarted.ToString(CultureInfo.InvariantCulture));
        if (sourceSettings != null) info.ArgumentList.Add(sourceSettings);
        return info;
    }

    internal static bool RunLaunchHelper(string[] args)
    {
        if (args.Length < 2 || args[1] != LaunchArgument) return false;
        try
        {
            if (args.Length is not (4 or 5) || !int.TryParse(args[2], out int parentId) || parentId <= 0
                || !long.TryParse(args[3], out long started) || started <= 0)
                throw new ArgumentException("Invalid installed-app launch request.");
            WaitForParentExit(parentId, started, TimeSpan.FromMinutes(2));
            if (args.Length == 5) PortableApplication.MigratePreferencesAfterExit(args[4], PortableApplication.CurrentSettingsPath);
            using var launched = Process.Start(new ProcessStartInfo(Application.ExecutablePath)
            { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false })
                ?? throw new IOException("Windows did not start the installed application.");
        }
        catch (Exception error)
        {
            Logger.Log("Installed launch: " + error.Message);
            MessageBox.Show("Installation completed, but the app could not start automatically. Exit the previous copy, then open Mouse Without Borders from the Start menu.\n\n" + error.Message,
                "Mouse Without Borders", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return true;
    }

    internal static void WaitForParentExit(int id, long started, TimeSpan timeout)
    {
        Process parent;
        try { parent = Process.GetProcessById(id); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            // PID reuse must never make us wait for an unrelated application.
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != started) return;
            if (!parent.WaitForExit((int)timeout.TotalMilliseconds))
                throw new System.TimeoutException("The previous copy is still running.");
        }
    }

    internal static RunningCopy StopInstalledCopy(string executable)
    {
        var result = new RunningCopy(executable);
        try
        {
            using var current = Process.GetCurrentProcess();
            // The settings endpoint identifies the GUI. The input agent has the same
            // image/session but belongs to SCM, so it must never enter this wait.
            using var process = RequestShutdown(SettingsPipe, id =>
            {
                var candidate = Process.GetProcessById(id);
                bool keep = false;
                try
                {
                    keep = candidate.Id != current.Id && candidate.SessionId == current.SessionId && !candidate.HasExited
                        && string.Equals(Path.GetFullPath(candidate.MainModule.FileName), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
                    return keep ? candidate : null;
                }
                finally { if (!keep) candidate.Dispose(); }
            });
            if (process == null) return result;
            if (!process.WaitForExit(15000)) throw new IOException("The installed copy is still running.");
            result.Stopped = true;
            return result;
        }
        catch (Exception error)
        {
            throw new IOException("Could not close the running installation safely. Exit Mouse Without Borders from its tray menu on this PC, then try Install again. The installed files have not been replaced.", error);
        }
    }

    internal static Process RequestShutdown(string pipeName, Func<int, Process> expectedProcess)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(0); }
        catch (System.TimeoutException) { return null; }
        IpcChannel<object>.VerifyServerOwner(pipe);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverId))
            throw new IOException("The running control endpoint belongs to a different process.");
        var process = expectedProcess((int)serverId);
        if (process == null) return null;
        try
        {
            using var rpc = JsonRpc.Attach(pipe);
            try { rpc.InvokeAsync("Shutdown").WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (ConnectionLostException) { /* Successful shutdown can close the pipe before replying. Caller verifies exit. */ }
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverId);

    // Declared before the file transaction so rollback restores files before restarting.
    internal sealed class RunningCopy(string executable) : IDisposable
    {
        internal bool Stopped;
        private bool completed;
        internal void Complete() => completed = true;
        public void Dispose()
        {
            if (!Stopped || completed) return;
            try
            {
                using var restarted = Process.Start(new ProcessStartInfo(executable)
                { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false });
            }
            catch (Exception error) { Logger.Log("Installation rollback could not restart the previous copy: " + error.Message); }
        }
    }
}
#endif
