using System.Dynamic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class NetworkSetupTests
{
    private const string ApplicationPath = @"C:\Synthetic User\Programs\MouseWithoutBordersPlus.exe";

    private static dynamic LanRule()
    {
        dynamic rule = new ExpandoObject();
        rule.ApplicationName = ApplicationPath; rule.Enabled = true;
        rule.Direction = 1; rule.Action = 1; rule.Protocol = 256;
        rule.Profiles = int.MaxValue; rule.RemoteAddresses = "LocalSubnet";
        return rule;
    }

    [TestMethod]
    public void NetworkSetupStartsTheCurrentExecutableWithoutPassingPreferencesOrInstallingAService()
    {
        var normal = PortableInstallLifecycle.CreateNetworkSetupStartInfo();
        Assert.AreEqual(Environment.ProcessPath, normal.FileName);
        CollectionAssert.AreEqual(new[] { PortableInstallLifecycle.NetworkArgument }, normal.ArgumentList.ToArray());
        Assert.IsFalse(normal.UseShellExecute); Assert.IsTrue(normal.CreateNoWindow);
        Assert.AreEqual(string.Empty, normal.Verb);
        var elevated = PortableInstallLifecycle.CreateNetworkSetupStartInfo(elevated: true);
        Assert.AreEqual(normal.FileName, elevated.FileName);
        CollectionAssert.AreEqual(normal.ArgumentList.ToArray(), elevated.ArgumentList.ToArray());
        Assert.IsTrue(elevated.UseShellExecute); Assert.AreEqual("runas", elevated.Verb);
    }

    [TestMethod]
    public void ExistingApplicationLanRuleCoversPairingDiscoveryAndConfigurableLegacyPorts()
    {
        dynamic rule = LanRule();
        Assert.IsTrue(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        rule.Profiles = 7; // Domain, Private and Public together cover all current profile types too.
        Assert.IsTrue(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath.ToUpperInvariant()));
        rule.Protocol = 6; // The old TCP-only permission cannot cover UDP discovery.
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows(null!, ApplicationPath));
    }

    [TestMethod]
    public void DisabledOutboundBlockedOrDifferentlyScopedRulesCannotPretendNetworkSetupIsComplete()
    {
        dynamic rule = LanRule();
        rule.Enabled = false;
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        rule.Enabled = true; rule.Direction = 2;
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        rule.Direction = 1; rule.Action = 0;
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        rule.Action = 1; rule.Profiles = 3;
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        rule.Profiles = int.MaxValue; rule.RemoteAddresses = "*";
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
        rule.RemoteAddresses = "LocalSubnet"; rule.ApplicationName = @"C:\Other\MouseWithoutBordersPlus.exe";
        Assert.IsFalse(PortableInstallLifecycle.NetworkRuleAllows((object)rule, ApplicationPath));
    }

    [TestMethod]
    public void UnrelatedArgumentsDoNotStartNetworkSetup()
    {
        Assert.IsFalse(PortableInstallLifecycle.TryRunNetworkSetup(["app.exe"]));
        Assert.IsFalse(PortableInstallLifecycle.TryRunNetworkSetup(["app.exe", "--port", "15200"]));
    }

    [TestMethod]
    [DoNotParallelize]
    public void MalformedNetworkRoleFailsBeforeAnyFirewallOrElevationAccess()
    {
        int previous = Environment.ExitCode;
        try
        {
            Assert.IsTrue(PortableInstallLifecycle.TryRunNetworkSetup(["app.exe", PortableInstallLifecycle.NetworkArgument, "unexpected"]));
            Assert.AreEqual(1, Environment.ExitCode);
        }
        finally { Environment.ExitCode = previous; }
    }
}
