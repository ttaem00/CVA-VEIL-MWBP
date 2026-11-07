using System.Reflection;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class LogonSessionTests
{
    [DataTestMethod]
    [DataRow(null, null, 2, null)]
    [DataRow("owner", null, -1, null)]
    [DataRow("owner", "other", 2, null)]
    [DataRow("owner", null, 2, LogonSession.Argument)]
    [DataRow("owner", "owner", 2, PrivilegedInput.AgentArgument)]
    public void ConsoleOwnershipSelectsBootOrUserWorkerWithoutWaitingForAUserToken(string? owner, string? user, int session, string? expected)
        => Assert.AreEqual(expected, LogonSession.SelectRole(owner!, user!, session));

    [TestMethod]
    public void LogonRoleCannotPersistOrCreatePreferencesOrAcceptNonInputCommands()
    {
        var active = typeof(LogonSession).GetField("<Active>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        object old = active.GetValue(null)!;
        string folder = Path.Combine(Path.GetTempPath(), "mwb-logon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "prefs.json");
        try
        {
            active.SetValue(null, true);
            var settings = new MouseWithoutBordersSettings();
            string document = JsonSerializer.Serialize(settings, SettingsUtils.SerializerOptions);
            new SettingsUtils(path).SaveSettings(document, "MouseWithoutBorders");
            Assert.IsFalse(File.Exists(path), "Even direct settings-owner saves cannot create a SYSTEM/user preferences file.");
            File.WriteAllText(path, "preserved");
            new SettingsUtils(path).SaveSettings(document, "MouseWithoutBorders");
            Assert.AreEqual("preserved", File.ReadAllText(path));
            foreach (PackageType type in new[] { PackageType.SharedSettings, PackageType.Clipboard, PackageType.ClipboardText,
                PackageType.ClipboardImage, PackageType.ExplorerDragDrop, PackageType.CaptureScreenCommand, PackageType.ClipboardDragDropOperation })
                Assert.IsFalse(LogonSession.Allows(type), type.ToString());
            Assert.IsTrue(LogonSession.Allows(PackageType.Keyboard)); Assert.IsTrue(LogonSession.Allows(PackageType.Mouse));
            Assert.ThrowsException<InvalidDataException>(() => LogonSession.ValidatePreferences(settings));
            settings.Properties.SecurityKey.Value = "synthetic-logon-key"; settings.Properties.MachineID.Value = 101;
            settings.Properties.MachineMatrixString = ["A", "B", "", ""];
            LogonSession.ValidatePreferences(settings);
            settings.Properties.MachineID.Value = (int)ID.ALL;
            Assert.ThrowsException<InvalidDataException>(() => LogonSession.ValidatePreferences(settings));
        }
        finally { active.SetValue(null, old); Directory.Delete(folder, true); }
    }
}
