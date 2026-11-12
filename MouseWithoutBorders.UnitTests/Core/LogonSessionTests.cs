using System.Reflection;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class LogonSessionTests
{
    [DataTestMethod]
    [DataRow(null, null, 2, true, null)]
    [DataRow("owner", null, -1, true, null)]
    [DataRow("owner", "other", 2, true, null)]
    [DataRow("owner", null, 2, true, LogonSession.Argument)]
    [DataRow("owner", "owner", 2, true, PrivilegedInput.AgentArgument)]
    // Without approval screen control nothing runs on the sign-in screen; the signed-in worker is unaffected.
    [DataRow("owner", null, 2, false, null)]
    [DataRow("owner", "owner", 2, false, PrivilegedInput.AgentArgument)]
    [DataRow("owner", "other", 2, false, null)]
    public void ConsoleOwnershipSelectsBootOrUserWorkerWithoutWaitingForAUserToken(string? owner, string? user, int session, bool approvalScreens, string? expected)
        => Assert.AreEqual(expected, LogonSession.SelectRole(owner!, user!, session, approvalScreens));

    [TestMethod]
    public void ApprovalScreensStayAllowedAsInstalledUntilAnAdministratorBlocksThem()
    {
        // An installation made before this setting existed has no value and must keep working.
        Assert.IsTrue(PrivilegedInput.ReadApprovalScreens(() => null!));
        Assert.IsTrue(PrivilegedInput.ReadApprovalScreens(() => 0));
        // Any stored value other than 0 is read as the safer answer.
        foreach (object stored in new object[] { 1, 2, -1, "0", "1", 0L, 1L, false, true, new byte[] { 0 } })
            Assert.IsFalse(PrivilegedInput.ReadApprovalScreens(() => stored), stored.ToString());
        Assert.IsFalse(PrivilegedInput.ReadApprovalScreens(() => throw new UnauthorizedAccessException()));
        Assert.IsFalse(PrivilegedInput.ReadApprovalScreens(() => throw new IOException()));
        Assert.IsFalse(PrivilegedInput.ReadApprovalScreens(() => throw new System.Security.SecurityException()));
    }

    [TestMethod]
    public void OnlyTheOrdinaryDesktopReceivesInputWhileApprovalScreensAreBlocked()
    {
        Assert.IsFalse(PrivilegedInput.DesktopBlocked("Default", false));
        Assert.IsFalse(PrivilegedInput.DesktopBlocked("default", false));
        foreach (string name in new[] { "Winlogon", "Screen-saver", "Disconnect", "", "Default2", "Defaul", " Default", "Default\0" })
            Assert.IsTrue(PrivilegedInput.DesktopBlocked(name, false), "'" + name + "'");
        foreach (string name in new[] { "Default", "Winlogon", "" })
            Assert.IsFalse(PrivilegedInput.DesktopBlocked(name, true), "'" + name + "'");
    }

    [TestMethod]
    public void OnlyLettingGoCountsAsAReleaseThatMayReachABlockedScreen()
    {
        NativeMethods.INPUT64 Key(int flags) { NativeMethods.INPUT64 input = default; input.type = 1; input.ki.wVk = 0x0D; input.ki.dwFlags = flags; return input; }
        NativeMethods.INPUT64 Mouse(int flags) { NativeMethods.INPUT64 input = default; input.type = 0; input.mi.dwFlags = flags; return input; }
        Assert.IsTrue(PrivilegedInput.IsPureRelease(Key((int)NativeMethods.KEYEVENTF.KEYUP)));
        Assert.IsTrue(PrivilegedInput.IsPureRelease(Key((int)(NativeMethods.KEYEVENTF.KEYUP | NativeMethods.KEYEVENTF.EXTENDEDKEY))));
        Assert.IsFalse(PrivilegedInput.IsPureRelease(Key(0)), "A key press is not a release.");
        foreach (int up in new[] { 4, 16, 64, 256 }) Assert.IsTrue(PrivilegedInput.IsPureRelease(Mouse(up)), up.ToString());
        // Presses, movement, wheels, or a release combined with any of them must stay blocked.
        foreach (int flags in new[] { 0, 1, 2, 8, 32, 128, 0x800, 0x1000, 0x8000, 4 | 1, 4 | 0x8000, 4 | 2, 16 | 8, 256 | 0x800 })
            Assert.IsFalse(PrivilegedInput.IsPureRelease(Mouse(flags)), flags.ToString());
        NativeMethods.INPUT64 other = default; other.type = 2;
        Assert.IsFalse(PrivilegedInput.IsPureRelease(other));
    }

    [TestMethod]
    public void ABlockedApprovalScreenTravelsInTheWorkerReplyAsItsOwnStage()
    {
        byte[] reply = new byte[PrivilegedInput.ReplySize];
        var blocked = new PrivilegedInput.InjectionFailure(PrivilegedInput.InjectionStage.ApprovalScreenBlocked, 5);
        PrivilegedInput.WriteReply(reply, 0, 0, blocked);
        Assert.AreEqual(blocked, PrivilegedInput.ReadFailure(reply));
        Assert.AreEqual(5u, (uint)PrivilegedInput.InjectionStage.ApprovalScreenBlocked, "Existing stage numbers must not move.");
    }

    [TestMethod]
    public void ChangingApprovalScreensNeedsTheSameAdministratorPoliciesAsTheService()
    {
        var on = PowerToys.GPOWrapper.GpoRuleConfigured.Enabled; var off = PowerToys.GPOWrapper.GpoRuleConfigured.Disabled;
        Assert.IsTrue(PrivilegedInput.PolicyAllowsRole(PrivilegedInput.ApprovalScreensArgument, on, on));
        Assert.IsFalse(PrivilegedInput.PolicyAllowsRole(PrivilegedInput.ApprovalScreensArgument, on, off));
        Assert.IsFalse(PrivilegedInput.PolicyAllowsRole(PrivilegedInput.ApprovalScreensArgument, off, on));
        var info = PortableInstallLifecycle.CreateApprovalScreensStartInfo(true);
        Assert.AreEqual("runas", info.Verb);
        CollectionAssert.AreEqual(new[] { PrivilegedInput.ApprovalScreensArgument, "on" }, info.ArgumentList.ToArray());
        CollectionAssert.AreEqual(new[] { PrivilegedInput.ApprovalScreensArgument, "off" },
            PortableInstallLifecycle.CreateApprovalScreensStartInfo(false).ArgumentList.ToArray());
    }

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
