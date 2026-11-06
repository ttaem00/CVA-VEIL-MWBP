// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using PowerToys.GPOWrapper;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PortableApplicationTests
{
    [TestMethod]
    public void InstalledAppsUsesTheQuotedNormalUninstallEntryPoint()
    {
        string executable = @"C:\Program Files\MWBP\MouseWithoutBordersPlus.exe";
        var values = PortableApplication.InstalledApplicationValues(executable, "1.2.3-preview").ToDictionary(value => value.Name, value => value.Value);
        Assert.AreEqual("\"" + executable + "\" --uninstall", values["UninstallString"]);
        Assert.AreEqual("1.2.3-preview", values["DisplayVersion"]);
        Assert.AreEqual(Path.GetDirectoryName(executable), values["InstallLocation"]);
        Assert.AreEqual(1, values["NoModify"]); Assert.AreEqual(1, values["NoRepair"]);
        Assert.IsFalse(((string)values["UninstallString"]).Contains(PrivilegedInput.UninstallArgument));
        Assert.IsFalse(PortableApplication.TryRunUninstall([executable]));
    }

    [TestMethod]
    public void InstalledAppsRegistrationRollsBackFreshAndExistingEntries()
    {
        // Only this unique synthetic subtree is writable; never the real Installed Apps entry.
        string testRoot = @"Software\MouseWithoutBordersPlus.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(testRoot);
            using (var cancelled = new PortableInstallTransaction()) PortableApplication.RegisterInstalledApplication(cancelled, root);
            using (var removed = root.OpenSubKey(PortableApplication.InstalledApplicationRegistryPath)) Assert.IsNull(removed);
            using (var original = root.CreateSubKey(PortableApplication.InstalledApplicationRegistryPath))
            {
                original.SetValue("DisplayVersion", "old-preview"); original.SetValue("NoModify", 0);
                original.SetValue("UnrelatedMetadata", "preserve");
            }
            using (var cancelled = new PortableInstallTransaction()) PortableApplication.RegisterInstalledApplication(cancelled, root);
            using (var restored = root.OpenSubKey(PortableApplication.InstalledApplicationRegistryPath)!)
            {
                Assert.AreEqual("old-preview", restored.GetValue("DisplayVersion"));
                Assert.AreEqual(0, restored.GetValue("NoModify")); Assert.AreEqual(RegistryValueKind.DWord, restored.GetValueKind("NoModify"));
                Assert.IsNull(restored.GetValue("UninstallString")); Assert.AreEqual("preserve", restored.GetValue("UnrelatedMetadata"));
            }
            using (var installed = new PortableInstallTransaction())
            {
                PortableApplication.RegisterInstalledApplication(installed, root); installed.Complete();
            }
            using var current = root.OpenSubKey(PortableApplication.InstalledApplicationRegistryPath)!;
            Assert.AreEqual("\"" + PrivilegedInput.ProtectedExecutable + "\" --uninstall", current.GetValue("UninstallString"));
            Assert.AreEqual(RegistryValueKind.DWord, current.GetValueKind("NoModify"));
            Assert.AreEqual("preserve", current.GetValue("UnrelatedMetadata"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testRoot, false); }
    }

    [TestMethod]
    public void ProtectedUninstallRemovesOnlyItsInitiatingUsersRegistrationAfterTheExecutable()
    {
        const string sid = "S-1-5-21-101-202-303-1001";
        var preserve = PortableApplication.CreateUninstallCleanupScripts(PrivilegedInput.ProtectedExecutable, 1234, false, sid);
        Assert.IsNull(preserve.Preferences, "Keeping connections must not start a preference-deletion helper.");
        string registryPath = @"Registry::HKEY_USERS\" + sid + "\\" + PortableApplication.InstalledApplicationRegistryPath;
        StringAssert.Contains(preserve.Files, "'" + PrivilegedInput.UninstallArgument + "' | Out-Null;if ($LASTEXITCODE -ne 0)",
            "The GUI input-role process must finish before its exit code can authorize file and ARP removal.");
        int deletedExe = preserve.Files.IndexOf("Remove-Item -LiteralPath '" + PrivilegedInput.ProtectedExecutable + "' -Force -ErrorAction Stop;", StringComparison.Ordinal);
        int registration = preserve.Files.IndexOf(registryPath, StringComparison.Ordinal);
        Assert.IsTrue(deletedExe >= 0 && registration > deletedExe, "The ARP entry remains available if service or executable removal fails.");
        Assert.IsFalse(preserve.Files.Contains("HKEY_CURRENT_USER"), "Alternate-admin UAC must not remove that administrator's registration.");
        Assert.IsFalse(preserve.Files.Contains(PortableApplication.SettingsFileName), "The elevated helper must not delete user preferences.");
        var delete = PortableApplication.CreateUninstallCleanupScripts(PrivilegedInput.ProtectedExecutable, 1234, true, sid);
        StringAssert.Contains(delete.Preferences, PortableApplication.SettingsPathForExecutable(PrivilegedInput.ProtectedExecutable));
        StringAssert.Contains(delete.Preferences, PortableApplication.SettingsPathForExecutable(PortableApplication.LegacyExecutable));
        Assert.IsFalse(delete.Preferences.Contains("Registry::"));
        var portable = PortableApplication.CreateUninstallCleanupScripts(@"C:\Synthetic O'Brien\MWBP.exe", 1234, true, sid);
        Assert.IsFalse(portable.Files.Contains("Registry::"), "Removing an arbitrary portable copy must not remove the protected installation's entry.");
        StringAssert.Contains(portable.Files, "Synthetic O''Brien");
        Assert.IsNull(portable.Preferences);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(15100)]
    [DataRow(18400)]
    public void ExplicitStartupPortPersistsWithoutReplacingOtherPreferences(int previousPort)
    {
        WithTemporarySettingsPath(path =>
        {
            var existing = new MouseWithoutBordersSettings();
            existing.Properties.SecurityKey.Value = "synthetic-port-test-key";
            existing.Properties.MachineID.Value = 123;
            existing.Properties.Name2IP = "SYNTHETIC-PC 192.0.2.10";
            existing.Properties.MachineMatrixString = ["SYNTHETIC-PC", "OTHER-PC", "", ""];
            existing.Properties.TCPPort.Value = previousPort < 0 ? 15100 : previousPort;
            var document = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(existing, SettingsUtils.SerializerOptions))!;
            if (previousPort < 0) Assert.IsTrue(document["properties"]!.AsObject().Remove("TCPPort"));
            File.WriteAllText(path, document.ToJsonString());
            string journal = Path.Combine(Path.GetDirectoryName(path)!, "MouseWithoutBorders.transfers.json");
            File.WriteAllText(journal, "synthetic journal, leave unchanged");
            var settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
            int? port = MouseWithoutBorders.Class.Program.ReadStartupPort(["MouseWithoutBordersPlus.exe", "--port", "15200"]);
            settings.SetTcpPortSynchronously(port!.Value);
            var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
            Assert.AreEqual(15200, reloaded.TcpPort);
            Assert.AreEqual("synthetic-port-test-key", reloaded.MyKey);
            Assert.AreEqual(123, reloaded.MachineId);
            Assert.AreEqual("SYNTHETIC-PC 192.0.2.10", reloaded.Name2IP);
            Assert.AreEqual("SYNTHETIC-PC,OTHER-PC,,", reloaded.MachineMatrixString);
            Assert.AreEqual("synthetic journal, leave unchanged", File.ReadAllText(journal));
            Assert.IsNull(MouseWithoutBorders.Class.Program.ReadStartupPort(["MouseWithoutBordersPlus.exe"]));
            Assert.AreEqual(15200, reloaded.TcpPort, "An ordinary subsequent launch keeps the explicitly adopted port.");
        });
    }

    [TestMethod]
    public void StartupWithoutPortPreservesTheExistingCustomPortAndFile()
    {
        WithTemporarySettingsPath(path =>
        {
            var existing = new MouseWithoutBordersSettings();
            existing.Properties.TCPPort.Value = 18400;
            File.WriteAllText(path, JsonSerializer.Serialize(existing, SettingsUtils.SerializerOptions));
            string original = File.ReadAllText(path);
            var settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
            var port = MouseWithoutBorders.Class.Program.ReadStartupPort(["app.exe"]);
            if (port.HasValue) settings.SetTcpPortSynchronously(port.Value);
            Assert.IsNull(port);
            Assert.AreEqual(18400, settings.TcpPort);
            Assert.AreEqual(original, File.ReadAllText(path));
        });
    }

    [TestMethod]
    [DataRow("1", 1)]
    [DataRow("65534", 65534)]
    public void StartupPortAcceptsTheUsableTcpPairRange(string value, int expected)
        => Assert.AreEqual(expected, MouseWithoutBorders.Class.Program.ReadStartupPort(["app.exe", "--port", value]));

    [TestMethod]
    [DataRow("0", true)]
    [DataRow("65535", true)]
    [DataRow("-1", true)]
    [DataRow("not-a-port", false)]
    [DataRow("", false)]
    public void InvalidStartupPortCannotChangeExistingPreferences(string value, bool outsidePortRange)
    {
        WithTemporarySettingsPath(path =>
        {
            var existing = new MouseWithoutBordersSettings();
            existing.Properties.TCPPort.Value = 18400;
            File.WriteAllText(path, JsonSerializer.Serialize(existing, SettingsUtils.SerializerOptions));
            string original = File.ReadAllText(path);
            var settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
            Action apply = () =>
            {
                var port = MouseWithoutBorders.Class.Program.ReadStartupPort(value.Length == 0 ? ["app.exe", "--port"] : ["app.exe", "--port", value]);
                if (port.HasValue) settings.SetTcpPortSynchronously(port.Value);
            };
            if (outsidePortRange) Assert.ThrowsException<InvalidDataException>(apply);
            else Assert.ThrowsException<ArgumentException>(apply);
            Assert.AreEqual(18400, settings.TcpPort);
            Assert.AreEqual(original, File.ReadAllText(path));
        });
    }

    [TestMethod]
    public void ClipboardHelperMarkerShouldBeRecognizedAsFirstArgument()
    {
        Assert.IsTrue(PortableApplication.IsClipboardHelperInvocation(
            new[] { "MouseWithoutBorders.exe", PortableApplication.ClipboardHelperArgument }));
        Assert.IsFalse(PortableApplication.IsClipboardHelperInvocation(
            new[] { "MouseWithoutBorders.exe" }));
    }

    [TestMethod]
    public void NewPreferencesShouldOpenMwbSetupOnFirstRun()
    {
        WithTemporarySettingsPath(settingsPath =>
        {
            PortableApplication.SaveInitialSettings(settingsPath, PortableApplication.AppModePortable);

            var settings = ReadSettings(settingsPath);
            Assert.AreEqual(PortableApplication.AppModePortable, settings.AppMode);
            Assert.IsTrue(settings.Properties.FirstRun);
            Assert.IsFalse(settings.Properties.WrapMouse, "New preferences should not wrap the pointer across the outside edges of the matrix by default.");
            Assert.IsFalse(settings.Properties.KeyboardShortcutsEnabled, "New preferences should disable all keyboard shortcuts by default.");
            Assert.AreEqual(0, settings.Properties.HotKeySwitchMachine.Value, "New preferences should disable direct machine-switch shortcuts by default.");
            Assert.IsTrue(settings.Properties.ToggleEasyMouseShortcut.IsEmpty());
            Assert.IsTrue(settings.Properties.LockMachineShortcut.IsEmpty());
            Assert.IsTrue(settings.Properties.ReconnectShortcut.IsEmpty());
            Assert.IsTrue(settings.Properties.Switch2AllPCShortcut.IsEmpty());
        });
    }

    [TestMethod]
    public void ThreeFreshProfilesGenerateIndependentKeysWithoutAnExistingLayoutOrMapping()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var keys = new HashSet<string>();
            for (int pc = 0; pc < 3; pc++)
            {
                string path = Path.Combine(directory, $"new-pc-{pc}.json");
                PortableApplication.SaveInitialSettings(path, PortableApplication.AppModePortable);
                var settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                Assert.IsTrue(settings.FirstRun);
                Assert.IsTrue(string.IsNullOrEmpty(settings.MachineMatrixString));
                Assert.IsTrue(string.IsNullOrEmpty(settings.Name2IP));
                string key = settings.MyKey;
                Assert.IsTrue(Encryption.IsKeyValid(key, out _));
                Assert.IsTrue(keys.Add(key), "New PCs must not silently share a predefined key.");
                settings.SaveSettingsSynchronously();
                var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                Assert.AreEqual(key, reloaded.MyKey, "A fresh PC keeps its generated key after restart.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void InstallingOverExistingPreferencesShouldPreserveThem()
    {
        WithTemporarySettingsPath(settingsPath =>
        {
            var existing = new MouseWithoutBordersSettings();
            existing.Properties.SecurityKey.Value = "keep-this-key";
            existing.Properties.FirstRun = false;
            existing.Properties.HotKeySwitchMachine.Value = 0x70;
            existing.Properties.KeyboardShortcutsEnabled = true;
            existing.Properties.LockMachineShortcut = new HotkeySettings(false, true, true, false, 'L');
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(existing, SettingsUtils.SerializerOptions));

            PortableApplication.SaveInitialSettings(settingsPath, PortableApplication.AppModeInstalled);

            var settings = ReadSettings(settingsPath);
            Assert.AreEqual(PortableApplication.AppModeInstalled, settings.AppMode);
            Assert.AreEqual("keep-this-key", settings.Properties.SecurityKey.Value);
            Assert.IsFalse(settings.Properties.FirstRun);
            Assert.AreEqual(0x70, settings.Properties.HotKeySwitchMachine.Value);
            Assert.IsTrue(settings.Properties.KeyboardShortcutsEnabled);
            Assert.AreEqual('L', settings.Properties.LockMachineShortcut.Code);
        });
    }

    [TestMethod]
    public void InstallingPortableCopyShouldCopyCurrentPreferencesAndChangeMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.Tests", Guid.NewGuid().ToString("N"));
        var installDirectory = Path.Combine(directory, "installed");
        Directory.CreateDirectory(installDirectory);
        try
        {
            var sourceSettingsPath = Path.Combine(directory, PortableApplication.SettingsFileName);
            var installedSettingsPath = Path.Combine(installDirectory, PortableApplication.SettingsFileName);
            var existing = new MouseWithoutBordersSettings();
            existing.AppMode = PortableApplication.AppModePortable;
            existing.Properties.SecurityKey.Value = "keep-this-portable-key";
            existing.Properties.FirstRun = false;
            File.WriteAllText(sourceSettingsPath, JsonSerializer.Serialize(existing, SettingsUtils.SerializerOptions));

            PortableApplication.SavePreferencesForInstall(sourceSettingsPath, installedSettingsPath);

            var installed = ReadSettings(installedSettingsPath);
            Assert.AreEqual(PortableApplication.AppModeInstalled, installed.AppMode);
            Assert.AreEqual("keep-this-portable-key", installed.Properties.SecurityKey.Value);
            Assert.IsFalse(installed.Properties.FirstRun);
            Assert.IsTrue(File.Exists(sourceSettingsPath), "The running copy keeps its source prefs until it exits.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void InvalidPortablePreferencesShouldNotReplaceInstalledPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceSettingsPath = Path.Combine(directory, "source.json");
            var installedSettingsPath = Path.Combine(directory, "installed.json");
            File.WriteAllText(sourceSettingsPath, "not valid JSON");
            File.WriteAllText(installedSettingsPath, "keep existing destination");

            _ = Assert.ThrowsException<InvalidDataException>(
                () => PortableApplication.SavePreferencesForInstall(sourceSettingsPath, installedSettingsPath));

            Assert.AreEqual("keep existing destination", File.ReadAllText(installedSettingsPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ProtectedMigrationUsesTheLastSavedPreferencesAndRecoveryAfterParentExit()
    {
        WithTemporarySettingsPath(source =>
        {
            string sourceDirectory = Path.GetDirectoryName(source)!;
            string targetDirectory = Path.Combine(sourceDirectory, "protected-user-data");
            Directory.CreateDirectory(targetDirectory);
            string target = Path.Combine(targetDirectory, PortableApplication.SettingsFileName);
            PortableApplication.SaveInitialSettings(source, PortableApplication.AppModePortable);
            PortableApplication.SavePreferencesForInstall(source, target); // Initial pre-UAC snapshot.
            var latest = ReadSettings(source);
            latest.Properties.FirstRun = false;
            latest.Properties.HotKeySwitchMachine.Value = 0x71;
            File.WriteAllText(source, JsonSerializer.Serialize(latest, SettingsUtils.SerializerOptions));
            string journalName = "MouseWithoutBorders.transfers.json";
            File.WriteAllText(Path.Combine(sourceDirectory, journalName), "latest-final-transfer-journal");
            File.WriteAllText(Path.Combine(targetDirectory, journalName), "stale-before-exit");
            File.WriteAllText(Path.Combine(targetDirectory, journalName + ".bak"), "unrelated-old-journal-backup");

            PortableApplication.MigratePreferencesAfterExit(source, target);

            var migrated = ReadSettings(target);
            Assert.AreEqual(0x71, migrated.Properties.HotKeySwitchMachine.Value);
            Assert.IsFalse(migrated.Properties.FirstRun);
            Assert.AreEqual(PortableApplication.AppModeInstalled, migrated.AppMode);
            Assert.AreEqual("latest-final-transfer-journal", File.ReadAllText(Path.Combine(targetDirectory, journalName)));
            Assert.IsFalse(File.Exists(Path.Combine(targetDirectory, journalName + ".bak")), "An absent source journal backup must not revive an unrelated target transfer.");
            Assert.IsTrue(File.Exists(source), "Adoption never deletes the original preferences.");
            Assert.IsTrue(File.Exists(Path.Combine(sourceDirectory, journalName)));
        });
    }

    [TestMethod]
    public void ProtectedMigrationFailureRestoresThePreviousDestinationDocuments()
    {
        WithTemporarySettingsPath(source =>
        {
            PortableApplication.SaveInitialSettings(source, PortableApplication.AppModePortable);
            string targetDirectory = Path.Combine(Path.GetDirectoryName(source)!, "target");
            Directory.CreateDirectory(targetDirectory);
            string target = Path.Combine(targetDirectory, PortableApplication.SettingsFileName);
            PortableApplication.SaveInitialSettings(target, PortableApplication.AppModeInstalled);
            string previous = File.ReadAllText(target);
            string journal = Path.Combine(targetDirectory, "MouseWithoutBorders.transfers.json");
            File.WriteAllText(journal, "keep-this-recovery-document");
            using (var locked = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Assert.ThrowsException<IOException>(() => PortableApplication.MigratePreferencesAfterExit(source, target));
            Assert.AreEqual(previous, File.ReadAllText(target));
            Assert.AreEqual("keep-this-recovery-document", File.ReadAllText(journal));
        });
    }

    [TestMethod]
    public void ProtectedSetupRetargetsOnlyAnExistingShortcutOwnedByTheOldExecutable()
    {
        WithTemporarySettingsPath(path =>
        {
            string directory = Path.GetDirectoryName(path)!;
            string oldExe = Path.Combine(directory, "old.exe"), newExe = Path.Combine(directory, "protected.exe"), otherExe = Path.Combine(directory, "unrelated.exe");
            string owned = Path.Combine(directory, "owned.lnk"), unrelated = Path.Combine(directory, "unrelated.lnk"), missing = Path.Combine(directory, "missing.lnk");
            PortableApplication.CreateShortcut(owned, oldExe);
            PortableApplication.CreateShortcut(unrelated, otherExe);
            byte[] original = File.ReadAllBytes(unrelated);
            PortableApplication.RetargetShortcutIfOwnedBy(owned, oldExe, newExe);
            PortableApplication.RetargetShortcutIfOwnedBy(unrelated, oldExe, newExe);
            PortableApplication.RetargetShortcutIfOwnedBy(missing, oldExe, newExe);
            PortableApplication.RemoveShortcutIfOwnedBy(owned, newExe);
            Assert.IsFalse(File.Exists(owned), "The owned Desktop entry now opens the protected executable.");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(unrelated));
            Assert.IsFalse(File.Exists(missing), "Setup must not add a Desktop shortcut the user did not choose.");
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallationMigratesTheProductNameAndRefreshesAlreadyProtectedShortcutIcons(bool alreadyProtected)
    {
        WithTemporarySettingsPath(path =>
        {
            string root = Path.GetDirectoryName(path)!;
            string programs = Path.Combine(root, "Programs"), desktop = Path.Combine(root, "Desktop");
            string oldExe = Path.Combine(root, "old.exe"), executable = Path.Combine(root, "protected.exe");
            foreach (string directory in new[] { programs, desktop })
            {
                PortableApplication.CreateShortcut(Path.Combine(directory, "Mouse Without Borders Plus.lnk"), oldExe);
                if (alreadyProtected)
                {
                    string current = Path.Combine(directory, PortableApplication.ShortcutName);
                    PortableApplication.CreateShortcut(current, oldExe);
                    ShortcutProperty(current, "TargetPath", executable); // Correct target, stale icon and description.
                    ShortcutProperty(current, "Description", "previous product name");
                }
            }
            using (var transaction = new PortableInstallTransaction())
            {
                PortableApplication.InstallShortcuts(transaction, executable, programs, desktop, oldExe);
                transaction.Complete();
            }
            Assert.AreEqual("VEIL MWBP.lnk", PortableApplication.ShortcutName, "The searchable launcher must use the product's actual name.");
            foreach (string directory in new[] { programs, desktop })
            {
                string current = Path.Combine(directory, PortableApplication.ShortcutName);
                Assert.IsFalse(File.Exists(Path.Combine(directory, "Mouse Without Borders Plus.lnk")));
                Assert.AreEqual(executable, ShortcutProperty(current, "TargetPath"), true, CultureInfo.InvariantCulture);
                string icon = ShortcutProperty(current, "IconLocation");
                int separator = icon.LastIndexOf(',');
                Assert.IsTrue(separator > 0);
                Assert.AreEqual("0", icon[(separator + 1)..].Trim());
                Assert.AreEqual(executable, icon[..separator].Trim().Trim('"'), true, CultureInfo.InvariantCulture);
                Assert.AreEqual(root, ShortcutProperty(current, "WorkingDirectory"), true, CultureInfo.InvariantCulture);
                StringAssert.Contains(ShortcutProperty(current, "Description"), "VEIL MWBP");
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedShortcutMigrationRestoresBothNamesInBothLocations(bool existingCurrentName)
    {
        WithTemporarySettingsPath(path =>
        {
            string root = Path.GetDirectoryName(path)!;
            string programs = Path.Combine(root, "Programs"), desktop = Path.Combine(root, "Desktop");
            string oldExe = Path.Combine(root, "old.exe"), executable = Path.Combine(root, "protected.exe");
            var before = new Dictionary<string, byte[]?>();
            foreach (string directory in new[] { programs, desktop })
            {
                string legacy = Path.Combine(directory, "Mouse Without Borders Plus.lnk");
                string current = Path.Combine(directory, PortableApplication.ShortcutName);
                PortableApplication.CreateShortcut(legacy, oldExe);
                if (existingCurrentName) PortableApplication.CreateShortcut(current, oldExe);
                before.Add(legacy, File.ReadAllBytes(legacy));
                before.Add(current, existingCurrentName ? File.ReadAllBytes(current) : null);
            }
            using (var transaction = new PortableInstallTransaction())
                PortableApplication.InstallShortcuts(transaction, executable, programs, desktop, oldExe);
            foreach (var entry in before)
            {
                if (entry.Value == null) Assert.IsFalse(File.Exists(entry.Key), "Rollback must remove the newly introduced launcher.");
                else CollectionAssert.AreEqual(entry.Value, File.ReadAllBytes(entry.Key), "Rollback must restore the original .lnk bytes, including user metadata.");
            }
        });
    }

    [TestMethod]
    public void ShortcutInstallationAndUninstallPreserveUnrelatedTargetsAndRemoveOnlyOwnedNames()
    {
        WithTemporarySettingsPath(path =>
        {
            string root = Path.GetDirectoryName(path)!;
            string programs = Path.Combine(root, "Programs"), desktop = Path.Combine(root, "Desktop");
            string executable = Path.Combine(root, "protected.exe"), unrelated = Path.Combine(root, "unrelated.exe");
            string programsLegacy = Path.Combine(programs, "Mouse Without Borders Plus.lnk");
            string desktopCurrent = Path.Combine(desktop, PortableApplication.ShortcutName);
            string desktopLegacy = Path.Combine(desktop, "Mouse Without Borders Plus.lnk");
            PortableApplication.CreateShortcut(programsLegacy, unrelated);
            PortableApplication.CreateShortcut(desktopCurrent, unrelated);
            PortableApplication.CreateShortcut(desktopLegacy, executable);
            byte[] programsBefore = File.ReadAllBytes(programsLegacy), desktopBefore = File.ReadAllBytes(desktopCurrent);
            byte[] legacyBefore = File.ReadAllBytes(desktopLegacy);
            var attributes = File.GetAttributes(programsLegacy);
            File.SetAttributes(programsLegacy, attributes | FileAttributes.ReadOnly);
            try
            {
                using (var transaction = new PortableInstallTransaction())
                {
                    PortableApplication.InstallShortcuts(transaction, executable, programs, desktop);
                    transaction.Complete();
                }
                Assert.IsTrue(File.Exists(Path.Combine(programs, PortableApplication.ShortcutName)));
                CollectionAssert.AreEqual(legacyBefore, File.ReadAllBytes(desktopLegacy), "An unrelated canonical launcher must leave its existing alternative intact.");
                CollectionAssert.AreEqual(programsBefore, File.ReadAllBytes(programsLegacy));
                CollectionAssert.AreEqual(desktopBefore, File.ReadAllBytes(desktopCurrent));
                Assert.IsTrue(File.GetAttributes(programsLegacy).HasFlag(FileAttributes.ReadOnly));
                Assert.IsFalse(Directory.EnumerateFiles(root, "*.rollback-*", SearchOption.AllDirectories).Any(),
                    "Unrelated read-only launchers must not be backed up or leave transaction debris.");
                PortableApplication.RemoveProductShortcuts(executable, programs, desktop);
                Assert.IsFalse(File.Exists(Path.Combine(programs, PortableApplication.ShortcutName)));
                Assert.IsFalse(File.Exists(desktopLegacy), "Uninstall must also remove the old product name when its target is this installed copy.");
                CollectionAssert.AreEqual(programsBefore, File.ReadAllBytes(programsLegacy));
                CollectionAssert.AreEqual(desktopBefore, File.ReadAllBytes(desktopCurrent));
            }
            finally { if (File.Exists(programsLegacy)) File.SetAttributes(programsLegacy, attributes); }
        });
    }

    [TestMethod]
    public void DownloadedInstallerKeepsCanonicalPreferencesAndRecoveryByteForByte()
    {
        WithTemporarySettingsPath(target =>
        {
            WriteInstallSettings(target, "canonical-group");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(target))!;
            json["future-version-field"] = "keep this too";
            File.WriteAllText(target, json.ToJsonString());
            string before = File.ReadAllText(target), journal = Path.Combine(Path.GetDirectoryName(target)!, "MouseWithoutBorders.transfers.json");
            File.WriteAllText(journal, "canonical-transfer-history");
            string legacy = Path.Combine(Path.GetDirectoryName(target)!, "legacy", PortableApplication.SettingsFileName);
            WriteInstallSettings(legacy, "older-group");
            string? source = PortableApplication.SelectInstallSource("missing-download.json", legacy, target, false);
            Assert.IsNull(source, "A package without preferences must not replace the canonical group with defaults or older legacy data.");
            using (var transaction = new PortableInstallTransaction())
            { PortableApplication.PrepareInstallPreferences(source!, target, transaction); transaction.Complete(); }
            Assert.AreEqual(before, File.ReadAllText(target));
            Assert.AreEqual("canonical-transfer-history", File.ReadAllText(journal));
        });
    }

    [TestMethod]
    public void ClosedCanonicalLegacyInstallationKeepsItsGroupAndTransferHistory()
    {
        WithTemporarySettingsPath(legacy =>
        {
            WriteInstallSettings(legacy, "existing-game-group");
            string root = Path.GetDirectoryName(legacy)!, target = Path.Combine(root, "protected", PortableApplication.SettingsFileName);
            File.WriteAllText(Path.Combine(root, "MouseWithoutBorders.transfers.json"), "paused-transfer-history");
            Assert.AreEqual(legacy, PortableApplication.SelectInstallSource("missing-download.json", legacy, target, false));
            using (var transaction = new PortableInstallTransaction())
            { PortableApplication.PrepareInstallPreferences(legacy, target, transaction); transaction.Complete(); }
            Assert.AreEqual("existing-game-group", ReadSettings(target).Properties.SecurityKey.Value);
            Assert.AreEqual("paused-transfer-history", File.ReadAllText(Path.Combine(Path.GetDirectoryName(target)!, "MouseWithoutBorders.transfers.json")));
            Assert.IsTrue(File.Exists(legacy), "Adopting the recognized old installation must not delete its original settings.");
        });
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void ExistingBackupOrDamageIsNeverTurnedIntoFreshDefaults(bool canonical, bool damaged)
    {
        WithTemporarySettingsPath(legacy =>
        {
            string target = Path.Combine(Path.GetDirectoryName(legacy)!, "protected", PortableApplication.SettingsFileName);
            string subject = canonical ? target : legacy;
            WriteInstallSettings(subject + ".bak", "preserve-backup-group");
            if (damaged) File.WriteAllText(subject, "invalid existing document");
            string backup = File.ReadAllText(subject + ".bak");
            string? source = PortableApplication.SelectInstallSource("missing-download.json", legacy, target, false);
            Assert.AreEqual(canonical ? null : legacy, source);
            using (var transaction = new PortableInstallTransaction())
            {
                if (damaged) Assert.ThrowsException<InvalidDataException>(() => PortableApplication.PrepareInstallPreferences(source!, target, transaction));
                else Assert.ThrowsException<FileNotFoundException>(() => PortableApplication.PrepareInstallPreferences(source!, target, transaction));
            }
            Assert.AreEqual(backup, File.ReadAllText(subject + ".bak"));
            Assert.AreEqual(damaged, File.Exists(subject));
            if (damaged) Assert.AreEqual("invalid existing document", File.ReadAllText(subject));
            if (!canonical) Assert.IsFalse(File.Exists(target));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitCurrentProfileCanRepairDestinationWithRollback(bool commit)
    {
        WithTemporarySettingsPath(source =>
        {
            WriteInstallSettings(source, "current-connected-group");
            string target = Path.Combine(Path.GetDirectoryName(source)!, "protected", PortableApplication.SettingsFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "old damaged target");
            Assert.AreEqual(source, PortableApplication.SelectInstallSource(source, "unrelated-legacy.json", target, true));
            using (var transaction = new PortableInstallTransaction())
            {
                PortableApplication.PrepareInstallPreferences(source, target, transaction);
                Assert.AreEqual("current-connected-group", ReadSettings(target).Properties.SecurityKey.Value);
                if (commit) transaction.Complete();
            }
            if (!commit) Assert.AreEqual("old damaged target", File.ReadAllText(target));
            Assert.AreEqual("current-connected-group", ReadSettings(source).Properties.SecurityKey.Value);
        });
    }

    [TestMethod]
    public void StartupOwnershipKeepsRecognizedPathsAndLeavesOtherCommandsAlone()
    {
        string[] owned = [@"C:\Program Files\MWBP\MWBP.exe", @"C:\Users\Example\AppData\Local\Programs\MWBP\MWBP.exe"];
        Assert.IsTrue(PortableApplication.StartupCommandTargets("  \"c:\\program files\\mwbp\\mwbp.exe\"  ", owned));
        Assert.IsTrue(PortableApplication.StartupCommandTargets(owned[1], owned));
        Assert.IsFalse(PortableApplication.StartupCommandTargets(null!, owned));
        Assert.IsFalse(PortableApplication.StartupCommandTargets(@"C:\Unrelated\MWBP.exe", owned));
        Assert.IsFalse(PortableApplication.StartupCommandTargets("\"" + owned[0] + "\" --unowned-arguments", owned));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task FirstLaunchHasOneInstallActionAndFailureRestoresCancel()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            var original = MouseWithoutBorders.Class.Setting.Values;
            bool logon = Common.RunOnLogonDesktop;
            Exception? failure = null;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
                foreach (bool migrating in new[] { false, true })
                using (var form = new FirstLaunchForm(migrating))
                {
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000);
                    var controls = form.Controls.Cast<Control>().SelectMany(control => control.Controls.Cast<Control>()).ToArray();
                    Assert.AreEqual(2, controls.OfType<Button>().Count());
                    Assert.IsFalse(controls.Any(control => control is TextBox or CheckBox), "There is one install location and no run-mode or account/folder choice.");
                    var install = (Button)controls.Single(control => control.Name == "installProduct");
                    var cancel = (Button)controls.Single(control => control.Name == "cancelInstallation");
                    var installer = typeof(FirstLaunchForm).GetMethod("Install_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    install.Click -= installer.CreateDelegate<EventHandler>(form); // A preview must never install in response to host keyboard input.
                    form.Show(); form.PerformLayout(); Application.DoEvents();
                    Assert.AreEqual(DialogResult.None, install.DialogResult, "The dialog must await the actual installer before closing.");
                    Assert.AreEqual(DialogResult.Cancel, cancel.DialogResult);
                    if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string previews)
                    {
                        Directory.CreateDirectory(Path.Combine(previews, "mwb-ui-previews"));
                        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                        image.Save(Path.Combine(previews, "mwb-ui-previews", migrating ? "install-existing-ko.png" : "install-fresh-ko.png"));
                    }
                    form.Hide();
                    if (!migrating) continue;
                    WithTemporarySettingsPath(path =>
                    {
                        WriteInstallSettings(path, "synthetic-locked-group");
                        Common.RunOnLogonDesktop = false;
                        MouseWithoutBorders.Class.Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                        Assert.ThrowsException<IOException>(() => MouseWithoutBorders.Class.Setting.Values.SaveSettingsSynchronously());
                        // This synchronous failure precedes every service, directory and UAC operation.
                        installer.Invoke(form, [install, EventArgs.Empty]);
                        Assert.IsTrue(install.Enabled); Assert.IsTrue(cancel.Enabled);
                        Assert.AreEqual(DialogResult.None, form.DialogResult);
                        Assert.IsFalse(string.IsNullOrEmpty(controls.Single(control => control.Name == "installStatus").Text));
                    });
                }
            }
            catch (Exception error) { failure = error; }
            finally { MouseWithoutBorders.Class.Setting.Values = original; Common.RunOnLogonDesktop = logon; }
            if (failure == null) done.SetResult(); else done.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    [TestMethod]
    [DataRow(1, 0, true, false)]
    [DataRow(-1, 0, true, false)]
    [DataRow(1, -1, true, true)]
    [DataRow(0, 1, false, false)]
    public void ServicePolicyDoesNotBlockBaseInstallationOrPreventRemoval(int appValue, int serviceValue, bool install, bool runService)
    {
        var app = (GpoRuleConfigured)appValue; var service = (GpoRuleConfigured)serviceValue;
        Assert.AreEqual(install, PrivilegedInput.PolicyAllowsRole(PrivilegedInput.InstallArgument, app, service));
        Assert.AreEqual(runService, PrivilegedInput.PolicyAllowsRole(PrivilegedInput.ServiceArgument, app, service));
        Assert.AreEqual(runService, PrivilegedInput.PolicyAllowsRole(PrivilegedInput.AgentArgument, app, service));
        Assert.IsTrue(PrivilegedInput.PolicyAllowsRole(PrivilegedInput.RemoveArgument, app, service));
        Assert.IsTrue(PrivilegedInput.PolicyAllowsRole(PrivilegedInput.UninstallArgument, app, service));
    }

    private static void WriteInstallSettings(string path, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new MouseWithoutBordersSettings(); settings.Properties.SecurityKey.Value = key;
        settings.Properties.MachineMatrixString = ["MAIN", "GAME", "TRUMAN", ""];
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(settings, SettingsUtils.SerializerOptions));
    }

    private static MouseWithoutBordersSettings ReadSettings(string settingsPath)
    {
        return JsonSerializer.Deserialize<MouseWithoutBordersSettings>(
            File.ReadAllText(settingsPath),
            SettingsUtils.SerializerOptions)!;
    }

    private static string ShortcutProperty(string path, string property, string? value = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
            var type = shortcut.GetType();
            if (value != null)
            {
                type.InvokeMember(property, BindingFlags.SetProperty, null, shortcut, [value]);
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
            }
            return type.InvokeMember(property, BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void WithTemporarySettingsPath(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(Path.Combine(directory, PortableApplication.SettingsFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
