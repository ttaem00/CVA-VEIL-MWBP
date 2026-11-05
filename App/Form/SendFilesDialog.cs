// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal static class SendFilesDialog
{
    internal static void Show(IWin32Window owner)
    {
        var peers = MachineStuff.MachinePool.ListAllMachines()
            .Where(peer => peer.Id != Common.MachineID && peer.Id is not (ID.NONE or ID.ALL) && Common.IsConnectedTo(peer.Id))
            .Select(peer => peer.Name.Trim()).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (peers.Length == 0)
        {
            MessageBox.Show(owner, Ui.Text("No PCs are connected. Use Find PC to connect, then try again."),
                Ui.Text("Send files"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = Create(peers);
        dialog.ShowDialog(owner);
    }

    internal static System.Windows.Forms.Form Create(string[] peers)
    {
        var dialog = new System.Windows.Forms.Form
        {
            Text = Ui.Text("Send files"), ClientSize = new Size(420, 178), Font = SystemFonts.MessageBoxFont,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
        };
        StandaloneBranding.Apply(dialog);
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(24, 20, 24, 8) };
        var label = new Label { Text = Ui.Text("Send to PC"), AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        var target = new ComboBox { Name = "sendFileTarget", DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 372, Margin = new Padding(0), AccessibleName = label.Text, TabIndex = 0 };
        target.Items.AddRange(peers); target.SelectedIndex = 0;
        content.Controls.AddRange([label, target]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 66, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(24, 10, 24, 20) };
        var choose = new Button { Name = "chooseFilesToSend", Text = Ui.Text("Choose files…"), Size = new Size(140, 36),
            Margin = new Padding(12, 0, 0, 0), BackColor = SystemColors.Highlight, ForeColor = SystemColors.HighlightText,
            FlatStyle = FlatStyle.Flat, TabIndex = 1 };
        choose.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = Ui.Text("Cancel"), DialogResult = DialogResult.Cancel,
            Size = new Size(96, 36), Margin = new Padding(0), TabIndex = 2 };
        choose.Click += (_, _) =>
        {
            string peer = (string)target.SelectedItem;
            int offer = 0;
            try
            {
                using var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true,
                    Title = Ui.Format("Send files to {0}", peer) };
                if (picker.ShowDialog(dialog) != DialogResult.OK) return;
                offer = QueuedFileTransfer.Offer(picker.FileNames);
                if (!QueuedFileTransfer.SendToPeer(offer, MachineStuff.MachinePool.ResolveID(peer), peer))
                    throw new IOException(Ui.Text("The PC disconnected. Connect again, then resend the files."));
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            catch (Exception error)
            {
                QueuedFileTransfer.RevokeOffer(offer);
                MessageBox.Show(dialog, Ui.Text(error.Message), Ui.Text("Send files"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        actions.Controls.AddRange([choose, cancel]);
        dialog.Controls.AddRange([content, actions]);
        dialog.AcceptButton = choose; dialog.CancelButton = cancel; dialog.ActiveControl = target;
        return dialog;
    }
}
