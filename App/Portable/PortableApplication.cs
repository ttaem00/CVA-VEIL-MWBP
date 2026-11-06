// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.

#if PORTABLE_SINGLE_FILE

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.Win32;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

internal static class PortableApplication
{
    internal const string ClipboardHelperArgument = "--mwb-clipboard-helper";
    internal const string UninstallArgument = "--uninstall";
    internal const string SettingsFileName = "MouseWithoutBorders.prefs.json";
    internal const string InstalledApplicationRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MouseWithoutBordersPlus";

    internal const string AppModeInstalled = "Installed";
    internal const string AppModePortable = "Portable";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "MouseWithoutBordersPlus";
    internal const string ShortcutName = "VEIL MWBP.lnk";
    private const string LegacyShortcutName = "Mouse Without Borders Plus.lnk";

    internal static bool IsInstalledCopy => PrivilegedInput.IsProtectedCopy;

    internal static string CurrentExecutablePath => Path.GetFullPath(Application.ExecutablePath);

    internal static string CurrentSettingsPath => SettingsPathForExecutable(CurrentExecutablePath);

    internal static string SettingsPathForExecutable(string executable) =>
        Path.GetFullPath(executable).Equals(PrivilegedInput.ProtectedExecutable, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseWithoutBordersPlus", SettingsFileName)
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, SettingsFileName);

    internal static string LegacyExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MouseWithoutBordersPlus", "MouseWithoutBordersPlus.exe");

    internal static bool IsClipboardHelperInvocation(string[] args)
    {
        return args.Length > 1 && args[1].Equals(ClipboardHelperArgument, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PrepareFirstLaunch()
    {
        if (File.Exists(CurrentSettingsPath) || File.Exists(CurrentSettingsPath + ".bak"))
        {
            try
            {
                _ = PortableSettingsStore.Read(CurrentSettingsPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                try
                {
                    _ = PortableSettingsStore.Read(CurrentSettingsPath + ".bak");
                    if (MessageBox.Show(
                        "Your preferences could not be loaded. Restore the last valid backup? The current file will be preserved.\r\n\r\n" + ex.Message,
                        "Recover Mouse Without Borders preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        PortableSettingsStore.RestoreBackup(CurrentSettingsPath);
                        return PrepareFirstLaunch();
                    }
                }
                catch (Exception recoveryError) when (recoveryError is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _ = MessageBox.Show(
                        "Mouse Without Borders could not load your preferences or recover a backup. Your files have not been reset.\r\n\r\n" +
                        CurrentSettingsPath + "\r\n\r\n" + recoveryError.Message,
                        "Preferences could not be loaded", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return false;
            }
        }

        using var dialog = new FirstLaunchForm();
        _ = dialog.ShowDialog();
        return false; // Successful setup schedules the protected copy after this process exits.
    }

    internal static async Task PrepareNetworkAsync()
    {
        using var setup = Process.Start(PortableInstallLifecycle.CreateNetworkSetupStartInfo())
            ?? throw new IOException("Windows did not start network setup.");
        await setup.WaitForExitAsync();
        if (setup.ExitCode != 0) throw new IOException("Network setup did not complete. Mouse sharing can still run with the existing network permissions.");
    }

    internal static Task<bool> EnablePrivilegedInputAsync() => InstallProductAsync(preserveCurrentPreferences: true);

    // The first-launch UI keeps its message pump alive while awaiting this owner.
    // Fresh setup must not initialize Setting.Values beside the downloaded executable.
    internal static async Task<bool> InstallProductAsync(bool preserveCurrentPreferences)
    {
        if (PowerToys.GPOWrapper.GPOWrapper.GetConfiguredMouseWithoutBordersEnabledValue() == PowerToys.GPOWrapper.GpoRuleConfigured.Disabled)
            throw new UnauthorizedAccessException("Mouse Without Borders is disabled by administrator policy.");
        if (preserveCurrentPreferences) Setting.Values.SaveSettingsSynchronously();
        string targetSettings = SettingsPathForExecutable(PrivilegedInput.ProtectedExecutable);
        string targetDirectory = Path.GetDirectoryName(targetSettings)!;
        Directory.CreateDirectory(targetDirectory);
        bool newInstallation = !File.Exists(PrivilegedInput.ProtectedExecutable);
        using var running = PrivilegedInput.IsProtectedCopy ? null : PortableInstallLifecycle.StopInstalledCopy(PrivilegedInput.ProtectedExecutable);
        using var legacy = CurrentExecutablePath.Equals(LegacyExecutable, StringComparison.OrdinalIgnoreCase)
            ? null : PortableInstallLifecycle.StopInstalledCopy(LegacyExecutable);
        string sourceSettings = SelectInstallSource(CurrentSettingsPath, SettingsPathForExecutable(LegacyExecutable), targetSettings,
            preserveCurrentPreferences);
        bool moving = sourceSettings != null && !sourceSettings.Equals(targetSettings, StringComparison.OrdinalIgnoreCase);
        bool startup = newInstallation && sourceSettings == null && !File.Exists(targetSettings) && !File.Exists(targetSettings + ".bak")
            || IsStartWithWindowsEnabled(CurrentExecutablePath, PrivilegedInput.ProtectedExecutable, LegacyExecutable);
        using var transaction = new PortableInstallTransaction();
        if (preserveCurrentPreferences) DurableTransfers.PrepareInstall();
        try
        {
            if (moving || !preserveCurrentPreferences)
                PrepareInstallPreferences(moving ? sourceSettings : null, targetSettings, transaction);
            using var identity = WindowsIdentity.GetCurrent();
            string authorizedUserSid = identity.User?.Value ?? throw new UnauthorizedAccessException("The current Windows account is unavailable.");
            using var installer = Process.Start(PortableInstallLifecycle.CreatePrivilegedInputStartInfo(authorizedUserSid: authorizedUserSid))
                ?? throw new IOException("Windows did not start protected input setup.");
            await installer.WaitForExitAsync();
            if (installer.ExitCode != 0) throw new IOException("Protected input setup did not complete (" + installer.ExitCode + ").");
            TrackUserInstallEntries(transaction);
            InstallShortcuts(transaction, PrivilegedInput.ProtectedExecutable,
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), CurrentExecutablePath, LegacyExecutable);
            SetStartWithWindows(startup, PrivilegedInput.ProtectedExecutable);
            RegisterInstalledApplication(transaction, Registry.CurrentUser);
            ScheduleInstalledLaunchAfterExit(PrivilegedInput.ProtectedExecutable, PrivilegedInput.ProtectedDirectory,
                moving && preserveCurrentPreferences ? CurrentSettingsPath : null);
            transaction.Complete(); running?.Complete(); legacy?.Complete();
            return false; // Caller quits normally; the existing launch helper waits for that exit.
        }
        catch { if (preserveCurrentPreferences) DurableTransfers.ResumeService(); throw; }
    }

    internal static async Task DisablePrivilegedInputAsync()
    {
        using (var service = Registry.LocalMachine.OpenSubKey(PrivilegedInput.ServiceRegistryPath))
            if (service == null) return;
        using var installer = Process.Start(PortableInstallLifecycle.CreatePrivilegedInputStartInfo(remove: true))
            ?? throw new IOException("Windows did not start protected input removal.");
        await installer.WaitForExitAsync();
        if (installer.ExitCode != 0) throw new IOException("Protected input removal did not complete (" + installer.ExitCode + ").");
    }

    internal static bool IsStartWithWindowsEnabled() => IsStartWithWindowsEnabled(CurrentExecutablePath);

    private static bool IsStartWithWindowsEnabled(params string[] executables)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
            var command = key?.GetValue(StartupValueName) as string;
            return StartupCommandTargets(command, executables);
        }
        catch (Exception)
        {
            // A locked-down Windows account should still be able to run the app.
            return false;
        }
    }

    internal static bool StartupCommandTargets(string command, params string[] executables)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        foreach (string executable in executables)
            if (command.Trim().Trim('"').Equals(executable, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static void SetStartWithWindows(bool enabled)
    {
        SetStartWithWindows(enabled, CurrentExecutablePath);
    }

    internal static bool PromptToInstallCurrentPortableCopy(IWin32Window owner)
    {
        if (IsInstalledCopy)
        {
            return true;
        }

        using var dialog = new FirstLaunchForm(installingExistingPreferences: true);
        return dialog.ShowDialog(owner) != DialogResult.OK;
    }

    internal static bool? ConfirmUninstall(IWin32Window owner)
    {
        if (MessageBox.Show(owner, Ui.Text("Uninstall this copy of Mouse Without Borders?"), Ui.Text("Uninstall Mouse Without Borders"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;
        var choice = MessageBox.Show(owner, Ui.Text("Delete saved connections and settings too?\n\nChoose No to keep your connections, screen layout and preferences for a later reinstall."),
            Ui.Text("Delete connections and settings?"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        return choice == DialogResult.Cancel ? null : choice == DialogResult.Yes;
    }

    internal static bool TryRunUninstall(string[] args)
    {
        if (args.Length < 2 || args[1] != UninstallArgument) return false;
        try
        {
            if (args.Length != 2 || !IsInstalledCopy) throw new ArgumentException("Run Uninstall from Windows Installed Apps or the installed MWBP settings.");
            bool? deletePreferences = ConfirmUninstall(null);
            if (deletePreferences == null) return true;
            using var running = PortableInstallLifecycle.StopInstalledCopy(PrivilegedInput.ProtectedExecutable);
            BeginUninstall(deletePreferences.Value, running);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, Ui.Text("Uninstall Mouse Without Borders"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        return true;
    }

    internal static void BeginUninstall(bool deletePreferences, PortableInstallLifecycle.RunningCopy running = null)
    {
        string executablePath = CurrentExecutablePath;
        using var identity = WindowsIdentity.GetCurrent();
        var scripts = CreateUninstallCleanupScripts(executablePath, Environment.ProcessId, deletePreferences, identity.User.Value);
        using var cleanup = Process.Start(CreateCleanupStartInfo(scripts.Files, IsInstalledCopy))
            ?? throw new IOException("Windows did not start uninstall cleanup.");
        // This helper now owns removal; later user-cleanup errors must not restart its old GUI.
        running?.Complete();
        if (scripts.Preferences != null)
        {
            using var userCleanup = Process.Start(CreateCleanupStartInfo(scripts.Preferences, false))
                ?? throw new IOException("Windows did not start preference cleanup.");
        }
        RemoveStartupEntryIfOwnedBy(executablePath);
        RemoveProductShortcuts(executablePath, Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    internal static (string Files, string Preferences) CreateUninstallCleanupScripts(string executablePath, int processId, bool deletePreferences, string userSid)
    {
        string settingsPath = SettingsPathForExecutable(executablePath);
        string directory = Path.GetDirectoryName(executablePath)!;
        string wait = "$ErrorActionPreference='Stop';Get-Process -Id " + processId +
            " -ErrorAction SilentlyContinue | Wait-Process -Timeout 30;" +
            "if (Get-Process -Id " + processId + " -ErrorAction SilentlyContinue) { exit 1 };";
        bool protectedCopy = executablePath.Equals(PrivilegedInput.ProtectedExecutable, StringComparison.OrdinalIgnoreCase);
        var files = new StringBuilder();
        if (protectedCopy)
        {
            // A GUI EXE must enter a pipeline so this command host waits and sets LASTEXITCODE.
            files.Append("& '").Append(EscapePowerShellLiteral(PrivilegedInput.ProtectedExecutable)).Append("' '")
                .Append(PrivilegedInput.UninstallArgument).Append("' | Out-Null;if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE };");
        }
        files.Append(wait);
        files.Append("Remove-Item -LiteralPath '").Append(EscapePowerShellLiteral(executablePath)).Append("' -Force -ErrorAction Stop;");
        if (protectedCopy)
        {
            // UAC can use another administrator account. Remove only the initiating
            // user's public installation metadata after the protected EXE is gone.
            string registration = @"Registry::HKEY_USERS\" + userSid + "\\" + InstalledApplicationRegistryPath;
            files.Append("if (Test-Path -LiteralPath '").Append(EscapePowerShellLiteral(registration)).Append("') { Remove-Item -LiteralPath '")
                .Append(EscapePowerShellLiteral(registration)).Append("' -Recurse -Force -ErrorAction Stop };");
        }
        if (!protectedCopy && deletePreferences) AppendPreferenceRemoval(files, settingsPath);
        files.Append("if ((Test-Path -LiteralPath '").Append(EscapePowerShellLiteral(directory)).Append("') -and -not (Get-ChildItem -LiteralPath '")
            .Append(EscapePowerShellLiteral(directory)).Append("' -Force | Select-Object -First 1)) { Remove-Item -LiteralPath '")
            .Append(EscapePowerShellLiteral(directory)).Append("' -Force }");
        string preferencesScript = null;
        if (protectedCopy && deletePreferences)
        {
            // User paths are deleted with the user's token, never by the elevated helper.
            var preferences = new StringBuilder(wait);
            AppendPreferenceRemoval(preferences, settingsPath);
            AppendPreferenceRemoval(preferences, SettingsPathForExecutable(LegacyExecutable));
            preferencesScript = preferences.ToString();
        }
        return (files.ToString(), preferencesScript);
    }

    internal static (string Name, object Value)[] InstalledApplicationValues(string executablePath, string version) =>
    [
        ("DisplayName", StandaloneProduct.DisplayName), ("DisplayVersion", version),
        ("DisplayIcon", executablePath + ",0"), ("InstallLocation", Path.GetDirectoryName(executablePath)!),
        ("UninstallString", '"' + executablePath + "\" " + UninstallArgument),
        ("URLInfoAbout", StandaloneProduct.ProjectUrl), ("NoModify", 1), ("NoRepair", 1),
    ];

    internal static void RegisterInstalledApplication(PortableInstallTransaction transaction, RegistryKey root)
    {
        var values = InstalledApplicationValues(PrivilegedInput.ProtectedExecutable, Application.ProductVersion);
        using var previous = root.OpenSubKey(InstalledApplicationRegistryPath);
        var saved = previous == null ? null : values.Select(value =>
        {
            object stored = previous.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (value.Name, Value: stored, Kind: stored == null ? RegistryValueKind.String : previous.GetValueKind(value.Name));
        }).ToArray();
        transaction.OnRollback(() =>
        {
            if (saved == null) { root.DeleteSubKeyTree(InstalledApplicationRegistryPath, false); return; }
            using var restored = root.CreateSubKey(InstalledApplicationRegistryPath);
            foreach (var value in saved)
                if (value.Value == null) restored.DeleteValue(value.Name, false);
                else restored.SetValue(value.Name, value.Value, value.Kind);
        });
        using var registration = root.CreateSubKey(InstalledApplicationRegistryPath);
        foreach (var value in values) registration.SetValue(value.Name, value.Value,
            value.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    private static void AppendPreferenceRemoval(StringBuilder script, string settingsPath)
    {
        foreach (string path in new[] { settingsPath, settingsPath + ".bak" })
            script.Append("Remove-Item -LiteralPath '").Append(EscapePowerShellLiteral(path)).Append("' -Force -ErrorAction SilentlyContinue;");
    }

    private static ProcessStartInfo CreateCleanupStartInfo(string script, bool elevated)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = elevated, CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden };
        if (elevated) info.Verb = "runas";
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return info;
    }

    internal static void SavePreferencesForInstall(string sourceSettingsPath, string installedSettingsPath)
    {
        var settings = PortableSettingsStore.Read(sourceSettingsPath);

        settings.AppMode = AppModeInstalled;
        settings.Properties ??= new MouseWithoutBordersProperties();
        // Explicit migration has a validated source and its caller owns destination
        // rollback. A damaged old destination must not block that chosen source.
        string staged = installedSettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { SaveSettingsDocument(staged, settings); File.Move(staged, installedSettingsPath, overwrite: true); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    internal static void MigratePreferencesAfterExit(string sourceSettingsPath, string targetSettingsPath)
    {
        // The normal launch helper runs this only after the old process's final settings/recovery save.
        using var transaction = new PortableInstallTransaction();
        PrepareInstallPreferences(sourceSettingsPath, targetSettingsPath, transaction);
        transaction.Complete();
    }

    internal static string SelectInstallSource(string currentSettingsPath, string legacySettingsPath, string targetSettingsPath,
        bool preserveCurrentPreferences) => preserveCurrentPreferences ? currentSettingsPath
            : !File.Exists(targetSettingsPath) && !File.Exists(targetSettingsPath + ".bak")
                && (File.Exists(legacySettingsPath) || File.Exists(legacySettingsPath + ".bak")) ? legacySettingsPath : null;

    internal static void PrepareInstallPreferences(string sourceSettingsPath, string targetSettingsPath, PortableInstallTransaction transaction)
    {
        string targetDirectory = Path.GetDirectoryName(targetSettingsPath)!;
        Directory.CreateDirectory(targetDirectory);
        bool targetExists = File.Exists(targetSettingsPath) || File.Exists(targetSettingsPath + ".bak");
        if (sourceSettingsPath == null)
        {
            // Updating from Downloads must keep canonical preferences, including
            // fields from a newer version, and the existing recovery documents.
            if (targetExists) _ = PortableSettingsStore.Read(targetSettingsPath);
            else { transaction.TrackFile(targetSettingsPath); SaveInitialSettings(targetSettingsPath, AppModeInstalled); }
            return;
        }
        string sourceDirectory = Path.GetDirectoryName(sourceSettingsPath)!;
        foreach (string name in new[] { SettingsFileName, SettingsFileName + ".bak", "MouseWithoutBorders.transfers.json", "MouseWithoutBorders.transfers.json.bak" })
            transaction.TrackFile(Path.Combine(targetDirectory, name));
        SavePreferencesForInstall(sourceSettingsPath, targetSettingsPath);
        foreach (string name in new[] { SettingsFileName + ".bak", "MouseWithoutBorders.transfers.json", "MouseWithoutBorders.transfers.json.bak" })
        {
            string source = Path.Combine(sourceDirectory, name), target = Path.Combine(targetDirectory, name);
            if (File.Exists(source)) CopyFileAtomically(source, target);
            else File.Delete(target);
        }
    }

    private static void CopyFileAtomically(string source, string target)
    {
        string staged = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, staged); File.Move(staged, target, overwrite: true); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static void TrackUserInstallEntries(PortableInstallTransaction transaction)
    {
        using var startupKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
        object previous = startupKey?.GetValue(StartupValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = previous == null ? RegistryValueKind.String : startupKey.GetValueKind(StartupValueName);
        transaction.OnRollback(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);
            if (previous == null) key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            else key.SetValue(StartupValueName, previous, kind);
        });
    }

    internal static void SaveInitialSettings(string settingsPath, string appMode)
    {
        var isNewSettingsFile = !File.Exists(settingsPath);
        var settings = isNewSettingsFile
            ? new MouseWithoutBordersSettings()
            : PortableSettingsStore.Read(settingsPath);

        settings.AppMode = appMode;
        settings.Properties ??= new MouseWithoutBordersProperties();
        if (isNewSettingsFile)
        {
            // The portable launcher creates the file before the imported MWB settings
            // loader runs. Mark it as new so MWB still opens its machine/key setup UI.
            settings.Properties.FirstRun = true;
        }

        SaveSettingsDocument(settingsPath, settings);
    }

    private static void SetStartWithWindows(bool enabled, string executablePath)
    {
        if (!enabled)
        {
            RemoveStartupEntryIfOwnedBy(executablePath);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not provide access to the current-user startup settings.");
        key.SetValue(StartupValueName, '"' + executablePath + '"', RegistryValueKind.String);
    }

    private static void RemoveStartupEntryIfOwnedBy(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
        var command = key?.GetValue(StartupValueName) as string;
        if (StartupCommandTargets(command, executablePath))
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    internal static void InstallShortcuts(PortableInstallTransaction transaction, string executablePath,
        string programsDirectory, string desktopDirectory, params string[] previousExecutables)
    {
        var ownedExecutables = previousExecutables.Append(executablePath).ToArray();
        foreach (string directory in new[] { programsDirectory, desktopDirectory })
        {
            string shortcut = Path.Combine(directory, ShortcutName), legacy = Path.Combine(directory, LegacyShortcutName);
            // Leave a user's unrelated shortcut and its legacy alternative intact.
            if (File.Exists(shortcut) && !ownedExecutables.Any(executable => ShortcutIsOwnedBy(shortcut, executable))) continue;
            transaction.TrackFile(shortcut);
            CreateShortcut(shortcut, executablePath);
            if (ownedExecutables.Any(executable => ShortcutIsOwnedBy(legacy, executable)))
            {
                transaction.TrackFile(legacy);
                File.Delete(legacy);
            }
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // Refresh shell icon associations after replacing an executable.
    }

    internal static void RemoveProductShortcuts(string executablePath, string programsDirectory, string desktopDirectory)
    {
        foreach (string directory in new[] { programsDirectory, desktopDirectory })
            foreach (string name in new[] { ShortcutName, LegacyShortcutName })
                RemoveShortcutIfOwnedBy(Path.Combine(directory, name), executablePath);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    internal static void CreateShortcut(string shortcutPath, string executablePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new NotSupportedException("Windows shortcut support is unavailable.");
        object shell = null;
        object shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath })!;

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executablePath });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executablePath)! });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { executablePath + ",0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "VEIL MWBP — Mouse Without Borders Plus" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                _ = Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    internal static void RemoveShortcutIfOwnedBy(string shortcutPath, string executablePath)
    {
        if (ShortcutIsOwnedBy(shortcutPath, executablePath)) File.Delete(shortcutPath);
    }

    private static bool ShortcutIsOwnedBy(string shortcutPath, string executablePath)
    {
        if (!File.Exists(shortcutPath)) return false;
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new NotSupportedException("Windows shortcut support is unavailable.");
        object shell = null;
        object shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            string target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            return !string.IsNullOrWhiteSpace(target)
                && Path.GetFullPath(target).Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) _ = Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) _ = Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void SaveSettingsDocument(string settingsPath, MouseWithoutBordersSettings settings)
    {
        PortableSettingsStore.Write(settingsPath, JsonSerializer.Serialize(settings, SettingsUtils.SerializerOptions));
    }

    private static void ScheduleInstalledLaunchAfterExit(string executablePath, string workingDirectory, string sourceSettings = null)
    {
        using var parent = Process.GetCurrentProcess();
        using var launch = Process.Start(PortableInstallLifecycle.CreateLaunchHelperStartInfo(
            executablePath, workingDirectory, parent.Id, parent.StartTime.ToUniversalTime().Ticks, sourceSettings))
            ?? throw new InvalidOperationException("Windows could not start the installed copy.");
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

#endif
