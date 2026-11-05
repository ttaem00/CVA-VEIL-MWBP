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
public sealed class CursorOverlayTests
{
    [TestMethod]
    public void MouseKeysInteropReadsTheExistingWindowsConfiguration()
    {
        var keys = new NativeMethods.MOUSEKEYS { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MOUSEKEYS>() };
        Assert.AreEqual(28u, keys.cbSize, "Win32 MOUSEKEYS contains seven DWORD-sized fields on supported architectures.");
        Assert.IsTrue(NativeMethods.SystemParametersInfoMouseKeys(0x36, keys.cbSize, ref keys, 0));
        Assert.AreEqual(0u, keys.dwReserved1);
        Assert.AreEqual(0u, keys.dwReserved2);
    }

    [TestMethod]
    public async Task ReturningToLocalInputDoesNotWaitForTheSettingsUi()
    {
        await OnSta(() =>
        {
            var originalMain = Common.MainForm;
            int originalThread = Common.UIThreadID;
            bool originalVisible = Common.MainFormVisible;
            using var main = new MouseWithoutBorders.FrmScreen();
            _ = main.Handle;
            Task? pending = null;
            try
            {
                Common.MainForm = main;
                Common.UIThreadID = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Common.MainFormVisible = true;
                pending = Task.Run(Helper.MainFormDot);
                // The real UI owns a handle but deliberately does not drain messages.
                bool returnedBeforeUi = pending.Wait(TimeSpan.FromMilliseconds(250));
                bool hideAlreadyRequested = !Common.MainFormVisible;
                Application.DoEvents();
                pending.GetAwaiter().GetResult();
                Assert.IsTrue(returnedBeforeUi, "Local input must not wait for the UI to hide its auxiliary window.");
                Assert.IsTrue(hideAlreadyRequested, "Following input must not enqueue the same cleanup while the UI is busy.");
            }
            finally
            {
                Application.DoEvents();
                pending?.GetAwaiter().GetResult();
                Common.MainForm = originalMain;
                Common.UIThreadID = originalThread;
                Common.MainFormVisible = originalVisible;
            }
        });
    }

    [TestMethod]
    public async Task ExpandedCursorClientDoesNotExposeAnOpaqueBackground()
    {
        await OnSta(() =>
        {
            using var cursor = new MouseWithoutBorders.FrmMouseCursor();
            // Observed on the installed 96-DPI process: 32px artwork in a
            // 136x39 native client. Every uncovered pixel must be keyed out.
            cursor.ClientSize = new Size(136, 39);
            using var pixels = new Bitmap(cursor.ClientSize.Width, cursor.ClientSize.Height);
            cursor.DrawToBitmap(pixels, cursor.ClientRectangle);
            Assert.AreEqual(cursor.TransparencyKey.ToArgb(), pixels.GetPixel(100, 20).ToArgb());
        });
    }

    [TestMethod]
    public async Task RemoteOverlayMovesHidesAndReusesItsWindowWithoutTakingInput()
    {
        await OnSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "mwb-cursor-overlay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var originalSettings = Setting.Values;
            var originalMain = Common.MainForm;
            int originalThreadId = Common.UIThreadID;
            var originalOverlay = Common.MouseCursorForm;
            MouseWithoutBorders.FrmMouseCursor? overlay = null;
            try
            {
                var document = new MouseWithoutBordersSettings();
                document.Properties.SecurityKey.Value = "test-key";
                document.Properties.DrawMouseCursor = true;
                string path = Path.Combine(folder, "prefs.json");
                PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
                Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                // Create the UI owner without showing its startup form or installing hooks.
                using var main = new MouseWithoutBorders.FrmScreen();
                _ = main.Handle;
                Common.MainForm = main;
                Common.UIThreadID = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Common.MouseCursorForm = null;

                CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
                Assert.IsNull(Common.MouseCursorForm, "Hiding before the first remote input must not create an overlay.");
                CustomCursor.RenderMouseCursor(new Point(100, 120), false);
                overlay = Common.MouseCursorForm;
                Assert.IsNotNull(overlay);
                Assert.AreSame(main, overlay.Owner);
                Assert.IsTrue(overlay.Visible);
                Assert.AreEqual(new Point(101, 121), overlay.Location);
                Assert.IsNotNull(overlay.BackgroundImage);
                Assert.IsTrue(overlay.TopMost);
                Assert.AreEqual(Color.Teal, overlay.TransparencyKey);
                Assert.IsTrue((bool)typeof(MouseWithoutBorders.FrmMouseCursor)
                    .GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!);
                var parameters = (CreateParams)typeof(MouseWithoutBorders.FrmMouseCursor)
                    .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
                Assert.AreEqual(0x08000020, parameters.ExStyle & 0x08000020, "The overlay must declare no-activation and transparent hit testing.");

                using (var covering = new PassiveWindow { TopMost = true, ShowInTaskbar = false, Bounds = new Rectangle(90, 110, 80, 80) })
                {
                    covering.Show();
                    NativeMethods.SetWindowPos(covering.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, 0x13);
                    Assert.IsFalse(IsAbove(overlay.Handle, covering.Handle), "The competing topmost window must cover the fallback before this update.");
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    CustomCursor.RenderMouseCursor(new Point(100, 120), false);
                    Assert.IsTrue(IsAbove(overlay.Handle, covering.Handle), "A later topmost window must not leave the fallback cursor hidden behind it.");
                    Assert.AreEqual(foreground, NativeMethods.GetForegroundWindow(), "Cursor rendering must not activate its window.");
                }

                CustomCursor.RenderMouseCursor(new Point(200, 220), true);
                Assert.IsFalse(overlay.Visible, "A visible Windows cursor must suppress the fallback window.");
                Assert.AreEqual(new Point(101, 121), overlay.Location, "Native-cursor updates must not move a second window.");
                CustomCursor.RenderMouseCursor(new Point(-240, 75), false);
                Assert.AreSame(overlay, Common.MouseCursorForm);
                Assert.AreEqual(new Point(-239, 76), overlay.Location);
                CustomCursor.RenderMouseCursor(new Point(int.MinValue, 75), false);
                Assert.IsFalse(overlay.Visible);
                Assert.IsFalse(overlay.IsDisposed);
                CustomCursor.RenderMouseCursor(new Point(110, 130), false);
                Assert.AreSame(overlay, Common.MouseCursorForm);
                Assert.IsTrue(overlay.Visible);

                Setting.Values.DrawMouse = false;
                CustomCursor.RenderMouseCursor(new Point(140, 160), false);
                Assert.IsFalse(overlay.Visible);
                Assert.AreEqual(new Point(111, 131), overlay.Location, "Disabled fallback rendering must not move the hidden overlay.");
                var queued = typeof(MouseWithoutBorders.FrmScreen).GetField("cursorUpdatePending", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Task.Run(() =>
                {
                    for (int n = 0; n < 10000; n++) CustomCursor.ShowFakeMouseCursor(140 + n, 160);
                }).GetAwaiter().GetResult();
                Assert.IsFalse((bool)queued.GetValue(main)!, "Disabled cursor movement must not post work to the UI thread.");
                Setting.Values.DrawMouse = true;
                CustomCursor.RenderMouseCursor(new Point(140, 160), false);
                Assert.AreSame(overlay, Common.MouseCursorForm);
                Assert.IsTrue(overlay.Visible);
                Assert.AreEqual(new Point(141, 161), overlay.Location);

                int moves = 0;
                overlay.LocationChanged += (_, _) => moves++;
                // Hold the actual UI thread while the receiver sends a burst, then drain it.
                Task.Run(() =>
                {
                    for (int i = 0; i < 10000; i++) CustomCursor.ShowFakeMouseCursor(i, i + 10);
                }).GetAwaiter().GetResult();
                Application.DoEvents();
                var native = new NativeMethods.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CURSORINFO>() };
                bool nativeShowing = NativeMethods.GetCursorInfo(ref native) && (native.flags & 1) != 0;
                if (nativeShowing)
                {
                    Assert.IsFalse(overlay.Visible);
                    Assert.AreEqual(0, moves, "Visible native cursors must not generate overlay window moves.");
                }
                else
                {
                    Assert.AreEqual(new Point(10000, 10010), overlay.Location);
                    Assert.AreEqual(1, moves, "Old packet positions must not fill the settings UI queue.");
                }

                Task.Run(() => CustomCursor.ShowFakeMouseCursor(170, 180)).GetAwaiter().GetResult();
                Setting.Values.DrawMouse = false;
                Application.DoEvents();
                Assert.IsFalse(overlay.Visible, "A move queued before disabling must not revive the overlay.");
                Setting.Values.DrawMouse = true;
                CustomCursor.RenderMouseCursor(new Point(170, 180), false);
                Setting.Values.DrawMouse = false;
                CustomCursor.ShowFakeMouseCursor(int.MinValue, 75);
                Assert.IsFalse(overlay.Visible, "The explicit hide command must still work while disabled.");
                Setting.Values.DrawMouse = true;

                // Hook reinstallation retires its callback while the UI and cursor remain alive.
                using (var callback = new MouseWithoutBorders.FrmInputCallback())
                {
                    _ = callback.Handle;
                    callback.Close();
                    Assert.IsFalse(overlay.IsDisposed);
                }
                main.Tag = "test";
                main.Close();
                Assert.IsTrue(overlay.IsDisposed, "Retiring the UI owner must dispose its overlay.");
                Assert.IsNull(Common.MouseCursorForm);
                using var replacementMain = new MouseWithoutBorders.FrmScreen();
                _ = replacementMain.Handle;
                Common.MainForm = replacementMain;
                CustomCursor.RenderMouseCursor(new Point(180, 200), false);
                var replacementOverlay = Common.MouseCursorForm;
                Assert.IsNotNull(replacementOverlay);
                Assert.AreNotSame(overlay, replacementOverlay);
                overlay = replacementOverlay;
                Assert.AreSame(replacementMain, overlay.Owner);
                Assert.IsTrue(overlay.Visible);
                Assert.AreEqual(new Point(181, 201), overlay.Location);
            }
            finally
            {
                overlay?.Dispose();
                Common.MouseCursorForm = originalOverlay;
                Common.MainForm = originalMain;
                Common.UIThreadID = originalThreadId;
                Setting.Values = originalSettings;
                Directory.Delete(folder, true);
            }
        });
    }

    private sealed class PassiveWindow : System.Windows.Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    private static bool IsAbove(IntPtr top, IntPtr bottom)
    {
        for (var window = GetWindow(bottom, 3); window != IntPtr.Zero; window = GetWindow(window, 3))
            if (window == top) return true;
        return false;
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
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
