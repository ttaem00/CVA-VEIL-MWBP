using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class Rc8SettingsTests
{
    private string folder = null!, path = null!;
    private readonly List<MouseWithoutBorders.Class.Settings> ownedSettings = new();
    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), "mwb-rc8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); path = Path.Combine(folder, "prefs.json");
    }
    [TestCleanup]
    public void Cleanup()
    {
        // UI changes queue asynchronous writes. Finish test-owned saves before deleting their directory.
        foreach (var settings in ownedSettings) settings.SaveSettingsSynchronously();
        ownedSettings.Clear(); DurableTransfers.ResetAfterTests(); Directory.Delete(folder, true);
    }
    private MouseWithoutBorders.Class.Settings Settings(MouseWithoutBordersSettings? document = null)
    {
        document ??= new MouseWithoutBordersSettings(); document.Properties.SecurityKey.Value = "test-key";
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
        var settings = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
        ownedSettings.Add(settings); return settings;
    }

    [TestMethod]
    public void OlderPreferencesKeepChoicesAndGetOnlyTheNewDefaults()
    {
        var old = PortableSettingsStore.Parse("{\"properties\":{\"SecurityKey\":{\"value\":\"test-key\"},\"MachineMatrixString\":[\"PC1\"],\"TransferFile\":false,\"DrawMouseCursor\":false,\"WrapMouse\":true}}");
        Assert.IsFalse(old.Properties.TransferFile); Assert.IsFalse(old.Properties.DrawMouseCursor); Assert.IsTrue(old.Properties.WrapMouse);
        Assert.AreEqual("PC1", old.Properties.MachineMatrixString.Single());
        Assert.AreEqual("", old.Properties.DefaultReceivingFolder); Assert.IsTrue(old.Properties.AutoCloseTransferWindow);
        old.Properties.ShowClipboardAndNetworkStatusMessages = true; old.Properties.SameSubnetOnly = true;
        var settings = Settings(old);
        Assert.IsFalse(settings.ShowClipNetStatus); Assert.IsFalse(settings.SameSubNetOnly);
    }

    [TestMethod]
    public void NewPreferencesSurviveReloadInstallAndFailedSave()
    {
        var settings = Settings();
        settings.SetDefaultReceivingFolder(folder); settings.SetAutoCloseTransferWindow(false);
        var reloaded = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
        Assert.AreEqual(folder, reloaded.DefaultReceivingFolder); Assert.IsFalse(reloaded.AutoCloseTransferWindow);
        string installed = Path.Combine(folder, "installed.json");
        PortableApplication.SavePreferencesForInstall(path, installed);
        var copy = PortableSettingsStore.Read(installed);
        Assert.AreEqual(folder, copy.Properties.DefaultReceivingFolder); Assert.IsFalse(copy.Properties.AutoCloseTransferWindow);
        Assert.AreEqual("test-key", copy.Properties.SecurityKey.Value);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsException<IOException>(() => settings.SetDefaultReceivingFolder(""));
            Assert.ThrowsException<IOException>(() => settings.SetAutoCloseTransferWindow(true));
        }
        Assert.AreEqual(folder, settings.DefaultReceivingFolder); Assert.IsFalse(settings.AutoCloseTransferWindow);
        Assert.AreEqual(folder, PortableSettingsStore.Read(path).Properties.DefaultReceivingFolder);
    }

    [TestMethod]
    public void MissingCustomDestinationDoesNotCreateOrRedirectFiles()
    {
        string desktop = Path.Combine(folder, "desktop-default"), custom = Path.Combine(folder, "missing-custom");
        Assert.ThrowsException<IOException>(() => TransferReceivePreferences.PrepareFallback(custom, desktop));
        Assert.IsFalse(Directory.Exists(desktop)); Assert.IsFalse(Directory.Exists(custom));
        Directory.CreateDirectory(custom);
        Assert.AreEqual(custom, TransferReceivePreferences.PrepareFallback(custom, desktop));
        Assert.AreEqual(0, Directory.GetFiles(custom).Length, "Write checks leave no probe files behind.");
        Assert.AreEqual(desktop, TransferReceivePreferences.PrepareFallback("", desktop));
        Assert.IsTrue(Directory.Exists(desktop));
    }

    [TestMethod]
    public void ReceivingFolderRequiresAnExplicitLocalPath()
    {
        foreach (var value in new[] { "relative", @"C:relative", @"\\server\share", @"\\?\C:\folder" })
            Assert.ThrowsException<IOException>(() => TransferReceivePreferences.NormalizeFolder(value));
        Assert.AreEqual(folder, TransferReceivePreferences.NormalizeFolder(folder + "\\"));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls) { yield return c; foreach (var nested in Descendants(c)) yield return nested; }
    }
    private static async Task OnSta(Action action, int timeoutSeconds = 25)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                action(); done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    }

    [TestMethod]
    public async Task SettingsLayoutPreservesFileChoiceAndFitsLargerText()
    {
        await OnSta(() =>
        {
            var original = Setting.Values;
            string originalName = Common.MachineName;
            try
            {
                Common.MachineName = "LOCAL-PC";
                var document = new MouseWithoutBordersSettings(); document.Properties.TransferFile = false;
                Setting.Values = Settings(document);
                Setting.Values.Username = "settings-test";
                using var settings = new MouseWithoutBorders.FrmMatrix();
                StringAssert.Contains(settings.Text, Application.ProductVersion);
                // Run the actual settings Load path without starting the input/network timer in Shown.
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, new object[] { EventArgs.Empty });
                typeof(MouseWithoutBorders.FrmMatrix).GetField("formShown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, true);
                var tabs = settings.Controls.OfType<TabControl>().Single();
                using var host = new System.Windows.Forms.Form { ClientSize = settings.ClientSize };
                tabs.Dock = DockStyle.Fill;
                host.Controls.Add(tabs); host.Show();
                var other = tabs.TabPages.Cast<TabPage>().Single(t => t.Text == "Other Options");
                tabs.SelectedTab = other;
                Application.DoEvents();
                var controls = Descendants(tabs).ToArray();
                var tips = (ToolTip)typeof(MouseWithoutBorders.FrmMatrix).GetField("toolTip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
                Assert.IsFalse(other.VerticalScroll.Visible, "Other Options should fit the original window at the normal font size. "
                    + $"Client: {other.ClientSize}; content: {other.DisplayRectangle}. "
                    + string.Join("; ", Descendants(other).Where(c => c.Visible).Select(c => $"{c.GetType().Name}/{c.Name}: {c.Bounds}")));
                Assert.IsFalse(Descendants(other).Any(c => c.Visible && c.Text.Contains("Default:")), "Defaults belong in tooltips, not visible option rows.");
                var divider = controls.OfType<Panel>().Single(c => c.Name == "keyboardShortcutDivider");
                var shortcuts = controls.Single(c => c.Name == "checkBoxEnableKeyboardShortcuts").Parent!;
                Assert.IsTrue(divider.Visible && divider.Height >= 2);
                Assert.IsTrue(divider.Bottom <= shortcuts.Top, "The divider must separate options from shortcuts.");
                var twoRows = controls.Single(c => c.Name == "checkBoxTwoRow");
                Assert.AreEqual("Two rows", twoRows.Text);
                Assert.IsFalse(tips.GetToolTip(twoRows).Contains("Default", StringComparison.OrdinalIgnoreCase));
                var mappings = controls.OfType<TextBox>().Single(c => c.Name == "textBoxMachineName2IP");
                Assert.IsFalse(tips.GetToolTip(mappings).Contains("Default:", StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(tips.GetToolTip(mappings).Contains("OFFICE-PC 192.168.1.20"));
                var mappingsPage = tabs.TabPages.Cast<TabPage>().Single(t => t.Name == "tabPageAdvancedSettings");
                tabs.SelectedTab = mappingsPage; Application.DoEvents();
                Assert.IsTrue(mappings.Visible && mappings.Multiline);
                Assert.IsTrue(mappings.ClientSize.Height >= mappings.Font.Height * 6,
                    "An empty mappings editor must show at least six lines, not collapse to one.");
                mappings.Text = "OFFICE-PC 192.168.1.20\r\nLAPTOP 192.168.1.21\r\nDESKTOP 192.168.1.22";
                Application.DoEvents();
                Assert.AreEqual(3, mappings.Lines.Length);
                Assert.IsTrue(mappings.GetPositionFromCharIndex(mappings.TextLength - 1).Y + mappings.Font.Height <= mappings.ClientSize.Height,
                    "Multiple mapping entries must be visible together.");
                mappings.Clear(); Application.DoEvents();
                Assert.IsTrue(mappings.ClientSize.Height >= mappings.Font.Height * 6, "Clearing entries must not collapse the editor.");
                tabs.SelectedTab = other; Application.DoEvents();
                // Exercise real hide/show layout, not only control construction.
                // The generous bound catches multi-second layout stalls per visit.
                var switchTime = Stopwatch.StartNew();
                for (int visit = 0; visit < 6; visit++)
                {
                    tabs.SelectedIndex = 0; Application.DoEvents();
                    tabs.SelectedTab = other; Application.DoEvents();
                }
                Assert.IsTrue(switchTime.Elapsed < TimeSpan.FromSeconds(3), $"Six tab visits took {switchTime.Elapsed}.");
                var share = controls.OfType<CheckBox>().Single(c => c.Name == "checkBoxShareClipboard");
                var transfer = controls.OfType<CheckBox>().Single(c => c.Name == "checkBoxTransferFile");
                Assert.IsFalse(Setting.Values.TransferFile, "Opening settings must preserve a disabled transfer choice.");
                share.Checked = false; share.Checked = true;
                Assert.IsFalse(transfer.Checked); Assert.IsFalse(Setting.Values.TransferFile);
                transfer.Checked = true; share.Checked = false;
                Assert.IsTrue(Setting.Values.TransferFile); Assert.IsTrue(transfer.Enabled);
                Assert.IsTrue(tips.GetToolTip(transfer).Contains("Copy/paste files also requires Share Clipboard"));
                Assert.IsTrue(tips.GetToolTip(transfer).Contains("Default: On"));
                // A disabled checkbox's parent must still expose its hover help.
                transfer.Enabled = false;
                var hover = new MouseEventArgs(MouseButtons.None, 0, transfer.Left + 5, transfer.Top + 5, 0);
                typeof(Control).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transfer.Parent, new object[] { hover });
                Assert.AreSame(transfer, typeof(MouseWithoutBorders.FrmMatrix).GetField("disabledOptionTipTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings));
                share.Checked = true; Assert.IsTrue(transfer.Enabled); Assert.IsTrue(transfer.Checked);
                foreach (var name in new[] { "checkBoxSameSubNet", "checkBoxClipNetStatus", "checkBoxDisableCAD", "checkBoxHideLogo" })
                    Assert.IsFalse(controls.Single(c => c.Name == name).Visible);
                Assert.IsTrue(tabs.TabPages.Cast<TabPage>().Any(t => t.Name == "installationTab"));
                host.ClientSize = new Size(900, 760);
                using var bigger = new Font(SystemFonts.MessageBoxFont.FontFamily, 14);
                host.Font = bigger; tabs.Font = bigger; host.PerformLayout(); Application.DoEvents();
                foreach (var page in tabs.TabPages.Cast<TabPage>().Where(t => t.Text != "Machine Setup"))
                {
                    tabs.SelectedTab = page; host.PerformLayout(); Application.DoEvents();
                    foreach (var c in Descendants(page).Where(c => c.Visible && c.Parent is MouseWithoutBorders.FrmMatrix.SettingsStack))
                    {
                        Assert.IsTrue(c.Right <= c.Parent!.ClientSize.Width + 1, c.Text + " width");
                        if (c is Label or CheckBox) Assert.IsTrue(c.Height >= c.GetPreferredSize(new Size(c.Width, 0)).Height, c.Text + " wrapped height");
                    }
                }
                Setting.Values.SaveSettingsSynchronously(); host.Close();
            }
            finally { Setting.Values = original; Common.MachineName = originalName; }
        });
    }

    [TestMethod]
    public async Task WindowsCursorButtonCannotChangeMouseKeysOrScreenSwitching()
    {
        await OnSta(() =>
        {
            var original = Setting.Values; var destination = MachineStuff.desMachineID; var next = MachineStuff.newDesMachineID;
            string originalName = Common.MachineName;
            var before = new NativeMethods.MOUSEKEYS { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MOUSEKEYS>() };
            Assert.IsTrue(NativeMethods.SystemParametersInfoMouseKeys(0x36, before.cbSize, ref before, 0));
            try
            {
                Setting.Values = Settings(); Setting.Values.DrawMouse = true;
                Common.MachineName = "LOCAL-PC"; Setting.Values.Username = "cursor-test";
                Setting.Values.EasyMouse = (int)EasyMouseOption.Custom; Setting.Values.EasyMouseActivationKeys = [0x14];
                using var window = new SettingsWindowWithoutNetworkTimer(); window.Show(); Application.DoEvents();
                MachineStuff.desMachineID = MachineStuff.newDesMachineID = (ID)202;
                var tabs = window.Controls.OfType<TabControl>().Single();
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(t => t.Text == "Other Options");
                Application.DoEvents();
                var button = Descendants(window).OfType<Button>().Single(c => c.Name == "nativeCursorRecovery");
                button.PerformClick();
                var after = new NativeMethods.MOUSEKEYS { cbSize = before.cbSize };
                Assert.IsTrue(NativeMethods.SystemParametersInfoMouseKeys(0x36, after.cbSize, ref after, 0));
                Assert.AreEqual(before.dwFlags, after.dwFlags, "A display choice cannot enable OS Mouse Keys or change numeric keypad behavior.");
                Assert.AreEqual(before.iMaxSpeed, after.iMaxSpeed); Assert.AreEqual(before.iTimeToMaxSpeed, after.iTimeToMaxSpeed);
                Assert.IsFalse(Setting.Values.DrawMouse);
                Assert.AreEqual((int)EasyMouseOption.Custom, Setting.Values.EasyMouse);
                CollectionAssert.AreEqual(new[] { 0x14 }, Setting.Values.EasyMouseActivationKeys);
                Assert.AreEqual((ID)202, Common.DesMachineID); Assert.AreEqual((ID)202, MachineStuff.newDesMachineID);
            }
            finally { Setting.Values = original; Common.MachineName = originalName; MachineStuff.desMachineID = destination; MachineStuff.newDesMachineID = next; }
        });
    }

    [DataTestMethod]
    [DataRow(100)]
    [DataRow(150)]
    [DataRow(200)]
    public async Task MatrixSurfaceDragCommitsCancelsAndRepaintsCleanly(int scale)
    {
        await OnSta(() =>
        {
            var original = Setting.Values;
            string name = Common.MachineName;
            var stateField = typeof(MonitorTopology).GetField("state", BindingFlags.Static | BindingFlags.NonPublic)!;
            var topology = stateField.GetValue(null);
            try
            {
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC";
                Setting.Values.MachineId = 103;
                Setting.Values.Username = "matrix-test";
                Encryption.MyKey = "test-key";
                using var form = new SettingsWindowWithoutNetworkTimer();
                if (scale != 100)
                {
                    form.Scale(new SizeF(scale / 100f, scale / 100f));
                    form.Font = new Font(form.Font.FontFamily, form.Font.Size * scale / 100f);
                }
                form.Show(); Application.DoEvents();
                var tabs = Descendants(form).OfType<TabControl>().Single();
                var setupTab = tabs.SelectedTab;
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(t => t.Name == "tabPageOther"); Application.DoEvents();
                Assert.IsTrue(Descendants(form).Single(c => c.Name == "checkBoxMouseEdgeSwitching").Visible);
                Assert.IsTrue(Descendants(form).Single(c => c.Name == "checkBoxMouseMoveRelatively").Visible);
                foreach (string obsolete in new[] { "checkBoxCircle", "checkBoxBlockMouseAtCorners" })
                    Assert.IsFalse(Descendants(form).Single(c => c.Name == obsolete).Visible, "An ineffective option cannot remain visible.");
                tabs.SelectedTab = setupTab; Application.DoEvents();
                uint local = (uint)Common.MachineID;
                MonitorTopology.AcceptPeer(local, [new("Main1", 0, 0, 1600, 900), new("Main2", -8, -900, 1600, 900)]);
                MonitorTopology.AcceptPeer(101, [new("Game1", 0, 0, 1600, 900)]);
                MonitorTopology.AcceptPeer(102, [new("Truman1", 0, 0, 1600, 900)]);
                MonitorTopology.ApplyOrigins([new(local, "Main1", 0, 0), new(101, "Game1", 1600, 0),
                    new(local, "Main2", 3200, 0), new(102, "Truman1", 4800, 0)]);
                var surface = Descendants(form).OfType<MouseWithoutBorders.FrmMatrix.MatrixSurface>().Single();
                surface.RefreshMonitors(); Application.DoEvents();
                var machines = (Machine[])typeof(MouseWithoutBorders.FrmMatrix).GetField("machines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                Assert.IsFalse(Descendants(form).Single(c => c.Name == "checkBoxTwoRow").Visible);
                Assert.AreEqual(4, surface.Draft.Length);
                Assert.IsTrue(machines.All(m => m.NameEditor.Visible && m.EnabledBox.Visible));
                Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 3);
                void Mouse(string method, MouseButtons button, Point point) =>
                    typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(surface, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
                void Key(Keys keys) => typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(surface, new object[] { new KeyEventArgs(keys) });
                int game = Array.FindIndex(surface.Draft, m => m.Key == new MonitorKey(101, "Game1"));
                int main = Array.FindIndex(surface.Draft, m => m.Key == new MonitorKey(local, "Main1"));
                var slots = surface.Slots;
                var destination = new Point(Center(slots[main]).X, slots[main].Bottom + 3 + slots[game].Height / 3);
                var persisted = Setting.Values.MonitorOrigins;
                Mouse("OnMouseDown", MouseButtons.Left, Center(slots[game]));
                Mouse("OnMouseMove", MouseButtons.Left, destination);
                Assert.IsTrue(surface.IsDragging);
                Assert.IsTrue(machines.All(m => m.NameEditor.Visible && m.EnabledBox.Visible), "Moving screens cannot hide independent PC editors.");
                Mouse("OnMouseUp", MouseButtons.Left, destination);
                Assert.IsFalse(surface.IsDragging);
                Assert.AreEqual(900, surface.Draft[game].WorldOrigin.Y, "Near edges snap to a real shared boundary.");
                Assert.IsTrue(surface.HasChanges);
                CollectionAssert.AreEqual(persisted, Setting.Values.MonitorOrigins, "Dragging only changes the draft.");
                var committed = surface.Draft;
                void Drag()
                {
                    var tile = surface.Slots[game];
                    Mouse("OnMouseDown", MouseButtons.Left, Center(tile));
                    Mouse("OnMouseMove", MouseButtons.Left, new Point(Center(tile).X + 30, Center(tile).Y - 20));
                    Assert.IsTrue(surface.IsDragging);
                }
                Drag();
                var escape = new object[] { Message.Create(surface.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero), Keys.Escape };
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, escape);
                CollectionAssert.AreEqual(committed, surface.Draft);
                Drag(); Mouse("OnMouseDown", MouseButtons.Right, Point.Empty);
                CollectionAssert.AreEqual(committed, surface.Draft);
                Drag(); surface.Capture = false;
                CollectionAssert.AreEqual(committed, surface.Draft);
                Drag(); Mouse("OnMouseUp", MouseButtons.Left, new Point(-20, -20));
                CollectionAssert.AreEqual(committed, surface.Draft);
                Mouse("OnMouseDown", MouseButtons.Left, Center(surface.Slots[game]));
                Mouse("OnMouseUp", MouseButtons.Left, Center(surface.Slots[game]));
                Key(Keys.Control | Keys.Right);
                Assert.AreEqual(committed[game].WorldOrigin.X + 1, surface.Draft[game].WorldOrigin.X);
                Key(Keys.Control | Keys.Left);
                var incoming = MonitorTopology.Monitors.Select(m => new MonitorOrigin(m.Key.Peer, m.Key.DisplayId, m.WorldOrigin.X, m.WorldOrigin.Y)).ToArray();
                MonitorTopology.ApplyOrigins(incoming.Select(m => m.Key == new MonitorKey(local, "Main1") ? m with { Y = -1800 } : m).ToArray());
                surface.RefreshMonitors();
                Assert.AreEqual(-1800, surface.Draft.Single(m => m.Key == new MonitorKey(local, "Main1")).WorldOrigin.Y, "Unedited screens follow authenticated metadata updates.");
                Assert.AreEqual(900, surface.Draft.Single(m => m.Key.Peer == 101).WorldOrigin.Y, "A refresh cannot discard the user's pending screen move.");
                MonitorTopology.ApplyOrigins(incoming); surface.RefreshMonitors();
                surface.ApplyDraft(surface.PrepareDraftOrigins());
                Assert.IsFalse(surface.HasChanges);
                Assert.AreEqual(900, Setting.Values.MonitorOrigins.Single(m => m.Key == new MonitorKey(101, "Game1")).Y);
                int truman = Array.FindIndex(surface.Draft, m => m.Key.Peer == 102);
                Mouse("OnMouseDown", MouseButtons.Left, Center(surface.Slots[truman]));
                Mouse("OnMouseUp", MouseButtons.Left, Center(surface.Slots[truman]));
                Key(Keys.Control | Keys.Left);
                var saved = Setting.Values.MonitorOrigins;
                CollectionAssert.AreEqual(saved, surface.PrepareDraftOrigins(), "Keyboard movement cannot overlap the adjacent screen.");
                var invalidDraft = surface.Draft;
                invalidDraft[truman] = invalidDraft[truman] with { WorldOrigin = new(invalidDraft[truman].WorldOrigin.X - 1, invalidDraft[truman].WorldOrigin.Y) };
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, invalidDraft);
                Assert.ThrowsException<ArgumentException>(() => surface.PrepareDraftOrigins(), "A user overlap must not be silently moved by hotplug recovery.");
                CollectionAssert.AreEqual(saved, Setting.Values.MonitorOrigins);
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("ButtonOK_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { form, EventArgs.Empty });
                Assert.AreEqual("test-key", PortableSettingsStore.Read(path).Properties.SecurityKey.Value, "Reject an overlapping layout before changing the shared key.");
                Assert.AreEqual("test-key", Encryption.MyKey);
                StringAssert.Contains(surface.AccessibleDescription, "overlap");
                Key(Keys.Control | Keys.Right);
                StringAssert.Contains(surface.AccessibleDescription, "Drag screens");
                var remote = machines.First(m => !m.LocalHost);
                remote.EnabledBox.Checked = true; remote.NameEditor.Text = "EDITED-PC";
                Assert.IsTrue(remote.MachineEnabled); Assert.AreEqual("EDITED-PC", remote.MachineName);
                foreach (Control control in surface.Controls)
                    Assert.IsTrue(surface.ClientRectangle.Contains(control.Bounds), $"Clipped {control.Name}: {control.Bounds}");
                var previewDir = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "mwb-ui-previews");
                Directory.CreateDirectory(previewDir);
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                preview.Save(Path.Combine(previewDir, $"monitor-layout-{scale}.png"));
            }
            finally { stateField.SetValue(null, topology); Common.MachineName = name; Setting.Values = original; }
        });
    }

    [DataTestMethod]
    [DataRow(100)]
    [DataRow(150)]
    [DataRow(200)]
    public async Task StackedScreensDoNotOverrideSideSnappingOrLeaveOverlappingDrops(int scale)
    {
        await OnSta(() =>
        {
            var original = Setting.Values;
            string name = Common.MachineName;
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var state = typeof(MonitorTopology).GetField("state", BindingFlags.Static | BindingFlags.NonPublic)!;
            var topology = state.GetValue(null);
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC";
                Setting.Values.MachineId = 103; Setting.Values.Username = "matrix-test"; Encryption.MyKey = "test-key";
                using var form = new SettingsWindowWithoutNetworkTimer();
                if (scale != 100)
                {
                    form.Scale(new SizeF(scale / 100f, scale / 100f));
                    form.Font = new Font(form.Font.FontFamily, form.Font.Size * scale / 100f);
                }
                form.Show(); Application.DoEvents();
                var surface = Descendants(form).OfType<MouseWithoutBorders.FrmMatrix.MatrixSurface>().Single();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var draft = new MonitorPlacement[] {
                    new(new(103, "Main1"), new(0, 0, 2560, 1440), new(8, 0)),
                    new(new(103, "Main2"), new(-8, -1440, 2560, 1440), new(0, -1440)),
                    new(new(101, "Game1"), new(0, 0, 2560, 1440), new(2568, 0)),
                    new(new(102, "Truman1"), new(0, 0, 1920, 1080), new(5128, 0)) };
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("draft", flags)!.SetValue(surface, draft);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("selected", flags)!.SetValue(surface, 2);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod("Fit", flags)!.Invoke(surface, null);
                Point Snap(Point origin) => (Point)typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface)
                    .GetMethod("Snap", flags)!.Invoke(surface, new object[] { origin })!;
                Assert.AreEqual(new Point(2568, 0), Snap(new(2558, 0)),
                    "The upper Main screen's zero-distance bottom edge must not undo the side snap.");
                draft[2] = draft[2] with { WorldOrigin = Snap(new(1200, 0)) };
                _ = new MonitorLayout(draft); // A deep overlapping drop must settle on a free edge too.
                draft[2] = draft[2] with { WorldOrigin = new(2568, 0) };
                draft[0] = draft[0] with { WorldOrigin = Point.Empty };
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("selected", flags)!.SetValue(surface, 0);
                draft[0] = draft[0] with { WorldOrigin = Snap(Point.Empty) };
                Assert.AreEqual(new Point(8, 0), draft[0].WorldOrigin,
                    "An existing top contact must not block closing the eight-pixel gap to Game.");
                void Key(Keys keys) => typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod("OnKeyDown", flags)!
                    .Invoke(surface, new object[] { new KeyEventArgs(keys) });
                Key(Keys.Control | Keys.Right);
                Assert.AreEqual(new Point(8, 0), surface.Draft[0].WorldOrigin, "A nudge into Game must be blocked.");
                Key(Keys.Control | Keys.Left);
                Assert.AreEqual(new Point(7, 0), surface.Draft[0].WorldOrigin, "A free one-pixel nudge must remain precise, without magnetic snapping.");
                Key(Keys.Control | Keys.Right);
                var layout = new MonitorLayout(draft);
                Assert.AreEqual(draft[0].Key, layout.Route(draft[2].Key, new(1, 720), new(0, 720)).Monitor.Key);
                Assert.AreEqual(draft[2].Key, layout.Route(draft[0].Key, new(2558, 720), new(2559, 720)).Monitor.Key);
                Assert.AreEqual(draft[3].Key, layout.Route(draft[2].Key, new(2558, 720), new(2559, 720)).Monitor.Key);
                Assert.AreEqual(new Point(6000, 2000), Snap(new(6000, 2000)), "Free placement outside the snap distance stays free.");
                draft[0] = draft[0] with { WorldOrigin = Point.Empty };
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("selected", flags)!.SetValue(surface, 2);
                Assert.AreEqual(new Point(2560, 0), Snap(new(2560, 0)),
                    "Placing Game exactly against Main must not pull it back to Truman eight pixels away.");
                draft[0] = draft[0] with { WorldOrigin = new(8, 0) };
                var game = surface.Slots[2];
                var press = new Point(game.Left + game.Width / 2, game.Top + game.Height / 2);
                foreach (string method in new[] { "OnMouseDown", "OnMouseMove", "OnMouseUp" })
                {
                    var point = method == "OnMouseDown" ? press : new Point(press.X - 6, press.Y);
                    typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod(method, flags)!
                        .Invoke(surface, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
                }
                Assert.AreEqual(new Point(2568, 0), surface.Draft[2].WorldOrigin,
                    "The actual drag-release path must retain the valid side snap.");
                _ = surface.PrepareDraftOrigins();
                if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string previewRoot)
                {
                    string previews = Path.Combine(previewRoot, "mwb-ui-previews"); Directory.CreateDirectory(previews);
                    using var preview = new Bitmap(surface.Width, surface.Height);
                    surface.DrawToBitmap(preview, surface.ClientRectangle);
                    preview.Save(Path.Combine(previews, $"monitor-snap-{scale}.png"));
                }
                Setting.Values.SaveSettingsSynchronously(); form.Close();
            }
            finally { state.SetValue(null, topology); Common.MachineName = name; Setting.Values = original; System.Globalization.CultureInfo.CurrentUICulture = culture; }
        });
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, false)]
    [DataRow(5, false)]
    [DataRow(6, false)]
    [DataRow(7, false)]
    [DataRow(8, false)]
    [DataRow(8, true)]
    [DataRow(9, false)]
    public async Task MonitorSnappingKeepsCornersAndAlignsParallelEdges(int scenario, bool reverse)
    {
        await OnSta(() =>
        {
            var original = Setting.Values; string name = Common.MachineName;
            try
            {
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC"; Setting.Values.MachineId = 103;
                Setting.Values.Username = "snap-test"; Encryption.MyKey = "test-key";
                using var form = new SettingsWindowWithoutNetworkTimer(); form.Show(); Application.DoEvents();
                var surface = Descendants(form).OfType<MouseWithoutBorders.FrmMatrix.MatrixSurface>().Single();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var main = new MonitorPlacement(new(103, "Main"), new(0, 0, 100, 100), Point.Empty);
                var moving = new MonitorPlacement(new(101, "Game"), new(0, 0, scenario is 3 or 4 ? 60 : 100, scenario is 1 or 2 or 5 or 9 ? 60 : 100),
                    scenario is 3 or 4 ? new(20, 100) : new(100, scenario is 1 or 2 or 9 ? 20 : 0));
                var draft = scenario is 0 or 6 or 8 or 9 ? new[] { main, moving, new(new MonitorKey(102, "Truman"), new(0, 0, 100, 100), scenario is 8 or 9 ? new(200, 5) : new(100, 100)) } : new[] { main, moving };
                if (reverse) Array.Reverse(draft);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("draft", flags)!.SetValue(surface, draft);
                int selected = Array.FindIndex(draft, monitor => monitor.Key == moving.Key);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("selected", flags)!.SetValue(surface, selected);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("scale", flags)!.SetValue(surface, scenario == 5 ? .1f : scenario == 9 ? .5f : 1f);
                var (origin, expected) = scenario switch
                {
                    0 => (new Point(99, 1), new Point(100, 0)),
                    1 => (new Point(100, 7), new Point(100, 0)),
                    2 => (new Point(100, 35), new Point(100, 40)),
                    3 => (new Point(7, 100), new Point(0, 100)),
                    4 => (new Point(35, 100), new Point(40, 100)),
                    5 => (new Point(100, 0), new Point(100, 0)),
                    6 => (new Point(50, 1), new Point(100, 0)),
                    8 => (new Point(100, 0), new Point(100, 0)),
                    9 => (new Point(100, 20), new Point(100, 5)),
                    _ => (new Point(300, 300), new Point(300, 300)),
                };
                Point actual = (Point)typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod("Snap", flags)!.Invoke(surface, [origin])!;
                Assert.AreEqual(expected, actual, $"Snap scenario {scenario}; reverse={reverse}");
                draft[selected] = moving with { WorldOrigin = actual };
                _ = new MonitorLayout(draft);
                if (scenario == 0)
                {
                    typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("translation", flags)!.SetValue(surface, new PointF(16, 16));
                    void Mouse(string method, Point point) => typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod(method, flags)!
                        .Invoke(surface, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
                    var tile = surface.Slots[selected]; var press = new Point(tile.Left + tile.Width / 2, tile.Top + tile.Height / 2);
                    Mouse("OnMouseDown", press);
                    Mouse("OnMouseMove", new(press.X - 6, press.Y));
                    Mouse("OnMouseMove", new(press.X - 6, press.Y + 6));
                    Assert.AreEqual(new Point(100, 0), surface.Draft[selected].WorldOrigin, "Dragging from a snapped side into the shared corner must not jump past either neighbour.");
                    Mouse("OnMouseUp", new(press.X - 6, press.Y + 6));
                    Assert.AreEqual(new Point(100, 0), surface.Draft[selected].WorldOrigin);
                    _ = surface.PrepareDraftOrigins();
                }
                Setting.Values.SaveSettingsSynchronously(); form.Close();
            }
            finally { Setting.Values = original; Common.MachineName = name; }
        });
    }

    [TestMethod]
    public async Task MonitorContactMarkersCoverOnlyPassableIntervalsOnEveryScreen()
    {
        await OnSta(() =>
        {
            var original = Setting.Values; var sockets = Common.Sk; string name = Common.MachineName;
            try
            {
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC"; Setting.Values.MachineId = 103;
                Setting.Values.Username = "contact-test"; Encryption.MyKey = "test-key"; Common.Sk = null;
                using var form = new SettingsWindowWithoutNetworkTimer(); form.Show(); Application.DoEvents();
                var surface = Descendants(form).OfType<MouseWithoutBorders.FrmMatrix.MatrixSurface>().Single();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var draft = new[] {
                    new MonitorPlacement(new(103, "A"), new(0, 0, 1000, 1000), new(0, 0)),
                    new MonitorPlacement(new(103, "B"), new(0, 0, 500, 600), new(1000, 200)),
                    new MonitorPlacement(new(103, "C"), new(0, 0, 500, 600), new(1500, 200)),
                    new MonitorPlacement(new(103, "D"), new(0, 0, 500, 600), new(2300, 200)) };
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("draft", flags)!.SetValue(surface, draft);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("selected", flags)!.SetValue(surface, 0);
                typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetMethod("Fit", flags)!.Invoke(surface, null);
                float scale = (float)typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("scale", flags)!.GetValue(surface)!;
                var offset = (PointF)typeof(MouseWithoutBorders.FrmMatrix.MatrixSurface).GetField("translation", flags)!.GetValue(surface)!;
                bool HasMarker(Bitmap bitmap, int x, int low, int high)
                {
                    int viewX = (int)Math.Round(x * scale + offset.X);
                    for (int y = (int)Math.Ceiling(low * scale + offset.Y); y < high * scale + offset.Y; y++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (bitmap.GetPixel(viewX + dx, y).ToArgb() == (SystemInformation.HighContrast ? SystemColors.Highlight : Color.SeaGreen).ToArgb()) return true;
                    return false;
                }
                using (var image = new Bitmap(surface.Width, surface.Height))
                {
                    surface.DrawToBitmap(image, surface.ClientRectangle);
                    Assert.IsTrue(HasMarker(image, 1000, 220, 780), "The partial selected-screen contact is marked.");
                    Assert.IsTrue(HasMarker(image, 1500, 220, 780), "Contacts between two unselected screens must also be visible.");
                    Assert.IsFalse(HasMarker(image, 1000, 20, 180), "The unshared portion of the edge is not passable.");
                    Assert.IsFalse(HasMarker(image, 2000, 220, 780), "A gap cannot be painted as a connection.");
                    if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string previewRoot)
                    {
                        string previews = Path.Combine(previewRoot, "mwb-ui-previews"); Directory.CreateDirectory(previews);
                        image.Save(Path.Combine(previews, "monitor-passable-contacts.png"));
                    }
                }
                var layout = new MonitorLayout(draft);
                Assert.AreEqual(draft[0].Key, layout.Route(draft[0].Key, new(998, 199), new(999, 199)).Monitor.Key);
                Assert.AreEqual(draft[1].Key, layout.Route(draft[0].Key, new(998, 200), new(999, 200)).Monitor.Key);
                Assert.AreEqual(draft[1].Key, layout.Route(draft[0].Key, new(998, 799), new(999, 799)).Monitor.Key);
                Assert.AreEqual(draft[0].Key, layout.Route(draft[0].Key, new(998, 800), new(999, 800)).Monitor.Key);
                draft[2] = draft[2] with { Key = new(102, "Offline") };
                using (var image = new Bitmap(surface.Width, surface.Height))
                {
                    surface.DrawToBitmap(image, surface.ClientRectangle);
                    Assert.IsFalse(HasMarker(image, 1500, 220, 780), "An offline peer is not a passable connection.");
                }
                Setting.Values.SaveSettingsSynchronously(); form.Close();
            }
            finally { Setting.Values = original; Common.Sk = sockets; Common.MachineName = name; }
        });
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ShortcutRecorderSavesCapsLockAndRestoresRoutingAfterItsOwnedDialog(bool rapid)
    {
        await OnSta(() =>
        {
            var original = Setting.Values; string name = Common.MachineName;
            var oldHook = Common.Hook; var oldCallback = Common.InputCallbackForm; int threadId = Common.InputCallbackThreadID;
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var oldKeys = held.DownKeys; held.Clear();
            using var callback = new MouseWithoutBorders.FrmInputCallback();
            _ = callback.Handle; // A message owner only; not shown, so no native hooks are installed.
            var hook = (InputHook)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
            var released = new List<KEYBDDATA>(); hook.KeyboardEvent += data => released.Add(data);
            try
            {
                Common.Hook = hook; Common.InputCallbackForm = callback; Common.InputCallbackThreadID = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC"; Setting.Values.Username = "record-test";
                Setting.Values.KeyboardShortcutsEnabled = true; Encryption.MyKey = "test-key";
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
                using var form = new SettingsWindowWithoutNetworkTimer(); form.Show(); Application.DoEvents();
                var tabs = Descendants(form).OfType<TabControl>().Single();
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Name == "tabPageOther"); Application.DoEvents();
                var button = Descendants(form).OfType<Button>().Single(control => control.Name == "shortcutNextComputer");
                CollectionAssert.AreEqual(new[] { (int)Keys.ControlKey, (int)Keys.Menu, (int)Keys.F5 }, Setting.Values.GetShortcutKeys(KeyboardAction.NextComputer));
                Assert.AreEqual(9, Descendants(form).OfType<Button>().Count(control => control.Name.StartsWith("shortcut", StringComparison.Ordinal)));
                held.Update((int)Keys.F24, true);
                int stage = 0; Exception? failure = null;
                Action<int, bool>? recorded = null;
                using var timer = new System.Windows.Forms.Timer { Interval = 20 };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        var dialog = form.OwnedForms.Single();
                        if (stage++ == 0)
                        {
                            Assert.IsTrue(InputHook.RecordingKeys); Assert.IsNotNull(InputHook.KeyRecorder);
                            var recorder = InputHook.KeyRecorder!;
                            recorded = recorder;
                            recorder((int)Keys.CapsLock, true);
                            if (rapid) recorder((int)Keys.CapsLock, false);
                        }
                        else
                        {
                            StringAssert.Contains(dialog.Controls.OfType<TextBox>().Single().Text, "Caps Lock",
                                $"Rapid={rapid}");
                            if (!rapid && stage == 2) InputHook.KeyRecorder!((int)Keys.CapsLock, false);
                            else { timer.Stop(); dialog.DialogResult = DialogResult.OK; }
                        }
                    }
                    catch (Exception error) { failure = error; timer.Stop(); foreach (var dialog in form.OwnedForms) dialog.DialogResult = DialogResult.Cancel; }
                };
                timer.Start(); button.PerformClick();
                if (failure != null) throw failure;
                recorded!((int)Keys.A, true); // A hook callback captured before closing may arrive after the owned dialog is disposed.
                Application.DoEvents();
                CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, Setting.Values.GetShortcutKeys(KeyboardAction.NextComputer));
                StringAssert.Contains(button.Text, "Caps Lock");
                Assert.IsFalse(InputHook.RecordingKeys); Assert.IsNull(InputHook.KeyRecorder);
                Assert.IsTrue(released.Any(key => key.wVk == (int)Keys.F24 && (key.dwFlags & (int)WM.LLKHF.UP) != 0), "Pre-dialog remote DOWN is completed before recording.");
                if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string previewRoot)
                {
                    string previews = Path.Combine(previewRoot, "mwb-ui-previews"); Directory.CreateDirectory(previews);
                    using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(Path.Combine(previews, "shortcuts-recorded-ko.png"));
                }
                Setting.Values.SaveSettingsSynchronously(); form.Close();
            }
            finally
            {
                InputHook.KeyRecorder = null; InputHook.RecordingKeys = false; held.Clear(); foreach (int key in oldKeys) held.Update(key, true);
                Common.Hook = oldHook; Common.InputCallbackForm = oldCallback; Common.InputCallbackThreadID = threadId;
                Setting.Values = original; Common.MachineName = name; System.Globalization.CultureInfo.CurrentUICulture = culture;
            }
        });
    }

    [TestMethod]
    public async Task ClosedSettingsWindowDoesNotDisplayLateOperationErrors()
    {
        await OnSta(() =>
        {
            var original = Setting.Values; string name = Common.MachineName;
            try
            {
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC"; Setting.Values.Username = "closed-settings-test"; Encryption.MyKey = "test-key";
                using var form = new SettingsWindowWithoutNetworkTimer(); form.Show(); Application.DoEvents();
                form.Close(); Assert.IsTrue(form.IsDisposed);
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("SettingsError", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, [new IOException("A background operation failed after the settings window closed.")]);
                Assert.IsTrue(form.IsDisposed, "Reporting a late failure must not recreate the closed settings view.");
            }
            finally { Setting.Values = original; Common.MachineName = name; }
        });
    }

    [TestMethod]
    public async Task ManualConnectionRemainsAvailableWithoutDiscoveryAndKeepsFailureFeedback()
    {
        await OnSta(() =>
        {
            var original = Setting.Values; string name = Common.MachineName;
            var discovery = typeof(MouseWithoutBorders.Class.Program).GetProperty("Discovery", BindingFlags.Static | BindingFlags.NonPublic)!;
            var pairing = typeof(MouseWithoutBorders.Class.Program).GetProperty("Pairing", BindingFlags.Static | BindingFlags.NonPublic)!;
            object? oldDiscovery = discovery.GetValue(null), oldPairing = pairing.GetValue(null);
            try
            {
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC"; Setting.Values.Username = "manual-connection-test"; Encryption.MyKey = "test-key";
                discovery.SetValue(null, null); pairing.SetValue(null, null);
                using var form = new SettingsWindowWithoutNetworkTimer(); form.Show(); Application.DoEvents();
                var find = Descendants(form).OfType<Button>().Single(button => button.Name == "findComputers");
                using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                Exception? failure = null; string message = ""; var elapsed = new Stopwatch();
                timer.Tick += (_, _) =>
                {
                    var dialog = form.OwnedForms.SingleOrDefault();
                    if (dialog == null) return;
                    try
                    {
                        var input = Descendants(dialog).OfType<TextBox>().Single(control => control.Name == "manualPeerAddress");
                        var connect = Descendants(dialog).OfType<Button>().Single(control => control.Name == "connectComputer");
                        var feedback = Descendants(dialog).OfType<Label>().Single(control => control.Name == "connectionFeedback");
                        if (!elapsed.IsRunning)
                        {
                            Assert.AreSame(form, dialog.Owner); Assert.IsTrue(input.Focused);
                            Assert.IsFalse(string.IsNullOrWhiteSpace(input.AccessibleName));
                            Assert.AreSame(connect, dialog.AcceptButton);
                            input.Text = "REMOTE-PC"; Assert.IsTrue(connect.Enabled, "A manual destination must work without discovery results.");
                            Assert.IsTrue((bool)typeof(System.Windows.Forms.Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [Keys.Enter])!);
                            message = feedback.Text;
                            Assert.IsFalse(string.IsNullOrWhiteSpace(message), "A failed request must explain the failure inside the same dialog.");
                            Assert.AreEqual(AccessibleRole.Alert, feedback.AccessibleRole);
                            Assert.IsTrue(input.Enabled && connect.Enabled, "A failed request must allow another attempt.");
                            elapsed.Start();
                        }
                        else if (elapsed.ElapsedMilliseconds >= 1500)
                        {
                            Assert.AreEqual(message, feedback.Text, "Discovery refresh must not erase connection failure feedback.");
                            timer.Stop(); dialog.DialogResult = DialogResult.Cancel;
                        }
                    }
                    catch (Exception error) { failure = error; timer.Stop(); dialog.Close(); }
                };
                timer.Start(); find.PerformClick();
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.IsTrue(elapsed.IsRunning); Assert.IsTrue(form.Enabled);
                Setting.Values.SaveSettingsSynchronously(); form.Close();
            }
            finally { discovery.SetValue(null, oldDiscovery); pairing.SetValue(null, oldPairing); Setting.Values = original; Common.MachineName = name; }
        });
    }

    private sealed class SettingsWindowWithoutNetworkTimer : MouseWithoutBorders.FrmMatrix
    {
        internal Action<string> TraceStep = _ => { };
        protected override void OnShown(EventArgs e)
        {
            // Use the real form, Load, machine setup and layout initialization.
            // Omit only the Shown handler's network/input polling timer.
            foreach (var name in new[] { "InitAll", "ConfigurePortableMachineTiles" })
            {
                TraceStep(name);
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);
            }
            TraceStep("Shown complete");
            typeof(MouseWithoutBorders.FrmMatrix).GetField("formShown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
        }
    }

    [DataTestMethod]
    [DataRow(100)]
    [DataRow(150)]
    [DataRow(200)]
    public async Task KoreanFileActionsAndClipboardLimitFitAtNormalAndLargeText(int scalePercent)
    {
        await OnSta(() =>
        {
            var original = Setting.Values;
            string machineName = Common.MachineName;
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
                Setting.Values = Settings(); Common.MachineName = "LOCAL-PC"; Setting.Values.Username = "file-layout-test";
                foreach (float points in new[] { Control.DefaultFont.SizeInPoints, 14f })
                {
                    using var settings = new SettingsWindowWithoutNetworkTimer();
                    float scale = scalePercent / 100f;
                    settings.Scale(new SizeF(scale, scale));
                    using var font = new Font(settings.Font.FontFamily, points * scale);
                    settings.Font = font;
                    settings.Show(); Application.DoEvents();
                    var controls = Descendants(settings).ToArray();
                    var find = controls.OfType<Button>().Single(c => c.Name == "findComputers");
                    var send = controls.OfType<Button>().Single(c => c.Name == "sendFiles");
                    Assert.AreSame(find.Parent, send.Parent);
                    Assert.IsFalse(find.Bounds.IntersectsWith(send.Bounds), $"File and connection actions overlap at {scalePercent}%, {points}pt.");
                    foreach (var button in new[] { find, send })
                    {
                        Assert.IsTrue(button.Visible && button.Enabled && button.TabStop);
                        Assert.IsTrue(button.Parent!.ClientRectangle.Contains(button.Bounds), $"Clipped {button.Name}: {button.Bounds} in {button.Parent.ClientSize}.");
                        Assert.IsTrue(button.Width >= button.GetPreferredSize(Size.Empty).Width && button.Height >= button.GetPreferredSize(Size.Empty).Height,
                            $"Clipped action text at {scalePercent}%, {points}pt: {button.Name}.");
                    }
                    var tabs = controls.OfType<TabControl>().Single();
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(t => t.Name == "tabPageOther"); Application.DoEvents();
                    var limit = controls.OfType<CheckBox>().Single(c => c.Name == "limitAutomaticClipboardFiles");
                    Assert.IsTrue(limit.Text.Any(c => c >= '\uAC00' && c <= '\uD7A3'), "Exercise the Korean caption, not the English fallback.");
                    Assert.IsTrue(limit.Visible && limit.TabStop);
                    Assert.IsTrue(limit.Parent!.ClientRectangle.Contains(limit.Bounds), $"Clipped clipboard limit: {limit.Bounds} in {limit.Parent.ClientSize}.");
                    using (var graphics = limit.CreateGraphics())
                    {
                        int glyph = CheckBoxRenderer.GetGlyphSize(graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
                        int requiredWidth = TextRenderer.MeasureText(limit.Text, limit.Font).Width + glyph + 3 + limit.Padding.Horizontal;
                        Assert.IsTrue(requiredWidth <= limit.Width,
                            $"Clipped single-line clipboard limit at {scalePercent}%, {points}pt: needs {requiredWidth}px, available {limit.Width}px.");
                    }
                    Assert.IsTrue(limit.Height >= limit.GetPreferredSize(new Size(limit.Width, 0)).Height, $"Clipped wrapped checkbox at {scalePercent}%, {points}pt.");
                    foreach (Control sibling in limit.Parent.Controls)
                        if (sibling != limit && sibling.Visible) Assert.IsFalse(limit.Bounds.IntersectsWith(sibling.Bounds), "The clipboard limit overlaps the next option.");
                    if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string temp)
                    {
                        string previews = Path.Combine(temp, "mwb-ui-previews"); Directory.CreateDirectory(previews);
                        using var image = new Bitmap(settings.ClientSize.Width, settings.ClientSize.Height);
                        settings.DrawToBitmap(image, settings.ClientRectangle);
                        image.Save(Path.Combine(previews, $"clipboard-limit-ko-{scalePercent}-{points:0}.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    settings.Close();
                }
            }
            finally { Setting.Values = original; Common.MachineName = machineName; System.Globalization.CultureInfo.CurrentUICulture = culture; }
        });
    }

    [DataTestMethod]
    [DataRow(100)]
    [DataRow(150)]
    [DataRow(200)]
    public async Task SettingsDividerRendersAndScrollRangeTracksContent(int scalePercent)
    {
        void Trace(string step)
        {
            if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string temp)
                File.WriteAllText(Path.Combine(temp, $"mwb-settings-stage-{scalePercent}.txt"), step);
        }
        await OnSta(() =>
        {
            var original = Setting.Values;
            string machineName = Common.MachineName;
            try
            {
                Setting.Values = Settings();
                Common.MachineName = "LOCAL-PC";
                Setting.Values.Username = "layout-test";
                Trace("constructing form");
                using var settings = new SettingsWindowWithoutNetworkTimer();
                settings.TraceStep = Trace;
                float scale = scalePercent / 100f;
                // Exercise scaled geometry and text, including controls built in Load.
                // Physical per-monitor DPI changes remain a real-PC check.
                Trace("scaling form"); settings.Scale(new SizeF(scale, scale));
                using var font = new Font(settings.Font.FontFamily, settings.Font.SizeInPoints * scale);
                settings.Font = font;
                Trace("showing form"); settings.Show(); Trace("pumping first show"); Application.DoEvents();
                var originalSize = settings.ClientSize;
                var tabs = settings.Controls.OfType<TabControl>().Single();
                var other = tabs.TabPages.Cast<TabPage>().Single(t => t.Text == "Other Options");
                var content = other.Controls.OfType<MouseWithoutBorders.FrmMatrix.SettingsStack>().Single();
                tabs.SelectedTab = other; Application.DoEvents();
                var recovery = Descendants(other).OfType<Button>().Single(c => c.Name == "nativeCursorRecovery");
                Assert.IsTrue(recovery.Visible && recovery.Enabled && recovery.TabStop, "Cursor recovery must be an available keyboard-accessible action.");
                Assert.AreEqual("Use Windows cursor", recovery.Text);
                var tips = (ToolTip)typeof(MouseWithoutBorders.FrmMatrix).GetField("toolTip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
                StringAssert.Contains(tips.GetToolTip(recovery), "numeric keypad");
                Assert.AreEqual(tips.GetToolTip(recovery), recovery.AccessibleDescription);
                var inputRecovery = Descendants(other).OfType<Button>().Single(c => c.Name == "inputRecovery");
                Assert.IsTrue(inputRecovery.Visible && inputRecovery.Enabled && inputRecovery.TabStop);
                Assert.AreEqual("Restore mouse input", inputRecovery.Text);
                StringAssert.Contains(tips.GetToolTip(inputRecovery), "restarts input hooks");
                Assert.AreEqual(tips.GetToolTip(inputRecovery), inputRecovery.AccessibleDescription);
                var sharedBeforeRecovery = System.Text.Json.JsonSerializer.Serialize(Setting.Values.SharedSnapshot());
                bool cursorBeforeRecovery = Setting.Values.DrawMouse;
                int pendingReconnect = InitAndCleanup.PleaseReopenSocket;
                bool readRecovery = InitAndCleanup.ReopenSocketDueToReadError;
                try
                {
                    InitAndCleanup.PleaseReopenSocket = 0;
                    inputRecovery.PerformClick();
                    Assert.AreEqual(cursorBeforeRecovery, Setting.Values.DrawMouse, "Input recovery preserves cursor settings.");
                    Assert.AreEqual(InitAndCleanup.REOPEN_WHEN_HOTKEY, InitAndCleanup.PleaseReopenSocket);
                    Assert.AreEqual(sharedBeforeRecovery, System.Text.Json.JsonSerializer.Serialize(Setting.Values.SharedSnapshot()),
                        "Recovery preserves the connection layout, shortcuts and sharing preferences.");
                    Setting.Values.SaveSettingsSynchronously();
                    Assert.AreEqual(cursorBeforeRecovery, PortableSettingsStore.Read(path).Properties.DrawMouseCursor);
                }
                finally { InitAndCleanup.PleaseReopenSocket = pendingReconnect; InitAndCleanup.ReopenSocketDueToReadError = readRecovery; }
                var divider = Descendants(other).Single(c => c.Name == "keyboardShortcutDivider");
                foreach (var width in new[] { originalSize.Width, originalSize.Width + 240, originalSize.Width * 2 / 3, originalSize.Width })
                {
                    Trace($"resizing to {width}");
                    settings.ClientSize = new Size(width, originalSize.Height);
                    foreach (var page in tabs.TabPages.Cast<TabPage>())
                    {
                        Trace($"visiting {page.Text} at {width}");
                        tabs.SelectedTab = page; Application.DoEvents();
                        tabs.SelectedTab = other; Application.DoEvents();
                        string bounds = $"Scale {scalePercent}; client {other.ClientSize}; display {other.DisplayRectangle}; root {content.Bounds}. "
                            + string.Join("; ", Descendants(other).Where(c => c.Visible).Select(c => $"{c.GetType().Name}/{c.Name}: {c.Bounds}"));
                        foreach (var stack in Descendants(other).OfType<MouseWithoutBorders.FrmMatrix.SettingsStack>().Where(c => c.Visible))
                        {
                            int bottom = stack.Controls.Cast<Control>().Where(c => c.Visible).Max(c => c.Bottom + c.Margin.Bottom) + stack.Padding.Bottom;
                            Assert.IsTrue(stack.Height <= bottom + 2, "No unused rows below a settings section. " + bounds);
                        }
                        int required = content.Bottom - other.AutoScrollPosition.Y + other.Padding.Bottom;
                        Assert.IsTrue(other.DisplayRectangle.Height <= Math.Max(required, other.ClientSize.Height) + 8,
                            "The scrollbar must not include empty space beyond the content. " + bounds);
                        if (width >= originalSize.Width)
                            Assert.IsFalse(other.VerticalScroll.Visible, "The original-size page should not need a scrollbar. " + bounds);
                    }
                }
                Trace("rendering divider");
                using var pixels = new Bitmap(other.ClientSize.Width, other.ClientSize.Height);
                other.DrawToBitmap(pixels, other.ClientRectangle);
                var line = other.RectangleToClient(divider.RectangleToScreen(divider.ClientRectangle));
                Assert.IsTrue(line.Width >= other.ClientSize.Width * 0.85 && line.Height >= 1 && other.ClientRectangle.Contains(line),
                    "The divider must span the visible options page.");
                int contrasting = 0, samples = 0;
                for (int x = line.Left + 4; x < line.Right - 4; x++)
                {
                    if (Math.Abs(pixels.GetPixel(x, line.Top + line.Height / 2).GetBrightness() - other.BackColor.GetBrightness()) >= 0.2f)
                        contrasting++;
                    samples++;
                }
                Assert.IsTrue(contrasting >= samples * 0.95, "The rendered divider must visibly contrast with the page, not just exist as a control.");
                if (scalePercent == 150 && Environment.GetEnvironmentVariable("RUNNER_TEMP") is string runnerTemp)
                {
                    string previews = Path.Combine(runnerTemp, "mwb-ui-previews"); Directory.CreateDirectory(previews);
                    pixels.Save(Path.Combine(previews, "settings-150.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Trace("closing form"); Setting.Values.SaveSettingsSynchronously(); settings.Close(); Trace("complete");
            }
            finally { Setting.Values = original; Common.MachineName = machineName; }
        });
    }

    [TestMethod]
    public async Task CompletedWindowStaysOpenUntilAutoCloseIsEnabled()
    {
        await OnSta(() =>
        {
            var job = new TransferJob { Name = "done.bin", State = "Completed", Sending = true };
            DurableTransfers.ConfigureForTests(Path.Combine(folder, "jobs.json"), job);
            bool automatic = false;
            using var window = new MouseWithoutBorders.TransferCenter(() => automatic); window.Show();
            Pump(TimeSpan.FromSeconds(3)); Assert.IsTrue(window.Visible);
            automatic = true; job.State = "Error";
            Pump(TimeSpan.FromSeconds(3)); Assert.IsTrue(window.Visible, "Errors must remain visible when automatic closing is enabled.");
            job.State = "Completed";
            Pump(TimeSpan.FromSeconds(3)); Assert.IsTrue(window.IsDisposed);
        });
    }
    [DataTestMethod]
    [DataRow(100, "ko-KR")]
    [DataRow(150, "ko-KR")]
    [DataRow(200, "ko-KR")]
    [DataRow(100, "en-US")]
    [DataRow(150, "en-US")]
    [DataRow(200, "en-US")]
    public async Task LocalizedSettingsPagesRenderFromSyntheticPreferences(int scalePercent, string cultureName)
    {
        int holdSeconds = scalePercent == 100 && cultureName == "ko-KR"
            && int.TryParse(Environment.GetEnvironmentVariable("MWBP_UI_QA_HOLD_SECONDS"), out int requested) && requested is > 0 and <= 300 ? requested : 0;
        await OnSta(() =>
        {
            Application.EnableVisualStyles();
            var original = Setting.Values;
            string machineName = Common.MachineName;
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var topologyField = typeof(MonitorTopology).GetField("state", BindingFlags.Static | BindingFlags.NonPublic)!;
            var matrixField = typeof(MachineStuff).GetField("mcMatrix", BindingFlags.Static | BindingFlags.NonPublic)!;
            var poolField = typeof(MachineStuff).GetField("_machinePool", BindingFlags.Static | BindingFlags.NonPublic)!;
            var topology = topologyField.GetValue(null); var matrix = matrixField.GetValue(null); var pool = poolField.GetValue(null);
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
                var document = new MouseWithoutBordersSettings();
                document.Properties.DefaultReceivingFolder = @"C:\Users\Example\Desktop\MWBP";
                document.Properties.MachineID.Value = 103;
                document.Properties.MachineMatrixString = ["LOCAL-PC", "SECOND-PC", "", ""];
                Setting.Values = Settings(document); Common.MachineName = "LOCAL-PC"; Setting.Values.Username = "ui-preview";
                matrixField.SetValue(null, new[] { "LOCAL-PC", "SECOND-PC", "", "" });
                poolField.SetValue(null, new MachinePool());
                MachineStuff.MachinePool.Initialize(new[] { new MachineInf { Name = "LOCAL-PC", Id = (ID)103 }, new MachineInf { Name = "SECOND-PC", Id = (ID)101 } });
                MonitorTopology.AcceptPeer(103, [new("DISPLAY1", 0, 0, 1920, 1080), new("DISPLAY2", 0, 1080, 1920, 1080)]);
                MonitorTopology.AcceptPeer(101, [new("DISPLAY1", 0, 0, 1920, 1080)]);
                MonitorTopology.ApplyOrigins([new(103, "DISPLAY1", 0, 0), new(103, "DISPLAY2", 0, 1080), new(101, "DISPLAY1", 1920, 0)]);
                using var settings = new SettingsWindowWithoutNetworkTimer();
                settings.ShowInTaskbar = false;
                settings.StartPosition = FormStartPosition.Manual; settings.Location = new Point(-10000, -10000);
                settings.Scale(new SizeF(scalePercent / 100f, scalePercent / 100f));
                settings.Font = new Font(settings.Font.FontFamily, settings.Font.SizeInPoints * scalePercent / 100f);
                settings.Show(); Application.DoEvents();
                var help = (HelpProvider)typeof(MouseWithoutBorders.FrmMatrix).GetField("optionHelp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
                var find = Descendants(settings).Single(c => c.Name == "findComputers");
                var send = Descendants(settings).Single(c => c.Name == "sendFiles");
                Assert.AreSame(send, find.Parent!.GetNextControl(find, true), "Tab moves from finding a PC to sending files.");
                foreach (var link in Descendants(settings).OfType<LinkLabel>().Where(c => c.Name is "linkLabelHelp" or "linkLabelMiniLog"))
                {
                    Assert.IsTrue(link.Visible && link.Parent!.ClientRectangle.Contains(link.Bounds), "Help and diagnostics remain visible and keyboard reachable.");
                    var bounds = settings.RectangleToClient(link.RectangleToScreen(link.ClientRectangle));
                    Assert.IsTrue(settings.ClientRectangle.Contains(bounds), $"Help link outside the form: {link.Name}/{bounds} in {settings.ClientRectangle}.");
                }
                var tabs = Descendants(settings).OfType<TabControl>().Single();
                var helpLinks = settings.Controls.Cast<Control>().Single(c => c.Name == "settingsHelpLinks");
                Assert.IsFalse(tabs.Bounds.IntersectsWith(helpLinks.Bounds), "The docked help row must reserve space instead of covering the tab page.");
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page; Application.DoEvents();
                    foreach (Control child in Descendants(page).Where(c => c.Visible && c is Button))
                    {
                        Assert.IsTrue(child.Width >= child.GetPreferredSize(Size.Empty).Width || child is Button { AutoEllipsis: true },
                            $"Button text clipped at {scalePercent}%: {child.Name}/{child.Text}.");
                        Assert.IsTrue(child.Parent!.ClientRectangle.Contains(child.Bounds), $"Button outside its page at {scalePercent}%: {child.Name}/{child.Bounds} in {child.Parent.ClientRectangle}.");
                        if (child.Name is "buttonOK" or "buttonCancel")
                        {
                            var formBounds = settings.RectangleToClient(child.RectangleToScreen(child.ClientRectangle));
                            Assert.IsFalse(formBounds.IntersectsWith(helpLinks.Bounds), "The help row cannot cover Apply or Close.");
                        }
                        foreach (Control sibling in child.Parent!.Controls)
                            if (sibling != child && sibling.Visible && sibling is Button)
                                Assert.IsFalse(child.Bounds.IntersectsWith(sibling.Bounds), $"Buttons overlap: {child.Name}/{sibling.Name}.");
                    }
                    if (page.Name == "tabPageAdvancedSettings")
                    {
                        var editor = Descendants(page).OfType<TextBox>().Single(c => c.Name == "textBoxMachineName2IP");
                        Assert.IsFalse(string.IsNullOrWhiteSpace(help.GetHelpString(editor)));
                        Assert.AreEqual(editor.AccessibleDescription, help.GetHelpString(editor), "Keyboard and screen-reader help match hover help.");
                        var details = Descendants(page).OfType<LinkLabel>().Single(c => c.Name == "ipMappingHelp");
                        Assert.IsTrue(details.TabStop && details.Visible, "Mapping instructions remain available without hover.");
                    }
                    if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string root)
                    {
                        string previews = Path.Combine(root, "mwb-ui-previews"); Directory.CreateDirectory(previews);
                        using var image = new Bitmap(settings.Width, settings.Height);
                        settings.DrawToBitmap(image, new Rectangle(Point.Empty, settings.Size));
                        image.Save(Path.Combine(previews, $"settings-{cultureName[..2]}-{page.Name}-{scalePercent}.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                if (holdSeconds > 0)
                {
                    tabs.SelectedIndex = 0; settings.Location = new Point(80, 80); settings.ShowInTaskbar = true;
                    settings.Activate(); Application.DoEvents();
                    if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is string root)
                        File.WriteAllText(Path.Combine(root, "ui-qa-ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId, window = settings.Handle.ToInt64(), syntheticSettings = path, scalePercent }));
                    Pump(TimeSpan.FromSeconds(holdSeconds));
                }
                settings.Close();
            }
            finally
            {
                topologyField.SetValue(null, topology); matrixField.SetValue(null, matrix); poolField.SetValue(null, pool);
                Setting.Values = original; Common.MachineName = machineName; System.Globalization.CultureInfo.CurrentUICulture = culture;
            }
        }, timeoutSeconds: 25 + holdSeconds);
    }

    private static void Pump(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
    }
}
