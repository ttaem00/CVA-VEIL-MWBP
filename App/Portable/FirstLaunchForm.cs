// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if PORTABLE_SINGLE_FILE
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace MouseWithoutBorders.Core;

internal sealed class FirstLaunchForm : System.Windows.Forms.Form
{
    private readonly bool installingExistingPreferences;
    private readonly Button install;
    private readonly Button cancel;
    private readonly Label status;
    private bool installing;

    internal FirstLaunchForm(bool installingExistingPreferences = false)
    {
        this.installingExistingPreferences = installingExistingPreferences;
        Text = Ui.Text("Install MWBP");
        StartPosition = installingExistingPreferences ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = !installingExistingPreferences;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(584, 426);
        Font = SystemFonts.MessageBoxFont;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* The setup remains usable without the file icon. */ }

        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(28, 24, 28, 8), TabStop = false };
        var identity = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Margin = new Padding(0, 0, 0, 20), Padding = Padding.Empty, TabStop = false };
        var brandMark = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 16, 0), TabStop = false, AccessibleRole = AccessibleRole.None,
            Image = StandaloneBranding.CreateProductIconBitmap(new Size(128, 128)) };
        var brandName = new Label { Text = "VEIL MWBP", AutoSize = true, UseMnemonic = false,
            Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Margin = new Padding(0, 14, 0, 0) };
        identity.Controls.AddRange([brandMark, brandName]);
        content.Controls.Add(identity);
        Disposed += (_, _) => brandMark.Image?.Dispose();
        Label AddText(string text, int gap = 12, bool bold = false)
        {
            var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(508, 0),
                Margin = new Padding(0, 0, 0, gap), UseMnemonic = false };
            if (bold) label.Font = new Font(Font, FontStyle.Bold);
            content.Controls.Add(label); return label;
        }
        AddText("One mouse. All your PCs.", 12).Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
        AddText("Your connections and settings are kept.", 16);
        AddText(Ui.Text("This computer: ") + Environment.MachineName, 20);
        if (PrivilegedInput.ServiceModeAllowed)
        {
            AddText("Keep control when Windows asks for approval", 8, bold: true);
            AddText("Install a SYSTEM input service so paired PCs can control administrator apps and Windows approval screens. Other apps in this Windows account could misuse this access. You can block approval, sign-in and lock screens later in settings. Windows administrator approval is required.", 0);
        }
        else AddText("Windows policy allows only normal mouse, keyboard and file sharing.", 0);
        status = AddText(""); status.Name = "installStatus"; status.Visible = false;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(28, 12, 28, 24) };
        install = new Button { Name = "installProduct", Text = "Install MWBP", Size = new Size(164, 36),
            Margin = new Padding(12, 0, 0, 0), FlatStyle = FlatStyle.Flat,
            BackColor = SystemColors.Highlight, ForeColor = SystemColors.HighlightText };
        install.FlatAppearance.BorderSize = 0;
        cancel = new Button { Name = "cancelInstallation", Text = "Cancel", DialogResult = DialogResult.Cancel,
            Size = new Size(100, 36), Margin = new Padding(0) };
        install.Click += Install_Click;
        actions.Controls.AddRange([install, cancel]);
        Controls.AddRange([content, actions]);
        AcceptButton = install; CancelButton = cancel; ActiveControl = install;
        Ui.Apply(this);
    }

    private async void Install_Click(object sender, EventArgs e)
    {
        installing = true; install.Enabled = cancel.Enabled = false;
        status.Visible = true; status.ForeColor = SystemColors.ControlText;
        status.Text = Ui.Text("Installing MWBP... Approve the Windows prompt to continue.");
        try
        {
            await PortableApplication.InstallProductAsync(installingExistingPreferences);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            status.Text = Ui.Text("Installation was cancelled. Choose Install to try again, or Cancel to leave.");
            return;
        }
        catch (Exception error)
        {
            status.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.Firebrick;
            status.Text = Ui.Text("Installation did not finish. Try again, or cancel.") + Environment.NewLine + error.Message;
            return;
        }
        finally
        {
            installing = false;
            install.Enabled = cancel.Enabled = true;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The pending installer owns rollback. Windows No cancels that operation;
        // closing this process first would abandon it. System shutdown is not blocked.
        if (installing && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
#endif
