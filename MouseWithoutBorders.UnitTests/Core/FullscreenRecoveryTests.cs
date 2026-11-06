using System.Drawing;
using System.Globalization;
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
public sealed class FullscreenRecoveryTests
{
    [TestMethod]
    public void FullscreenGeometryUnblocksWhenReturningToAnOrdinaryWindow()
    {
        var screen = new Rectangle(-2560, -1440, 2560, 1440);
        Assert.IsTrue(Common.CoversScreen(screen, screen), "Borderless fullscreen is blocked.");
        Assert.IsTrue(Common.CoversScreen(new(-2568, -1448, 2576, 1456), screen), "Outer frame margins may extend beyond the screen.");
        Assert.IsFalse(Common.CoversScreen(new(-2560, -1440, 2560, 1400), screen), "A maximized window above a taskbar is not fullscreen.");
        Assert.IsFalse(Common.CoversScreen(new(-2400, -1300, 900, 700), screen), "Returning to an ordinary foreground window must unblock regardless of notification suppression.");
        Assert.IsFalse(Common.CoversScreen(new(0, 0, 2560, 1440), screen), "A fullscreen window on another screen is not the foreground screen.");
        Assert.IsFalse(Common.CoversScreen(screen, Rectangle.Empty));
    }

    [TestMethod]
    public async Task FullscreenControlPersistsSynchronizesAndRecoveryPreservesPreferences()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "mwb-fullscreen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var original = Setting.Values;
            string originalName = Common.MachineName;
            int originalRequest = InitAndCleanup.PleaseReopenSocket;
            bool originalReadRecovery = InitAndCleanup.ReopenSocketDueToReadError;
            var topologyState = typeof(MonitorTopology).GetField("state", BindingFlags.Static | BindingFlags.NonPublic)!;
            object? originalTopology = topologyState.GetValue(null);
            var matrixState = typeof(MachineStuff).GetField("mcMatrix", BindingFlags.Static | BindingFlags.NonPublic)!;
            object? originalMatrix = matrixState.GetValue(null);
            Exception? failure = null;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
                Common.MachineName = "LOCAL-PC";
                var document = new MouseWithoutBordersSettings();
                document.Properties.SecurityKey.Value = "synthetic-fullscreen-key";
                document.Properties.MachineID.Value = 101;
                document.Properties.MachineMatrixString = ["LOCAL-PC", "GAME-PC", "", ""];
                document.Properties.EasyMouse.Value = (int)EasyMouseOption.Custom;
                document.Properties.EasyMouseActivationKeys = [(int)Keys.CapsLock];
                document.Properties.DrawMouseCursor = true;
                string path = Path.Combine(folder, "prefs.json");
                PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
                Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                matrixState.SetValue(null, null);
                Assert.AreEqual(101, Setting.Values.MachineId);
                Setting.Values.DesMachineID = 202; // Socket close saves the last controlled PC here.
                Setting.Values.SaveSettingsSynchronously();
                Assert.AreEqual(202u, Setting.Values.DesMachineID);
                Assert.AreEqual(101, PortableSettingsStore.Read(path).Properties.MachineID.Value,
                    "Reconnecting while controlling another PC must never overwrite this PC's persisted identity.");
                var reloadedIdentity = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                Assert.AreEqual(101, reloadedIdentity.MachineId, "A restart must retain the same local identity.");
                using var owner = new MouseWithoutBorders.FrmMatrix { ShowInTaskbar = false };
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [EventArgs.Empty]);
                typeof(MouseWithoutBorders.FrmMatrix).GetField("formShown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, true);
                var controls = Descendants(owner).ToArray();
                var option = controls.OfType<CheckBox>().Single(control => control.Name == "blockFullscreenSwitching");
                Assert.AreEqual("전체 화면 앱에서 화면 전환 차단", option.Text);
                Assert.IsTrue(option.Checked);
                option.Checked = false;
                Setting.Values.SaveSettingsSynchronously();
                Assert.IsFalse(PortableSettingsStore.Read(path).Properties.DisableEasyMouseWhenForegroundWindowIsFullscreen);
                long revision = Setting.Values.SharedSnapshot().Revision;
                Assert.IsTrue(revision > 0, "The real checkbox must commit the shared setting.");
                Assert.IsFalse(Setting.Values.SharedSnapshot().ReadValues().DisableEasyMouseWhenForegroundWindowIsFullscreen);

                var remote = Setting.Values.SharedSnapshot();
                remote.Revision++;
                remote.Writer = 202;
                remote.Values[nameof(MouseWithoutBordersProperties.DisableEasyMouseWhenForegroundWindowIsFullscreen)] = JsonSerializer.SerializeToElement(true);
                Assert.IsTrue(Setting.Values.AcceptSharedSettings(remote));
                long remoteRevision = Setting.Values.SharedSnapshot().Revision;
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("LoadSettingsToUI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
                Assert.IsTrue(option.Checked, "A peer's change must refresh the checkbox.");
                Assert.AreEqual(remoteRevision, Setting.Values.SharedSnapshot().Revision, "Refresh must not publish a competing edit.");

                string before = JsonSerializer.Serialize(Setting.Values.SharedSnapshot().Values);
                foreach (string name in new[] { "inputRecovery", "mainInputRecovery" })
                {
                    var button = controls.OfType<Button>().Single(control => control.Name == name);
                    Assert.AreEqual("마우스 입력 복구", button.Text);
                    // Invoke the real handler even though its tab is not selected in this test.
                    typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
                    Assert.AreEqual(InitAndCleanup.REOPEN_WHEN_HOTKEY, InitAndCleanup.PleaseReopenSocket);
                    Assert.IsTrue(InitAndCleanup.ReopenSocketDueToReadError);
                    Assert.IsTrue(Setting.Values.DrawMouse, "Recovery must preserve this PC's cursor preference.");
                    Assert.AreEqual(before, JsonSerializer.Serialize(Setting.Values.SharedSnapshot().Values), "Recovery must preserve layout and activation/fullscreen preferences.");
                }
                Setting.Values.SaveSettingsSynchronously();
                Assert.IsTrue(PortableSettingsStore.Read(path).Properties.DisableEasyMouseWhenForegroundWindowIsFullscreen);
                using var reopened = new MouseWithoutBorders.FrmMatrix();
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, [EventArgs.Empty]);
                Assert.IsTrue(Descendants(reopened).OfType<CheckBox>().Single(control => control.Name == "blockFullscreenSwitching").Checked);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                Setting.Values.SaveSettingsSynchronously();
                Setting.Values = original;
                Common.MachineName = originalName;
                InitAndCleanup.PleaseReopenSocket = originalRequest;
                InitAndCleanup.ReopenSocketDueToReadError = originalReadRecovery;
                topologyState.SetValue(null, originalTopology);
                matrixState.SetValue(null, originalMatrix);
                Directory.Delete(folder, true);
            }
            if (failure == null) done.SetResult();
            else done.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
