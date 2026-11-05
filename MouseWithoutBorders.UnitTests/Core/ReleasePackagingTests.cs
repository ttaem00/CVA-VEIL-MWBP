using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class ReleasePackagingTests
{
    [TestMethod]
    public void AboutWindowKeepsEveryBundledLicenseReadableInFull()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var about = new FrmAbout();
                var text = (TextBox)typeof(FrmAbout).GetField("textBoxContributors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(about)!;
                Assert.IsTrue(text.ReadOnly);
                Assert.IsTrue(text.Multiline);
                foreach (string name in new[] { "MouseWithoutBorders.License", "MouseWithoutBorders.UpstreamNotices", "MouseWithoutBorders.ThirdPartyNotices" })
                {
                    using var stream = typeof(FrmAbout).Assembly.GetManifestResourceStream(name);
                    Assert.IsNotNull(stream, "The standalone EXE needs " + name);
                    using var reader = new StreamReader(stream);
                    string notice = reader.ReadToEnd();
                    Assert.IsTrue(notice.Length > 1000);
                    Assert.IsTrue(text.Text.Replace("\r", "").Contains(notice.Replace("\r", ""), StringComparison.Ordinal),
                        "The About window must not truncate " + name);
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The About window must finish loading its local notices.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
