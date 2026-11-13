// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmMatrix
{
    private Label receivingFolderPath;
    private CheckBox automaticTransferClose;
    private CheckBox limitAutomaticClipboardFiles;
    private CheckBox blockFullscreenSwitching;
    private HelpProvider optionHelp;

    private Button InputRecoveryButton(string name)
    {
        var button = SettingsButton(Ui.Text("Restore mouse input"), (_, _) =>
        {
            InitAndCleanup.ReopenSocketDueToReadError = true;
            InitAndCleanup.PleaseReopenSocket = InitAndCleanup.REOPEN_WHEN_HOTKEY;
            Logger.Log("Input recovery requested from Settings.");
            Common.ShowToolTip(Ui.Text("Input reconnection requested. Sharing pauses briefly."), 4000);
        });
        button.Name = name;
        SetOptionTip(button, null, "Use this on the PC whose mouse cannot leave the screen. It restarts input hooks and connections on this PC. Sharing pauses briefly; pairing, screen layout, activation keys and cursor settings are kept. It does not turn off fullscreen blocking.");
        return button;
    }
    private CheckBox installationStartup;
    private bool refreshingInstallation;
    private bool refreshingTransferPreference;
    private Button keyChordButton;
    private readonly Dictionary<KeyboardAction, Button> shortcutButtons = new();
    private string ChordButtonText() => Setting.Values.EasyMouseActivationKeys.Length == 0 ? Ui.Text("Set keys…")
        : HeldKeyChord.Format(Setting.Values.EasyMouseActivationKeys) + " · " + Ui.Text("Change…");

    private static string ShortcutTitle(KeyboardAction action) => action switch
    {
        KeyboardAction.Computer1 => "Switch to PC 1",
        KeyboardAction.Computer2 => "Switch to PC 2",
        KeyboardAction.Computer3 => "Switch to PC 3",
        KeyboardAction.Computer4 => "Switch to PC 4",
        KeyboardAction.NextComputer => "Next PC",
        KeyboardAction.AllComputers => "All PCs",
        KeyboardAction.LockComputers => "Lock PCs",
        KeyboardAction.Reconnect => "Reconnect",
        KeyboardAction.ToggleEdgeSwitching => "Toggle edge switching",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private void RefreshShortcutButtons()
    {
        foreach (var (action, button) in shortcutButtons)
        {
            int[] keys = Setting.Values.GetShortcutKeys(action);
            string title = Ui.Text(ShortcutTitle(action));
            if (action <= KeyboardAction.Computer4)
            {
                string peer = Setting.Values.MachineMatrixString.Split(',').ElementAtOrDefault((int)action);
                if (!string.IsNullOrWhiteSpace(peer)) title += " · " + peer;
            }
            ((Label)button.Tag).Text = title;
            button.Text = (keys.Length == 0 ? Ui.Text("None") : HeldKeyChord.Format(keys)) + " · " + Ui.Text("Change…");
            button.AccessibleName = title + " · " + button.Text;
            toolTip.SetToolTip(button, button.AccessibleName + Environment.NewLine + button.AccessibleDescription);
            button.Enabled = checkBoxEnableKeyboardShortcuts.Checked;
        }
    }

    private int[] RecordKeys(string title, int[] initial)
    {
        var context = new WindowsFormsSynchronizationContext();
        using var dialog = new System.Windows.Forms.Form
        {
            Text = Ui.Text(title), ClientSize = new Size(480, 168),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font,
        };
        StandaloneBranding.Apply(dialog);
        var prompt = new Label { Text = Ui.Text("Press a key or hold a combination, then release all keys and choose Apply. Click the field to record again."),
            Bounds = new Rectangle(16, 16, 448, 48) };
        int[] captured = initial;
        var input = new TextBox { Text = captured.Length == 0 ? Ui.Text("None") : HeldKeyChord.Format(captured),
            AccessibleName = Ui.Text("Recorded keys"), Bounds = new Rectangle(16, 72, 448, 24), ReadOnly = true, ImeMode = ImeMode.Disable };
        var save = new Button { Text = Ui.Text("Apply"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = Ui.Text("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var clear = new Button { Text = Ui.Text("Clear"), AutoSize = true };
        var pressed = new HashSet<int>();
        clear.Click += (_, _) => { pressed.Clear(); captured = []; input.Text = Ui.Text("None"); save.Focus(); };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([cancel, save, clear]);
        dialog.Controls.AddRange([prompt, input, actions]);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        void Record(int code, bool down)
        {
            if (down)
            {
                pressed.Add(code);
                captured = pressed.ToArray();
                input.Text = HeldKeyChord.Format(captured);
            }
            else if (pressed.Remove(code) && pressed.Count == 0) save.Focus();
        }
        input.GotFocus += (_, _) =>
        {
            pressed.Clear();
            InputHook.KeyRecorder = (code, down) => context.Post(_ => { if (!dialog.IsDisposed) Record(code, down); }, null);
        };
        input.LostFocus += (_, _) => InputHook.KeyRecorder = null;
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        InputHook.RecordingKeys = true;
        try { return dialog.ShowDialog(this) == DialogResult.OK ? captured : null; }
        finally { InputHook.KeyRecorder = null; InputHook.RecordingKeys = false; }
    }

    // Keep options directly in each section. Nested, autosizing tables for every
    // option multiply preferred-size passes when a hidden tab becomes visible.
    internal sealed class SettingsStack : Panel
    {
        private bool updatingWidths, arranging;
        private int constrainedWidth = -1;
        private readonly bool sideBySide;
        internal SettingsStack(bool sideBySide = false)
        {
            this.sideBySide = sideBySide;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top; Margin = new Padding(0); Padding = new Padding(4);
        }
        internal void Add(Control control)
        {
            control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = new Padding(4, 2, 4, 2);
            control.TabIndex = Controls.Count;
            Controls.Add(control);
        }
        private int ColumnWidth(int width) => Math.Max(1, (width - Padding.Horizontal) /
            (sideBySide ? Math.Max(1, Controls.Count) : 1));
        private int ChildWidth(Control child, int width) => Math.Max(40, ColumnWidth(width) - child.Margin.Horizontal);
        private int Measure(Control child, int width) => child.AutoSize ? child.GetPreferredSize(
            new Size(ChildWidth(child, width), 0)).Height : child.Height;
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Width;
            int height = 0;
            foreach (Control child in Controls)
            {
                int rowHeight = Measure(child, width) + child.Margin.Vertical;
                height = sideBySide ? Math.Max(height, rowHeight) : height + rowHeight;
            }
            return new Size(width, height + Padding.Vertical);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            if (arranging) return;
            arranging = true;
            try
            {
                int y = Padding.Top, x = Padding.Left, bottom = Padding.Top;
                foreach (Control child in Controls)
                {
                    // Resolve wrapping at the new width before measuring height.
                    // SetBounds can relayout a nested section; using its old height
                    // afterwards leaves empty rows and an inflated scroll range.
                    child.Width = ChildWidth(child, ClientSize.Width);
                    int height = Measure(child, ClientSize.Width);
                    child.SetBounds(x + child.Margin.Left, y + child.Margin.Top,
                        ChildWidth(child, ClientSize.Width), height);
                    bottom = Math.Max(bottom, child.Bottom + child.Margin.Bottom);
                    if (sideBySide) x += ColumnWidth(ClientSize.Width);
                    else y = bottom;
                }
                int needed = bottom + Padding.Bottom;
                if (Height != needed) Height = needed;
            }
            finally { arranging = false; }
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateChildWidths();
        }
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            UpdateChildWidths(force: true);
        }
        private void UpdateChildWidths(bool force = false)
        {
            // Changing height alone must not invalidate every child's measurement.
            if (updatingWidths || (!force && constrainedWidth == ClientSize.Width)) return;
            updatingWidths = true; constrainedWidth = ClientSize.Width;
            SuspendLayout();
            try
            {
                foreach (Control child in Controls)
                    if (child is Label or CheckBox or FlowLayoutPanel)
                    {
                        var maximum = new Size(ChildWidth(child, ClientSize.Width), 0);
                        if (child.MaximumSize != maximum) child.MaximumSize = maximum;
                    }
            }
            finally { ResumeLayout(true); updatingWidths = false; }
        }
    }

    private static Label Explanation(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.ControlText, UseMnemonic = false };
    private SettingsStack Section(string title)
    {
        var stack = new SettingsStack();
        var heading = new Label { Text = title, AutoSize = true, ForeColor = SystemColors.ControlText, UseMnemonic = false,
            Font = new Font(Font, FontStyle.Bold) };
        stack.Add(heading); heading.Margin = new Padding(4, 8, 4, 7);
        return stack;
    }
    private void SetOptionTip(Control option, string defaults, string explanation)
    {
        string text = Ui.Text(explanation ?? toolTip.GetToolTip(option));
        if (defaults != null) text = (string.IsNullOrWhiteSpace(text) ? "" : text + "\n\n") + Ui.Format("Default: {0}.", Ui.Text(defaults));
        toolTip.SetToolTip(option, text);
        option.AccessibleDescription = text;
        if (optionHelp == null) { optionHelp = new HelpProvider(); components.Add(optionHelp); }
        optionHelp.SetHelpString(option, text);
    }
    private LinkLabel SettingsHelpLink(string text, string help)
    {
        var link = new LinkLabel { Text = Ui.Text(text), AutoSize = true, Margin = new Padding(4), TabStop = true };
        SetOptionTip(link, null, help);
        link.LinkClicked += (_, _) => MessageBox.Show(this, Ui.Text(help), Ui.Text(text), MessageBoxButtons.OK, MessageBoxIcon.Information);
        return link;
    }
    private void AddOption(SettingsStack parent, CheckBox option, string defaults, string explanation = null)
    {
        option.AutoSize = true; option.ResetFont();
        SetOptionTip(option, defaults, explanation);
        parent.Add(option);
    }
    private static Button SettingsButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4) };
        button.Click += click; return button;
    }
    private static FlowLayoutPanel Actions(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
        foreach (var control in controls)
        {
            control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = new Padding(3, 1, 8, 1); control.TabIndex = flow.Controls.Count; flow.Controls.Add(control);
        }
        return flow;
    }
    private void HostStack(TabPage tab, Control content)
    {
        tab.AutoScroll = true; tab.Padding = new Padding(10); tab.BackColor = SystemColors.Window;
        tab.Controls.Add(content); content.Dock = DockStyle.Top;
        RegisterDisabledOptionTips(content);
    }

    private Control disabledOptionTipTarget;
    private ToolTip disabledOptionTip;
    private void RegisterDisabledOptionTips(Control host)
    {
        // Disabled WinForms controls cannot receive hover events. Their enabled
        // parent provides the same help, so dependencies need no extra text row.
        host.MouseMove += (_, e) =>
        {
            var child = host.GetChildAtPoint(e.Location, GetChildAtPointSkip.Invisible);
            if (child?.Enabled != false || string.IsNullOrEmpty(toolTip.GetToolTip(child)))
            {
                if (disabledOptionTipTarget != null) disabledOptionTip?.Hide(disabledOptionTipTarget.Parent);
                disabledOptionTipTarget = null;
                return;
            }
            if (child == disabledOptionTipTarget) return;
            disabledOptionTip ??= new ToolTip(components) { ShowAlways = true };
            if (disabledOptionTipTarget != null) disabledOptionTip.Hide(disabledOptionTipTarget.Parent);
            disabledOptionTipTarget = child;
            disabledOptionTip.Show(toolTip.GetToolTip(child), host, child.Left, child.Bottom + 4, 10000);
        };
        host.MouseLeave += (_, _) =>
        {
            if (disabledOptionTipTarget?.Parent != host) return;
            disabledOptionTip?.Hide(host);
            disabledOptionTipTarget = null;
        };
        foreach (Control child in host.Controls)
            if (child.HasChildren) RegisterDisabledOptionTips(child);
    }

    private void LayoutPortableSettingsPages()
    {
        SuspendLayout();
        try
        {
            var mouse = Section("Mouse and screen switching");
            AddOption(mouse, checkBoxMouseEdgeSwitching, "On");
            AddOption(mouse, checkBoxMouseMoveRelatively, "Off", "When controlling one PC, send movement distances from the first move. Turn on before controlling a 3D game. This setting is shared with connected PCs.");
            labelEasyMouse.Text = "Activation:"; labelEasyMouse.AutoSize = true;
            comboBoxEasyMouseOption.ResetFont();
            SetOptionTip(comboBoxEasyMouseOption, "Always", "Switch at a screen edge immediately, or only while holding Ctrl or Shift.");
            toolTip.SetToolTip(labelEasyMouse, toolTip.GetToolTip(comboBoxEasyMouseOption));
            var editChord = SettingsButton(ChordButtonText(), (_, _) =>
            {
                int[] keys = RecordKeys("Screen-edge activation keys", Setting.Values.EasyMouseActivationKeys);
                if (keys == null) return;
                Setting.Values.EasyMouseActivationKeys = keys;
                keyChordButton.Text = ChordButtonText();
                ShowUpdateMessage();
            });
            keyChordButton = editChord;
            editChord.Visible = comboBoxEasyMouseOption.SelectedItem is EasyMouseOption.Custom;
            comboBoxEasyMouseOption.SelectedIndexChanged += (_, _) => editChord.Visible = comboBoxEasyMouseOption.SelectedItem is EasyMouseOption.Custom;
            mouse.Add(Actions(labelEasyMouse, comboBoxEasyMouseOption, editChord));
            blockFullscreenSwitching = new CheckBox { Name = "blockFullscreenSwitching", Text = "Block screen switching in fullscreen apps",
                Checked = Setting.Values.DisableEasyMouseWhenForegroundWindowIsFullscreen };
            AddOption(mouse, blockFullscreenSwitching, "On", "Blocks leaving this PC while the selected window fills a screen, even when activation keys are held. Turn off to switch from a fullscreen game using your activation keys. This setting is shared with connected PCs.");
            blockFullscreenSwitching.CheckedChanged += (_, _) =>
            {
                if (reloadingSharedSettings) return;
                Setting.Values.DisableEasyMouseWhenForegroundWindowIsFullscreen = blockFullscreenSwitching.Checked;
                ShowUpdateMessage();
            };
            AddOption(mouse, checkBoxHideMouse, "On", "Hide the pointer on this PC while controlling another.");
            checkBoxDrawMouse.Text = "Use a replacement cursor on this PC";
            AddOption(mouse, checkBoxDrawMouse, "On", "Only needed when Windows cannot show a cursor. Keep this off when the Windows cursor works. This choice is saved on this PC only.");
            var cursorRecovery = SettingsButton("Use Windows cursor", (_, _) =>
            {
                checkBoxDrawMouse.Checked = false;
                Common.ShowToolTip(Ui.Text("Applied: replacement cursor off. Windows input settings unchanged."), 4000);
            });
            cursorRecovery.Name = "nativeCursorRecovery";
            SetOptionTip(cursorRecovery, null, "Turns off this PC's replacement cursor. Windows Mouse Keys, numeric keypad settings and PC switching stay unchanged.");
            var inputRecovery = InputRecoveryButton("inputRecovery");
            mouse.Add(Actions(inputRecovery, cursorRecovery));
            AddOption(mouse, checkBoxBlockScreenSaver, "On", "Keep other PCs' screen savers from starting while you are actively using MWB.");

            var sharing = Section("Clipboard and file transfers");
            AddOption(sharing, checkBoxShareClipboard, "On", "Share copied text and images between PCs.");
            AddOption(sharing, checkBoxTransferFile, "On");
            UpdatePortableTransferFileText();
            limitAutomaticClipboardFiles = new CheckBox { Name = "limitAutomaticClipboardFiles", Text = "300 MB file clipboard limit", Checked = Setting.Values.LimitAutomaticClipboardFileSize };
            AddOption(sharing, limitAutomaticClipboardFiles, "On", "Only applies to copied files. Use Send files or drag and drop to send larger files.");
            limitAutomaticClipboardFiles.CheckedChanged += (_, _) =>
            {
                if (reloadingSharedSettings) return;
                Setting.Values.LimitAutomaticClipboardFileSize = limitAutomaticClipboardFiles.Checked;
                MachineStuff.SendMachineMatrix();
            };
            var receivingLabel = new Label { Text = "Receiving folder on this PC", AutoSize = true };
            sharing.Add(receivingLabel);
            receivingFolderPath = Explanation(ReceivingFolderText());
            receivingFolderPath.Name = "receivingFolderPath";
            sharing.Add(receivingFolderPath);
            const string receivingHelp = "Used for new drag/drop transfers when you aren't dropping into an Explorer folder. Existing transfers keep their destination.\n\nDefault: Desktop \\ MouseWithoutBorders.";
            SetOptionTip(receivingLabel, null, receivingHelp);
            SetOptionTip(receivingFolderPath, null, receivingHelp);
            var chooseFolder = SettingsButton("Choose folder…", ChooseReceivingFolder);
            var defaultFolder = SettingsButton("Use default", ResetReceivingFolder);
            SetOptionTip(chooseFolder, null, receivingHelp); SetOptionTip(defaultFolder, null, receivingHelp);
            sharing.Add(Actions(chooseFolder, defaultFolder));
            automaticTransferClose = new CheckBox { Name = "automaticTransferClose", Text = "Automatically close finished transfers", Checked = Setting.Values.AutoCloseTransferWindow };
            AddOption(sharing, automaticTransferClose, "On", "Close this PC's completed or cancelled list after cleanup. Errors stay visible.");
            automaticTransferClose.CheckedChanged += (_, _) =>
            {
                if (refreshingTransferPreference) return;
                try { Setting.Values.SetAutoCloseTransferWindow(automaticTransferClose.Checked); }
                catch (Exception error)
                {
                    refreshingTransferPreference = true;
                    try { automaticTransferClose.Checked = Setting.Values.AutoCloseTransferWindow; }
                    finally { refreshingTransferPreference = false; }
                    SettingsError(error);
                }
            };

            var columns = new SettingsStack(sideBySide: true) { Padding = new Padding(0) };
            columns.Add(mouse); columns.Add(sharing);

            var shortcuts = new SettingsStack();
            AddOption(shortcuts, checkBoxEnableKeyboardShortcuts, "Off", "Assignments below are preserved while shortcuts are disabled.");
            checkBoxEnableKeyboardShortcuts.Font = new Font(Font, FontStyle.Bold);
            var emergencyHelp = Explanation(Ui.Text("Saved automatically. Emergency local control: Ctrl+Alt+Backspace (always available)."));
            emergencyHelp.Name = "emergencyInputHelp";
            shortcuts.Add(emergencyHelp);
            var shortcutColumns = new SettingsStack(sideBySide: true);
            var shortcutGroups = new[] { new TableLayoutPanel(), new TableLayoutPanel() };
            foreach (var group in shortcutGroups)
            {
                group.AutoSize = true; group.AutoSizeMode = AutoSizeMode.GrowAndShrink; group.ColumnCount = 2;
                group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
                shortcutColumns.Add(group);
            }
            foreach (KeyboardAction action in Enum.GetValues<KeyboardAction>())
            {
                var button = SettingsButton("", (_, _) =>
                {
                    int[] keys = RecordKeys(ShortcutTitle(action), Setting.Values.GetShortcutKeys(action));
                    if (keys == null) return;
                    foreach (var other in Enum.GetValues<KeyboardAction>())
                        if (other != action && keys.Length != 0 && keys.ToHashSet().SetEquals(Setting.Values.GetShortcutKeys(other)))
                        {
                            MessageBox.Show(this, Ui.Format("This shortcut is already assigned to {0}.", Ui.Text(ShortcutTitle(other))),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                    Setting.Values.SetShortcutKeys(action, keys);
                    RefreshShortcutButtons();
                    MachineStuff.SendMachineMatrix();
                });
                button.Name = "shortcut" + action;
                button.UseMnemonic = false; button.Dock = DockStyle.Fill;
                button.Font = Font;
                button.Text = Ui.Text("Change…");
                int lineHeight = button.GetPreferredSize(Size.Empty).Height;
                button.AutoSize = false; button.AutoEllipsis = true; button.Height = lineHeight;
                button.Margin = new Padding(4, 3, 4, 3);
                var actionLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false, Margin = new Padding(4, 3, 12, 3) };
                button.Tag = actionLabel;
                SetOptionTip(button, null, action == KeyboardAction.ToggleEdgeSwitching
                    ? "Record one key or a combination. This toggles Always and Never; hold-to-switch modes are preserved."
                    : "Record one key or a combination. Clear removes only this shortcut.");
                shortcutButtons.Add(action, button);
                var shortcutRows = shortcutGroups[action <= KeyboardAction.NextComputer ? 0 : 1];
                int row = shortcutRows.RowCount++;
                shortcutRows.Controls.Add(actionLabel, 0, row); shortcutRows.Controls.Add(button, 1, row);
            }
            shortcuts.Add(shortcutColumns);
            groupBoxOtherOptions.Visible = groupBoxShortcuts.Visible = false;
            var content = new SettingsStack();
            content.Add(columns);
            content.Add(new Panel { Name = "keyboardShortcutDivider", Height = 2, BackColor = SystemColors.ControlDark });
            content.Add(shortcuts);
            HostStack(tabPageOther, content);

            var network = Section("Connection troubleshooting");
            checkBoxReverseLookup.Text = Ui.Text("Check computer names against DNS") + (Setting.Values.ReverseLookupIsGpoConfigured ? Ui.Text(" [Managed]") : "");
            AddOption(network, checkBoxReverseLookup, "Off", "Ask the network's name service (DNS) to check that an IP address matches the expected PC name. Use this to troubleshoot name/address mismatches. Some home networks have no matching DNS records, so enabling this check can prevent a connection.");
            network.Add(new Label { Text = "Machine name to IP address mappings", AutoSize = true });
            const string mappingHelp = "MWB normally finds your other PCs automatically. If they already connect, you can leave this box empty.\n\nIf MWB cannot find a PC, enter its exact name from Machine Setup, a space, and its local IP address. Add one PC per line.\nExample: OFFICE-PC 192.168.1.20\n\nAn IP address is a PC's address on your network. Find it in that PC's Windows network settings (IPv4 address). If it changes, update the entry here. A router setting called a DHCP reservation can keep that address from changing.";
            var mappingDetails = SettingsHelpLink("How to enter an IP mapping…", mappingHelp);
            mappingDetails.Name = "ipMappingHelp";
            network.Add(Actions(Explanation(Ui.Text("If discovery fails, add mappings and choose Apply on PC connections.")), mappingDetails));
            SetOptionTip(textBoxMachineName2IP, null, mappingHelp);
            textBoxMachineName2IP.PlaceholderText = "OFFICE-PC 192.168.1.20";
            // A multiline TextBox can still report a one-line preferred height.
            // Keep room for entries even when the editor is empty.
            textBoxMachineName2IP.AutoSize = false;
            textBoxMachineName2IP.ResetFont();
            textBoxMachineName2IP.MinimumSize = new Size(0, Math.Max(150, Font.Height * 8));
            textBoxMachineName2IP.Height = textBoxMachineName2IP.MinimumSize.Height;
            network.Add(textBoxMachineName2IP);
            if (Setting.Values.Name2IpPolicyListIsGpoConfigured)
            {
                network.Add(Explanation("These mappings are set by your Windows administrator and cannot be edited here."));
                textBoxMachineName2IPPolicyList.AutoSize = false;
                textBoxMachineName2IPPolicyList.MinimumSize = new Size(0, Math.Max(120, Font.Height * 6));
                textBoxMachineName2IPPolicyList.Height = textBoxMachineName2IPPolicyList.MinimumSize.Height;
                network.Add(textBoxMachineName2IPPolicyList);
            }
            groupBoxDNS.Visible = groupBoxName2IPPolicyList.Visible = pictureBoxMouseWithoutBorders.Visible = textBoxDNS.Visible = false;
            HostStack(tabPageAdvancedSettings, network);
            checkBoxTwoRow.Text = "Two rows";
            toolTip.SetToolTip(checkBoxTwoRow, "Arrange the computers in two rows to match screens above and below each other.");
            UpdatePortableShortcutControlState();
        }
        finally { ResumeLayout(true); }
    }

    private string ReceivingFolderText() => string.IsNullOrEmpty(Setting.Values.DefaultReceivingFolder)
        ? TransferReceivePreferences.DesktopFolder : Setting.Values.DefaultReceivingFolder;
    private void ChooseReceivingFolder(object sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = Ui.Text("Choose the default receiving folder on this PC"), UseDescriptionForTitle = true, SelectedPath = ReceivingFolderText() };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string folder = TransferReceivePreferences.NormalizeFolder(dialog.SelectedPath);
            TransferReceivePreferences.ValidateWritable(folder);
            Setting.Values.SetDefaultReceivingFolder(folder); receivingFolderPath.Text = ReceivingFolderText();
        }
        catch (Exception error) { SettingsError(error); }
    }
    private void ResetReceivingFolder(object sender, EventArgs e)
    {
        try { Setting.Values.SetDefaultReceivingFolder(""); receivingFolderPath.Text = ReceivingFolderText(); }
        catch (Exception error) { SettingsError(error); }
    }
    private void SettingsError(Exception error)
    {
        Logger.Log("Settings: " + error.Message);
        if (IsDisposed || Disposing) return;
        MessageBox.Show(this, error.Message, Ui.Text("Could not change setting"), MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void AddPortableSettingsTab()
    {
        var tab = new TabPage { Text = "MWBP", Name = "installationTab", BackColor = SystemColors.Window };
        var content = Section("MWBP on this PC");
        var administratorStatus = Explanation(""); administratorStatus.Name = "administratorControlStatus";
        content.Add(administratorStatus);
        const string permission = "Allow this Windows account and paired PCs to control administrator apps and Windows approval screens. Other programs running in this account could misuse MWBP to operate approval screens. You can block approval, sign-in and lock screens separately after installing. Windows administrator approval is required. Your connections and layout are kept.";
        const string allowScreens = "Allow paired PCs to operate Windows approval (UAC), sign-in and lock screens on this PC again? Any program running in this Windows account could then use MWBP to approve administrator prompts without you. Windows administrator approval is required.";
        const string blockScreens = "Block remote control of Windows approval (UAC), sign-in and lock screens on this PC? Administrator apps stay controllable.";
        Button protectedInput = null, disableProtectedInput = null, approvalScreens = null;
        void RefreshAdministratorControl()
        {
            protectedInput.Text = Ui.Text(PortableApplication.IsInstalledCopy ? "Repair installation…" : "Install MWBP…");
            protectedInput.AccessibleName = protectedInput.Text;
            bool serviceAllowed = true;
            string policyStatus = Ui.Text("Windows policy disables administrator control. Normal mouse, keyboard and file sharing remain available.");
            ServiceControllerStatus? state;
            try
            {
                serviceAllowed = PrivilegedInput.ServiceModeAllowed;
                using var service = new ServiceController(PrivilegedInput.ServiceName);
                state = service.Status;
            }
            catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 })
            { state = null; }
            catch (Exception error)
            {
                administratorStatus.Text = serviceAllowed ? Ui.Text("Installation status is unavailable. You can still install or repair MWBP.") : policyStatus;
                toolTip.SetToolTip(administratorStatus, error.Message);
                approvalScreens.Visible = false;
                return;
            }
            administratorStatus.Text = !serviceAllowed ? policyStatus : Ui.Text(!PortableApplication.IsInstalledCopy
                ? "Install MWBP once on this PC. Your connections and layout are kept."
                : state == ServiceControllerStatus.Running
                    ? "MWBP installed · administrator input service running"
                    : "MWBP installed · administrator input service stopped. Choose Repair installation.");
            disableProtectedInput.Visible = state.HasValue;
            toolTip.SetToolTip(administratorStatus, null);
            // Shown only where the service exists: the setting is enforced by that service.
            bool screens = PrivilegedInput.ApprovalScreensAllowed();
            approvalScreens.Visible = serviceAllowed && state.HasValue && PortableApplication.IsInstalledCopy;
            approvalScreens.Tag = screens;
            approvalScreens.Text = Ui.Text(screens ? "Block Windows approval screens…" : "Allow Windows approval screens…");
            approvalScreens.AccessibleName = approvalScreens.Text;
            SetOptionTip(approvalScreens, null, screens ? blockScreens : allowScreens);
            if (approvalScreens.Visible)
                administratorStatus.Text += " · " + Ui.Text(screens ? "Windows approval screens allowed" : "Windows approval screens blocked");
        }
        async void ChangeApprovalScreens(object sender, EventArgs args)
        {
            bool allow = approvalScreens.Tag is not true;
            if (MessageBox.Show(this, Ui.Text(allow ? allowScreens : blockScreens),
                Ui.Text(allow ? "Allow Windows approval screens…" : "Block Windows approval screens…"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            content.Enabled = false;
            try { await PortableApplication.SetApprovalScreensAsync(allow); }
            catch (Exception error) { SettingsError(error); }
            finally { content.Enabled = true; RefreshAdministratorControl(); }
        }
        async void ChangeAdministratorControl(object sender, EventArgs args)
        {
            bool disabling = sender == disableProtectedInput;
            string notice = disabling
                ? "Disable administrator control on this PC? Paired PCs will no longer control administrator apps or Windows approval screens. Normal sharing and your connections are kept."
                : permission;
            if (MessageBox.Show(this, Ui.Text(notice), Ui.Text(disabling ? "Disable administrator control…" : "Install MWBP…"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            content.Enabled = false;
            try
            {
                if (sender == disableProtectedInput)
                {
                    await PortableApplication.DisablePrivilegedInputAsync();
                    RefreshAdministratorControl();
                }
                else if (!await PortableApplication.EnablePrivilegedInputAsync())
                {
                    Hide();
                    Common.MainForm?.Quit(cleanup: true, isFormClosing: false);
                }
            }
            catch (Exception error) { SettingsError(error); }
            finally { content.Enabled = true; }
        }
        protectedInput = SettingsButton("Install MWBP…", ChangeAdministratorControl);
        protectedInput.Name = "setupAdministratorControl";
        SetOptionTip(protectedInput, null, permission);
        disableProtectedInput = SettingsButton("Disable administrator control…", ChangeAdministratorControl);
        disableProtectedInput.Name = "disableAdministratorControl"; disableProtectedInput.Visible = false;
        SetOptionTip(disableProtectedInput, null, "Disable administrator control on this PC? Paired PCs will no longer control administrator apps or Windows approval screens. Normal sharing and your connections are kept.");
        approvalScreens = SettingsButton("Allow Windows approval screens…", ChangeApprovalScreens);
        approvalScreens.Name = "approvalScreens"; approvalScreens.Visible = false;
        content.Add(Actions(protectedInput, disableProtectedInput, approvalScreens));
        content.Add(SettingsHelpLink("About administrator control…", permission));

        var management = Section("Manage this installation");
        if (PortableApplication.IsInstalledCopy)
        {
            installationStartup = new CheckBox { Text = "Start with Windows", Name = "installationStartup" };
            AddOption(management, installationStartup, null, "Start this installed copy when you sign in to Windows.");
            installationStartup.CheckedChanged += (_, _) =>
            {
                if (refreshingInstallation) return;
                try { PortableApplication.SetStartWithWindows(installationStartup.Checked); }
                catch (Exception error) { RefreshInstallationStartup(); SettingsError(error); }
            };
            RefreshInstallationStartup();
            tab.Enter += (_, _) => RefreshInstallationStartup();
            management.Add(SettingsButton("Uninstall…", UninstallInstalledCopy));
        }
        var folders = new List<Control>();
        foreach (var location in new[] { (Title: "Open application folder", Path: PortableApplication.CurrentExecutablePath),
            (Title: "Open settings folder", Path: PortableApplication.CurrentSettingsPath) })
        {
            var open = SettingsButton(location.Title, (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(location.Path)) { UseShellExecute = true }); }
                catch (Exception error) { SettingsError(error); }
            });
            SetOptionTip(open, null, location.Path);
            folders.Add(open);
        }
        management.Add(Actions(folders.ToArray()));
        content.Add(management);
        tab.Enter += (_, _) => RefreshAdministratorControl();
        HostStack(tab, content); tabControlSetting.TabPages.Add(tab);
    }
    private void RefreshInstallationStartup()
    {
        refreshingInstallation = true;
        try { installationStartup.Checked = PortableApplication.IsStartWithWindowsEnabled(); }
        catch (Exception error) { SettingsError(error); }
        finally { refreshingInstallation = false; }
    }
    private void UninstallInstalledCopy(object sender, EventArgs e)
    {
        bool? deletePreferences = PortableApplication.ConfirmUninstall(this);
        if (deletePreferences == null) return;
        try { PortableApplication.BeginUninstall(deletePreferences.Value); Hide(); Common.MainForm?.Quit(cleanup: true, isFormClosing: false); }
        catch (Exception error) { SettingsError(error); }
    }

    private IEnumerable<Control.ControlCollection> PortableDiagnosticControls()
    {
        foreach (var tab in new[] { tabPageOther, tabPageAdvancedSettings, tabControlSetting.TabPages[tabControlSetting.TabPages.Count - 1] })
            foreach (var collection in VisibleCollections(tab)) yield return collection;
    }
    private static IEnumerable<Control.ControlCollection> VisibleCollections(Control parent)
    {
        yield return parent.Controls;
        foreach (Control child in parent.Controls)
            if (child is not GroupBox && child.HasChildren)
                foreach (var collection in VisibleCollections(child)) yield return collection;
    }
}
#endif
