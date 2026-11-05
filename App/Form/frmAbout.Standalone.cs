// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if PORTABLE_SINGLE_FILE

using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmAbout
{
    private void ApplyPortableAboutContent()
    {
        Opacity = 1D;
        Text = Ui.Text("About MWBP");
        labelProductName.Text = $"{StandaloneProduct.DisplayName} {Application.ProductVersion}";
        labelCompanyName.Text = "Original creator: Truong Do (Đỗ Đức Trường)";
        groupBoxContributors.Text = Ui.Text("Credits and open-source licenses");

        StandaloneBranding.Apply(this);
        logoPictureBox.Image = StandaloneBranding.CreateProductIconBitmap(new Size(128, 128));
        logoPictureBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;

        var check = new Button { Text = "Check for updates", Location = new Point(9, 532), Size = new Size(140, 27), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        var result = new LinkLabel { Location = new Point(155, 537), Size = new Size(242, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AutoEllipsis = true };
        // No network activity until the button is clicked.
        string releaseUrl = ManualUpdates.Releases;
        var cancelCheck = new CancellationTokenSource();
        FormClosed += (_, _) => { cancelCheck.Cancel(); cancelCheck.Dispose(); };
        result.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
        check.Click += async (_, _) =>
        {
            check.Enabled = false; result.Text = "Checking GitHub…"; result.Links.Clear();
            try
            {
                string tag = await ManualUpdates.Check(Application.ProductVersion, cancelCheck.Token);
                if (IsDisposed) return;
                result.Text = tag == null ? "You're up to date" : tag.Replace("mwb-v", "") + " available — GitHub";
                releaseUrl = tag == null ? ManualUpdates.Releases : ManualUpdates.Releases + "/tag/" + Uri.EscapeDataString(tag);
                if (tag != null) result.Links.Add(0, result.Text.Length);
            }
            catch (OperationCanceledException) { if (!IsDisposed) { result.Text = "Check timed out — open GitHub"; result.Links.Add(0, result.Text.Length); } }
            catch (Exception error) { Logger.Log("Update check: " + error.Message); if (!IsDisposed) { result.Text = "Couldn't check — open GitHub"; result.Links.Add(0, result.Text.Length); } }
            finally { if (!IsDisposed) check.Enabled = true; }
        };
        Controls.Add(check); Controls.Add(result);

        var originalCredits = textBoxContributors.Text.TrimStart();
        textBoxContributors.Text = Ui.Text("MWBP is an unofficial project based on PowerToys Mouse Without Borders. It is not affiliated with or endorsed by Microsoft. Existing connections and settings are kept. PowerToys is not required.") +
            "\r\n\r\n" + originalCredits;
        foreach (string name in new[] { "MouseWithoutBorders.License", "MouseWithoutBorders.UpstreamNotices", "MouseWithoutBorders.ThirdPartyNotices" })
        {
            using var stream = typeof(FrmAbout).Assembly.GetManifestResourceStream(name);
            using var reader = new StreamReader(stream);
            textBoxContributors.AppendText("\r\n\r\n" + reader.ReadToEnd());
        }
    }
}

#endif
