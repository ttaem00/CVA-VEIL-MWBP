// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmMatrix
{
    private void AddDiscoveryButton()
    {
        var find = new Button { Name = "findComputers", Text = Ui.Text("Find PCs…"), AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Location = new Point(12, Font.Height + 5), MinimumSize = new Size(120, 34),
            Font = new Font(Font, FontStyle.Bold), AccessibleName = Ui.Text("Find PCs…") };
        find.Click += (_, _) => ShowNearbyComputers();
        find.AccessibleDescription = Ui.Text("Choose a PC, compare the codes, then approve on both PCs.");
        toolTip.SetToolTip(find, find.AccessibleDescription);
        groupBoxKeySetup.Controls.Add(find);
        var send = SettingsButton(Ui.Text("Send files…"), (_, _) => SendFilesDialog.Show(this));
        send.Name = "sendFiles";
        send.Location = new Point(find.Right + 8, find.Top);
        send.MinimumSize = find.MinimumSize;
        send.Font = find.Font;
        groupBoxKeySetup.Controls.Add(send);
        int extraHeight = Math.Max(0, find.Bottom + 8 - groupBoxKeySetup.Height);
        groupBoxKeySetup.Height += extraHeight; Height += extraHeight;
    }

    private void ShowNearbyComputers()
    {
        using var dialog = new System.Windows.Forms.Form { Name = "nearbyComputers", Text = Ui.Text("Find PCs…"), StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(560, 400), MinimumSize = new Size(480, 360), MinimizeBox = false, MaximizeBox = false, Font = Font,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96), Padding = new Padding(16) };
        StandaloneBranding.Apply(dialog);
        var computer = new Label { Text = Ui.Text("This computer: ") + Common.MachineName, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        var guide = Explanation(Ui.Text("Approve on both PCs only if the codes match."));
        guide.Dock = DockStyle.Top; guide.Height = Font.Height + 6; guide.AutoSize = false;
        var discoveryStatus = new Label { Name = "discoveryStatus", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        var list = new ListBox { Name = "nearbyComputerList", Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = Ui.Text("Nearby computers") };
        var address = new TextBox { Name = "manualPeerAddress", Dock = DockStyle.Fill, AccessibleName = Ui.Text("Other PC name or IPv4 address"),
            PlaceholderText = "DESKTOP-PC / 192.168.1.20" };
        var entry = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 10, 0, 6) };
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        entry.Controls.Add(new Label { Text = Ui.Text("Other PC name or IPv4 address"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 12, 3) }, 0, 0);
        entry.Controls.Add(address, 1, 0);
        var feedback = new Label { Name = "connectionFeedback", Dock = DockStyle.Bottom, Height = Font.Height * 3,
            AccessibleRole = AccessibleRole.Alert, AutoEllipsis = true, Padding = new Padding(0, 4, 0, 4) };
        var connect = SettingsButton(Ui.Text("Connect"), null); connect.Name = "connectComputer";
        connect.Font = new Font(Font, FontStyle.Bold); connect.FlatStyle = FlatStyle.Flat;
        connect.BackColor = SystemColors.Highlight; connect.ForeColor = SystemColors.HighlightText; connect.FlatAppearance.BorderSize = 0;
        var network = SettingsButton(Ui.Text("Allow network access…"), null);
        SetOptionTip(network, null, "Can't see the PC? Allow network access on both PCs, then search or enter its name or IPv4 address.");
        var close = SettingsButton(Ui.Text("Close"), (_, _) => dialog.Close()); close.DialogResult = DialogResult.Cancel;
        var actions = Actions(connect, network, close); actions.Dock = DockStyle.Bottom;
        dialog.Controls.Add(list); dialog.Controls.Add(discoveryStatus); dialog.Controls.Add(guide);
        dialog.Controls.Add(computer);
        dialog.Controls.Add(entry); dialog.Controls.Add(feedback); dialog.Controls.Add(actions);
        dialog.AcceptButton = connect; dialog.CancelButton = close;
        using var cancellation = new CancellationTokenSource();
        dialog.FormClosing += (_, _) => cancellation.Cancel();
        bool connecting = false;
        using var refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        void UpdateConnectButton() => connect.Enabled = !connecting && (address.Text.Trim().Length != 0 || list.SelectedItem != null);
        void Feedback(string text) { feedback.Text = text; feedback.AccessibleDescription = text; toolTip.SetToolTip(feedback, text); }
        void UpdatePeers()
        {
            var selected = (list.SelectedItem as LanDiscovery.Peer)?.Name;
            var found = Program.Discovery?.Snapshot() ?? Array.Empty<LanDiscovery.Peer>();
            discoveryStatus.Text = Ui.Text(Program.Discovery == null ? "Automatic discovery is unavailable. Enter the PC below."
                : found.Length == 0 ? "Searching… Run MWBP on the other PC, or enter it below." : "Nearby PCs");
            list.BeginUpdate();
            list.Items.Clear(); list.Items.AddRange(found);
            list.SelectedIndex = Array.FindIndex(found, peer => peer.Name == selected);
            if (list.SelectedIndex < 0 && found.Length > 0) list.SelectedIndex = 0;
            list.EndUpdate(); UpdateConnectButton();
        }
        async void Connect()
        {
            if (connecting) return;
            string target = address.Text.Trim();
            var peer = target.Length == 0 ? list.SelectedItem as LanDiscovery.Peer : null;
            if (peer != null)
            {
                if (Common.IsConnectedTo(MachineStuff.IdFromName(peer.Name))) { Feedback(Ui.Text("This PC is already connected.")); return; }
                target = peer.Address.ToString();
            }
            if (target.Length == 0) { address.Focus(); return; }
            connecting = true; connect.Enabled = network.Enabled = list.Enabled = address.Enabled = false;
            Feedback(Ui.Format("Connecting to {0}… Compare the codes on both PCs.", peer?.Name ?? target));
            var token = cancellation.Token;
            try
            {
                if (Program.Pairing == null) throw new InvalidOperationException(Ui.Text("Connection approval is unavailable. Close other MWBP copies and restart this app."));
                var addresses = await Dns.GetHostAddressesAsync(target, AddressFamily.InterNetwork, token);
                var destination = addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
                await Program.Pairing.ConnectAsync(destination, token);
                if (!dialog.IsDisposed) { RefreshApprovedGroup(); dialog.Close(); }
            }
            catch (OperationCanceledException) { if (!dialog.IsDisposed) Feedback(Ui.Text("Connection approval was cancelled or timed out. You can try again.")); }
            catch (Exception error) { if (!dialog.IsDisposed) Feedback(Ui.Format("Could not connect: {0}", Ui.Text(error.Message))); }
            finally
            {
                connecting = false;
                if (!dialog.IsDisposed) { network.Enabled = list.Enabled = address.Enabled = true; UpdateConnectButton(); }
            }
        }
        async void AllowNetwork()
        {
            network.Enabled = false;
            try { await PortableApplication.PrepareNetworkAsync(); if (!dialog.IsDisposed) Feedback(Ui.Text("Network access allowed. Allow it on the other PC too, then try connecting.")); }
            catch (Exception error) { if (!dialog.IsDisposed) Feedback(Ui.Format("Could not allow network access: {0}", Ui.Text(error.Message))); }
            finally { if (!dialog.IsDisposed) network.Enabled = true; }
        }
        connect.Click += (_, _) => Connect(); network.Click += (_, _) => AllowNetwork();
        address.TextChanged += (_, _) => UpdateConnectButton();
        list.SelectedIndexChanged += (_, _) => UpdateConnectButton();
        list.Enter += (_, _) => address.Clear(); list.Click += (_, _) => address.Clear();
        list.DoubleClick += (_, _) => Connect();
        refresh.Tick += (_, _) => UpdatePeers();
        dialog.Shown += (_, _) => { if (list.Items.Count == 0) address.Focus(); else list.Focus(); };
        UpdatePeers(); refresh.Start(); dialog.ShowDialog(this);
    }

}
