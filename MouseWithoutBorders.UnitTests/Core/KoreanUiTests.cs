using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class KoreanUiTests
{
    [TestMethod]
    public async Task KoreanSettingsKeepKeyChoicesAndEditableDataIndependentOfCaptions()
    {
        var done = new TaskCompletionSource();
        var thread = new System.Threading.Thread(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "mwb-ko-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var original = Setting.Values;
            string originalName = Common.MachineName;
            var originalTransport = Common.Sk;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
                Common.MachineName = "LOCAL-PC";
                var document = new MouseWithoutBordersSettings();
                document.Properties.SecurityKey.Value = "test-key";
                string path = Path.Combine(folder, "prefs.json");
                PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
                Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), false);
                Setting.Values.Username = "korean-ui-test";
                using var form = new MouseWithoutBorders.FrmMatrix();
                typeof(MouseWithoutBorders.FrmMatrix).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, [EventArgs.Empty]);
                var tabs = form.Controls.OfType<TabControl>().Single();
                Assert.IsTrue(tabs.TabPages.Cast<TabPage>().Any(t => t.Text == "옵션"));
                var connectionHeader = (GroupBox)typeof(MouseWithoutBorders.FrmMatrix).GetField("groupBoxKeySetup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                Assert.AreEqual("PC 연결", connectionHeader.Text);
                Assert.AreEqual("PC 찾기…", connectionHeader.Controls.Find("findComputers", true).Single().Text);
                Assert.IsNull(typeof(MouseWithoutBorders.FrmMatrix).GetField("textBoxEnc", BindingFlags.Instance | BindingFlags.NonPublic),
                    "Approval-based settings must not retain a hidden credential field.");
                var hideCursor = (CheckBox)typeof(MouseWithoutBorders.FrmMatrix).GetField("checkBoxHideMouse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                Assert.AreEqual("다른 PC 조작 중 커서 숨기기(&H)", hideCursor.Text);
                var choices = (ComboBox)typeof(MouseWithoutBorders.FrmMatrix).GetField("comboBoxEasyMouseOption", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                choices.SelectedItem = EasyMouseOption.Alt;
                Assert.AreEqual((int)EasyMouseOption.Alt, Setting.Values.EasyMouse);
                Setting.Values.SaveSettingsSynchronously();
                Assert.AreEqual((int)EasyMouseOption.Alt, PortableSettingsStore.Read(path).Properties.EasyMouse.Value);
                Assert.AreEqual("Alt를 누를 때", choices.GetItemText(EasyMouseOption.Alt));
                using var data = new TextBox { Text = "LOCAL-PC" };
                Ui.Apply(data);
                Assert.AreEqual("LOCAL-PC", data.Text);
                using var peer = new MouseWithoutBorders.Machine { MachineName = "REMOTE-PC", MachineEnabled = true };
                peer.SetConnectionStatus(SocketStatus.Connected, SocketStatus.InvalidKey);
                Assert.AreEqual("● 연결됨", peer.DisplayStatus, "One authenticated direction is sufficient for input and file endpoint selection.");
                Assert.IsFalse(peer.RequiresReconnect, "The approval warning must agree with the connected status.");
                Assert.AreEqual(SocketStatus.InvalidKey, peer.StatusServer, "Display priority must not erase the failed socket's diagnostic state.");
                peer.SetConnectionStatus(SocketStatus.InvalidKey, SocketStatus.Connected);
                Assert.AreEqual("● 연결됨", peer.DisplayStatus);
                Assert.IsFalse(peer.RequiresReconnect);
                peer.SetConnectionStatus(SocketStatus.InvalidKey, SocketStatus.NA);
                Assert.AreEqual("● 다시 연결 필요", peer.DisplayStatus, "A PC with no authenticated connection must still show the failure.");
                Assert.IsTrue(peer.RequiresReconnect);
                var peerHelp = (ToolTip)typeof(MouseWithoutBorders.Machine).GetField("toolTipHelp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer)!;
                StringAssert.Contains(peerHelp.GetToolTip(peer.NameEditor), "PC 찾기");
                StringAssert.Contains(peerHelp.GetToolTip(peer.NameEditor), "양쪽 PC에서 승인");
                peer.SetConnectionStatus(SocketStatus.Connected, SocketStatus.NA);
                Assert.AreEqual("● 연결됨", peer.DisplayStatus);
                var configured = (MouseWithoutBorders.Machine[])typeof(MouseWithoutBorders.FrmMatrix)
                    .GetField("machines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                configured[^1].MachineName = "REMOTE-PC"; configured[^1].MachineEnabled = true;
                using var connected = new TcpSk(true, null!, SocketStatus.Connected, "REMOTE-PC");
                using var rejected = new TcpSk(false, null!, SocketStatus.InvalidKey, "REMOTE-PC");
                var transport = (SocketStuff)RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
                GC.SuppressFinalize(transport);
                transport.TcpSocketsLock = new object();
                typeof(SocketStuff).GetProperty("TcpSockets", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(transport, new List<TcpSk> { connected, rejected });
                Common.Sk = transport;
                var refresh = typeof(MouseWithoutBorders.FrmMatrix).GetMethod("RefreshMatrixConnections", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.AreEqual(string.Empty, refresh.Invoke(form, null), "A working direction must not generate an approval-mismatch warning.");
                connected.Status = SocketStatus.NA;
                StringAssert.Contains((string)refresh.Invoke(form, null)!, "[REMOTE-PC]", "A genuinely unauthenticated peer must remain actionable.");
                Assert.AreEqual("완료", Ui.Text("Completed"));
                StringAssert.Contains(TransferCenter.TitleSummary([new TransferJob { State = "Paused" }]), "일시정지");
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
            finally
            {
                Setting.Values = original; Common.MachineName = originalName; Common.Sk = originalTransport;
                Directory.Delete(folder, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
