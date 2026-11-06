// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if PORTABLE_SINGLE_FILE

using System;
using System.Drawing;
using System.Windows.Forms;

using Microsoft.PowerToys.Settings.UI.Library;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmMatrix
{
    private CheckBox checkBoxEnableKeyboardShortcuts;
    private CheckBox checkBoxMouseEdgeSwitching;
    private bool portableMachineTilesConfigured;
    private bool layingOutMatrix;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        ConfigurePortableOtherOptions();
        ConfigurePortableShortcutControls();
        LayoutPortableSettingsPages();
        AddPortableSettingsTab();
        InitAll();
        ConfigurePortableMachineTiles();
        AddDiscoveryButton();
        Ui.Apply(this, toolTip);
    }

    private void ConfigurePortableMachineTiles()
    {
        if (portableMachineTilesConfigured)
        {
            return;
        }

        foreach (Machine machine in machines)
        {
            machine.ConfigurePortableArtwork();
        }

        portableMachineTilesConfigured = true;
        groupBoxMachineMatrix.Text = Ui.Text("Monitors — drag edges together");
        AllowDrop = false; // Tile movement is local pointer capture, not an OLE drop target.
        matrixSurface = new MatrixSurface(this);
        groupBoxMachineMatrix.Controls.Add(matrixSurface);
        DoubleBuffered = true;
        groupBoxMachineMatrix.SizeChanged += PortableMachineMatrix_SizeChanged;
        groupBoxMachineMatrix.Anchor = groupBoxKeySetup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        buttonOK.Anchor = buttonCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        MinimumSize = Size;
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true;
        Width = Math.Max(Width, 820 * DeviceDpi / 96);
        checkBoxTwoRow.CheckedChanged -= CheckBoxTwoRow_CheckedChanged;
        checkBoxTwoRow.Visible = false;
        LayoutPortableMachineTiles();
        RefreshMatrixConnections();
    }

    private void PortableMachineMatrix_SizeChanged(object sender, EventArgs e)
    {
        LayoutPortableMachineTiles();
    }

    private void LayoutPortableMachineTiles()
    {
        if (!portableMachineTilesConfigured || machines[0] == null || layingOutMatrix) return;
        layingOutMatrix = true;
        try
        {
            int padding = Math.Max(8, groupBoxMachineMatrix.Font.Height / 2);
            int top = TextRenderer.MeasureText(groupBoxMachineMatrix.Text, groupBoxMachineMatrix.Font).Height + padding;
            int height = groupBoxMachineMatrix.ClientSize.Height - top - padding;
            int rowHeight = Math.Max(groupBoxMachineMatrix.Font.Height, machines[0].NameEditor.PreferredHeight) + 8;
            int minimum = groupBoxMachineMatrix.Font.Height * 18 + machines.Length * rowHeight + 28;
            if (height < minimum)
            {
                Height += minimum - height;
                height = groupBoxMachineMatrix.ClientSize.Height - top - padding;
            }
            foreach (var machine in machines) machine.Visible = false;
            matrixSurface?.Arrange(new Rectangle(padding, top, Math.Max(1, groupBoxMachineMatrix.ClientSize.Width - padding * 2), Math.Max(1, height)));
        }
        finally { layingOutMatrix = false; }
    }

    private void ConfigurePortableOtherOptions()
    {
        // Remove unsupported settings. The settings layout supplies hover help
        // through enabled parents when an option is temporarily disabled.
        checkBoxDisableCAD.Visible = checkBoxHideLogo.Visible = false;
        checkBoxSameSubNet.Visible = checkBoxClipNetStatus.Visible = false;
        int mouseEdgeSwitchingTop = checkBoxClipNetStatus.Top;
        int activationTop = checkBoxSendLog.Top;

        checkBoxVKMap.Visible = false;
        checkBoxClipNetStatus.Top = checkBoxVKMap.Top;

        checkBoxShareClipboard.CheckedChanged += CheckBoxShareClipboard_PortableTextChanged;
        UpdatePortableTransferFileText();

        EasyMouseOption easyMouseOption = (EasyMouseOption)Setting.Values.EasyMouse;
        checkBoxMouseEdgeSwitching = new CheckBox
        {
            AutoSize = true,
            Checked = easyMouseOption != EasyMouseOption.Disable,
            Font = groupBoxOtherOptions.Font,
            Location = new Point(checkBoxVKMap.Left, mouseEdgeSwitchingTop),
            Name = "checkBoxMouseEdgeSwitching",
            TabIndex = checkBoxClipNetStatus.TabIndex + 1,
            Text = "Switch computers at screen edge",
            UseVisualStyleBackColor = true,
        };
        toolTip.SetToolTip(checkBoxMouseEdgeSwitching, "Move the pointer through a configured screen edge to switch computers.");
        checkBoxMouseEdgeSwitching.CheckedChanged += CheckBoxMouseEdgeSwitching_CheckedChanged;
        groupBoxOtherOptions.Controls.Add(checkBoxMouseEdgeSwitching);

        int activationRight = comboBoxEasyMouseOption.Right;
        comboBoxEasyMouseOption.TextChanged -= ComboBoxEasyMouseOption_TextChanged;
        comboBoxEasyMouseOption.Items.Clear();
        comboBoxEasyMouseOption.Items.AddRange(new object[] { EasyMouseOption.Enable, EasyMouseOption.Ctrl,
            EasyMouseOption.Shift, EasyMouseOption.Alt, EasyMouseOption.Win, EasyMouseOption.Custom });
        comboBoxEasyMouseOption.Format += (_, item) => item.Value = Ui.Text(PortableEasyMouseActivationText((EasyMouseOption)item.ListItem));
        comboBoxEasyMouseOption.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxEasyMouseOption.Width = Math.Max(
            comboBoxEasyMouseOption.Width,
            TextRenderer.MeasureText(Ui.Text("Hold Shift"), comboBoxEasyMouseOption.Font).Width + SystemInformation.VerticalScrollBarWidth + 10);
        comboBoxEasyMouseOption.Left = activationRight - comboBoxEasyMouseOption.Width;
        comboBoxEasyMouseOption.SelectedItem = easyMouseOption == EasyMouseOption.Disable ? EasyMouseOption.Enable : easyMouseOption;
        comboBoxEasyMouseOption.Enabled = checkBoxMouseEdgeSwitching.Checked;
        comboBoxEasyMouseOption.TabIndex = checkBoxTransferFile.TabIndex + 1;
        comboBoxEasyMouseOption.Top = activationTop;
        comboBoxEasyMouseOption.SelectedIndexChanged += ComboBoxEasyMouseActivation_SelectedIndexChanged;
        groupBoxOtherOptions.Controls.Add(comboBoxEasyMouseOption);

        labelEasyMouse.Text = "Activation:";
        labelEasyMouse.Left = checkBoxVKMap.Left;
        labelEasyMouse.Top = activationTop + ((comboBoxEasyMouseOption.Height - labelEasyMouse.Height) / 2);
        groupBoxOtherOptions.Controls.Add(labelEasyMouse);
    }

    private static string PortableEasyMouseActivationText(EasyMouseOption option)
    {
        return option switch
        {
            EasyMouseOption.Ctrl => "Hold Ctrl",
            EasyMouseOption.Shift => "Hold Shift",
            EasyMouseOption.Alt => "Hold Alt",
            EasyMouseOption.Win => "Hold Win",
            EasyMouseOption.Custom => "Custom keys",
            _ => "Always",
        };
    }

    private void CheckBoxMouseEdgeSwitching_CheckedChanged(object sender, EventArgs e)
    {
        comboBoxEasyMouseOption.Enabled = checkBoxMouseEdgeSwitching.Checked;
        Setting.Values.EasyMouse = checkBoxMouseEdgeSwitching.Checked
            ? (int)SelectedEasyMouseOption
            : (int)EasyMouseOption.Disable;
        ShowUpdateMessage();
    }

    private void ComboBoxEasyMouseActivation_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (checkBoxMouseEdgeSwitching.Checked)
        {
            Setting.Values.EasyMouse = (int)SelectedEasyMouseOption;
            ShowUpdateMessage();
        }
    }

    private EasyMouseOption SelectedEasyMouseOption =>
        comboBoxEasyMouseOption.SelectedItem is EasyMouseOption option ? option : EasyMouseOption.Enable;

    private void RefreshSharedOptionControls()
    {
        checkBoxShareClipboard.Checked = Setting.Values.ShareClipboard;
        checkBoxTransferFile.Checked = Setting.Values.TransferFile;
        if (limitAutomaticClipboardFiles != null) limitAutomaticClipboardFiles.Checked = Setting.Values.LimitAutomaticClipboardFileSize;
        checkBoxHideMouse.Checked = Setting.Values.HideMouse;
        if (blockFullscreenSwitching != null) blockFullscreenSwitching.Checked = Setting.Values.DisableEasyMouseWhenForegroundWindowIsFullscreen;
        checkBoxBlockScreenSaver.Checked = Setting.Values.BlockScreenSaver;
        checkBoxMouseMoveRelatively.Checked = Setting.Values.MoveMouseRelatively;
        if (checkBoxEnableKeyboardShortcuts != null)
        {
            checkBoxEnableKeyboardShortcuts.Checked = Setting.Values.KeyboardShortcutsEnabled;
            UpdatePortableShortcutControlState();
        }
        RefreshPortableEasyMouseControls();
        if (keyChordButton != null) keyChordButton.Text = ChordButtonText();
    }

    private void RefreshPortableEasyMouseControls()
    {
        if (checkBoxMouseEdgeSwitching == null)
        {
            return;
        }

        EasyMouseOption option = (EasyMouseOption)Setting.Values.EasyMouse;
        EasyMouseOption activation = option == EasyMouseOption.Disable ? EasyMouseOption.Enable : option;
        if (!Equals(comboBoxEasyMouseOption.SelectedItem, activation))
        {
            comboBoxEasyMouseOption.SelectedItem = activation;
        }

        bool enabled = option != EasyMouseOption.Disable;
        if (checkBoxMouseEdgeSwitching.Checked != enabled)
        {
            checkBoxMouseEdgeSwitching.Checked = enabled;
        }
    }

    private void CheckBoxShareClipboard_PortableTextChanged(object sender, EventArgs e)
    {
        UpdatePortableTransferFileText();
    }

    private void UpdatePortableTransferFileText()
    {
        checkBoxTransferFile.Text = Ui.Text("Allow file transfers") + (Setting.Values.TransferFileIsGpoConfigured ? Ui.Text(" [Managed]") : "");
        SetOptionTip(checkBoxTransferFile, "On",
            "Drag/drop files independently of clipboard sharing. Copy/paste files also requires Share Clipboard. Administrator policies still apply.");
    }

    private void ConfigurePortableShortcutControls()
    {
        // These legacy rows have no active backing behavior in the current MWB engine.
        // Hiding them is clearer than showing disabled controls left over from PowerToys.
        labelShowSettings.Visible = comboBoxShowSettings.Visible = false;
        labelExitMM.Visible = comboBoxExitMM.Visible = false;
        labelScreenCapture.Visible = comboBoxScreenCapture.Visible = false;

        checkBoxEnableKeyboardShortcuts = new CheckBox
        {
            AutoSize = true,
            Checked = Setting.Values.KeyboardShortcutsEnabled,
            Font = groupBoxShortcuts.Font,
            Location = new Point(labelSwitchBetweenMachine.Left, labelSwitchBetweenMachine.Top),
            Name = "checkBoxEnableKeyboardShortcuts",
            TabIndex = radioButtonF1.TabIndex - 1,
            UseVisualStyleBackColor = true,
        };
        toolTip.SetToolTip(checkBoxEnableKeyboardShortcuts, "Master switch for every keyboard shortcut listed below. Assignments are preserved while off.");
        checkBoxEnableKeyboardShortcuts.CheckedChanged += CheckBoxEnableKeyboardShortcuts_CheckedChanged;
        groupBoxShortcuts.Controls.Add(checkBoxEnableKeyboardShortcuts);

        UpdatePortableShortcutControlState();
    }

    private void CheckBoxEnableKeyboardShortcuts_CheckedChanged(object sender, EventArgs e)
    {
        Setting.Values.KeyboardShortcutsEnabled = checkBoxEnableKeyboardShortcuts.Checked;
        UpdatePortableShortcutControlState();
        ShowUpdateMessage();
    }

    private void UpdatePortableShortcutControlState()
    {
        checkBoxEnableKeyboardShortcuts.Text = Ui.Text("Enable keyboard shortcuts");

        RefreshShortcutButtons();
    }

    private static HotkeySettings PortableCtrlAltHotkey(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection)
            || selection.Contains("Disable", StringComparison.OrdinalIgnoreCase)
            || selection.Contains("None", StringComparison.OrdinalIgnoreCase))
        {
            return new HotkeySettings();
        }

        char key = char.ToUpperInvariant(selection[0]);
        return key is >= 'A' and <= 'Z'
            ? new HotkeySettings(false, true, true, false, key)
            : new HotkeySettings();
    }

    private void InstallPortableCopyButton_Click(object sender, EventArgs e)
    {
        if (!PortableApplication.PromptToInstallCurrentPortableCopy(this))
        {
            Hide();
            Common.MainForm?.Quit(cleanup: true, isFormClosing: false);
        }
    }
}

#endif
