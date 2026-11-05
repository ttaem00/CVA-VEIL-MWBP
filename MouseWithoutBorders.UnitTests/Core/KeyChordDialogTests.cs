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
public sealed class KeyChordDialogTests
{
    [TestMethod]
    public async Task KeyEditorIsOwnedFocusedAndSavesOnlyWhenApplied()
    {
        await OnSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "mwb-chord-dialog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var original = Setting.Values;
            string originalName = Common.MachineName;
            var originalHook = Common.Hook; var originalCallback = Common.InputCallbackForm; int originalThreadId = Common.InputCallbackThreadID;
            var held = (HeldKeyChord)typeof(InputHook).GetField("easyMouseKeys", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var originalKeys = held.DownKeys; held.Clear();
            using var callback = new MouseWithoutBorders.FrmInputCallback();
            _ = callback.Handle; // A message owner only; no native hooks are installed.
            var hook = (InputHook)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputHook)); GC.SuppressFinalize(hook);
            try
            {
                Common.Hook = hook; Common.InputCallbackForm = callback; Common.InputCallbackThreadID = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
                Common.MachineName = "LOCAL-PC";
                var document = new MouseWithoutBordersSettings();
                document.Properties.SecurityKey.Value = "test-key";
                document.Properties.EasyMouse.Value = (int)EasyMouseOption.Custom;
                document.Properties.EasyMouseActivationKeys = [];
                string path = Path.Combine(folder, "prefs.json");
                PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
                Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                Setting.Values.Username = "chord-dialog-test";
                using var owner = new MouseWithoutBorders.FrmMatrix { ShowInTaskbar = false };
                // Show the real owner without starting its unrelated input/network timer.
                var shown = typeof(MouseWithoutBorders.FrmMatrix).GetMethod("FrmMatrix_Shown", BindingFlags.Instance | BindingFlags.NonPublic)!;
                owner.Shown -= (EventHandler)shown.CreateDelegate(typeof(EventHandler), owner);
                owner.Show();
                Assert.IsFalse(owner.TopMost);
                var tabs = Descendants(owner).OfType<TabControl>().Single();
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == Ui.Text("Other Options"));
                var edit = Descendants(owner).OfType<Button>().Single(button => button.Text == Ui.Text("Set keys…"));

                EditDialog(owner, edit, [Keys.ControlKey, Keys.Menu, Keys.F2], "Ctrl+Alt+F2", apply: true);
                CollectionAssert.AreEqual(new[] { (int)Keys.ControlKey, (int)Keys.Menu, (int)Keys.F2 }, Setting.Values.EasyMouseActivationKeys);
                Setting.Values.SaveSettingsSynchronously();
                CollectionAssert.AreEqual(new[] { (int)Keys.ControlKey, (int)Keys.Menu, (int)Keys.F2 }, PortableSettingsStore.Read(path).Properties.EasyMouseActivationKeys);

                EditDialog(owner, edit, [Keys.ControlKey, Keys.D1], "Ctrl+D1", apply: true);
                CollectionAssert.AreEqual(new[] { (int)Keys.ControlKey, (int)Keys.D1 }, Setting.Values.EasyMouseActivationKeys);

                EditDialog(owner, edit, [Keys.CapsLock], "Caps Lock", apply: true);
                StringAssert.Contains(edit.Text, "Caps Lock");
                CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, Setting.Values.EasyMouseActivationKeys);
                Setting.Values.SaveSettingsSynchronously();
                CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, PortableSettingsStore.Read(path).Properties.EasyMouseActivationKeys);

                using var reopened = new MouseWithoutBorders.FrmMatrix();
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, [EventArgs.Empty]);
                Assert.IsTrue(Descendants(reopened).OfType<Button>().Any(button => button.Text.StartsWith("Caps Lock", StringComparison.Ordinal)));

                EditDialog(owner, edit, [Keys.ShiftKey, Keys.F4], "Shift+F4", apply: false);
                CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, Setting.Values.EasyMouseActivationKeys);
                CollectionAssert.AreEqual(new[] { (int)Keys.CapsLock }, PortableSettingsStore.Read(path).Properties.EasyMouseActivationKeys);
            }
            finally
            {
                InputHook.KeyRecorder = null; InputHook.RecordingKeys = false;
                held.Clear(); foreach (int key in originalKeys) held.Update(key, true);
                Common.Hook = originalHook; Common.InputCallbackForm = originalCallback; Common.InputCallbackThreadID = originalThreadId;
                Setting.Values = original;
                Common.MachineName = originalName;
                Directory.Delete(folder, true);
            }
        });
    }

    private static void EditDialog(System.Windows.Forms.Form owner, Button edit, Keys[] replacement, string expected, bool apply)
    {
        Exception? failure = null;
        bool inspected = false;
        int stage = 0;
        Action<int, bool>? recorder = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<System.Windows.Forms.Form>().SingleOrDefault(form => form != owner && form.Modal);
            if (dialog == null) return;
            try
            {
                Assert.AreSame(owner, dialog.Owner);
                var input = Descendants(dialog).OfType<TextBox>().Single();
                Assert.IsTrue(InputHook.RecordingKeys);
                if (stage++ == 0)
                {
                    Assert.IsTrue(input.Focused, "The editor must receive keyboard focus when the modal opens.");
                    Assert.IsTrue(input.ReadOnly, "Keys are recorded directly rather than entered as names.");
                    Assert.AreEqual(ImeMode.Disable, input.ImeMode);
                    Assert.IsTrue(((Button)dialog.AcceptButton!).Enabled, "An empty assignment can be applied to clear the binding.");
                    Assert.AreEqual(input.TextLength, input.SelectionLength);
                    int[] current = Setting.Values.EasyMouseActivationKeys;
                    Assert.AreEqual(current.Length == 0 ? Ui.Text("None") : HeldKeyChord.Format(current), input.Text);
                    recorder = InputHook.KeyRecorder;
                    Assert.IsNotNull(recorder);
                    foreach (Keys key in replacement) recorder((int)key, true);
                    return;
                }
                Assert.AreEqual(expected, input.Text);
                Assert.IsTrue(((Button)dialog.AcceptButton!).Enabled);
                if (stage == 2)
                {
                    foreach (Keys key in replacement.Reverse()) recorder!((int)key, false);
                    return;
                }
                Assert.IsTrue(((Button)dialog.AcceptButton!).Focused, "Releasing all keys must make the Apply action keyboard accessible.");
                Assert.IsNull(InputHook.KeyRecorder, "Only the focused capture field consumes keys.");
                timer.Stop();
                inspected = true;
                Assert.IsTrue((bool)typeof(System.Windows.Forms.Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, [apply ? Keys.Enter : Keys.Escape])!);
            }
            catch (Exception error) { failure = error; timer.Stop(); dialog.Close(); }
        };
        timer.Start();
        edit.PerformClick();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.IsTrue(inspected, "The visible edit action must open the owned modal.");
        Assert.IsTrue(owner.Enabled);
        Assert.IsFalse(InputHook.RecordingKeys);
        Assert.IsNull(InputHook.KeyRecorder);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task OnSta(Action action)
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
        await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
