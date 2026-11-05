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
                using var resource = typeof(FrmAbout).Assembly.GetManifestResourceStream("MouseWithoutBorders.Veil.ico");
                Assert.IsNotNull(resource, "The product icon must use VEIL artwork.");
                Assert.IsNull(typeof(FrmAbout).Assembly.GetManifestResourceStream("MouseWithoutBorders.ClassicGreen.ico"));
                using var expectedIcon = new System.Drawing.Icon(resource, 32, 32);
                using var expected = expectedIcon.ToBitmap();
                using var actual = about.Icon!.ToBitmap();
                using var send = SendFilesDialog.Create(["TEST-PC"]);
                using var sendIcon = send.Icon!.ToBitmap();
                using var message = new FrmMessage("Test notification", "", 1);
                Assert.AreEqual("MWBP", ((Label)typeof(FrmMessage).GetField("labelTitle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(message)!).Text);
                var messageImage = ((PictureBox)typeof(FrmMessage).GetField("pictureBoxIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(message)!).Image;
                using var expectedSmall = StandaloneBranding.CreateProductIconBitmap(new System.Drawing.Size(16, 16));
                var actualSmall = (System.Drawing.Bitmap)messageImage!;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++) Assert.AreEqual(expectedSmall.GetPixel(x, y), actualSmall.GetPixel(x, y));
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        Assert.AreEqual(expected.GetPixel(x, y), actual.GetPixel(x, y), "About icon differs from the embedded VEIL artwork.");
                        Assert.AreEqual(expected.GetPixel(x, y), sendIcon.GetPixel(x, y), "Send dialog icon differs from the embedded VEIL artwork.");
                    }
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
