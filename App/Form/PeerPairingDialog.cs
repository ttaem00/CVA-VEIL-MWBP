// Copyright (c) Mouse Without Borders Portable contributors.
// Licensed under the MIT license.
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal static class PeerPairingDialog
{
    internal static Task<bool> ConfirmAsync(PairingRequest request, CancellationToken token, SynchronizationContext context)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            if (token.IsCancellationRequested) { result.TrySetResult(false); return; }
            try
            {
                var owner = System.Windows.Forms.Form.ActiveForm ?? Common.MainForm;
                if (owner?.IsDisposed == true) owner = null;
                using var dialog = new System.Windows.Forms.Form
                {
                    Text = "PC 연결 확인 · Mouse Without Borders Plus",
                    ClientSize = new Size(500, 350), Font = SystemFonts.MessageBoxFont,
                    AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
                    StartPosition = owner?.Visible == true ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                    ShowInTaskbar = false, BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
                };
                using var headingFont = new Font(dialog.Font.FontFamily, 17, FontStyle.Bold);
                using var codeFont = new Font("Consolas", 36, FontStyle.Bold);
                var heading = new Label
                {
                    Text = "같은 확인 코드인가요?", Font = headingFont,
                    Bounds = new Rectangle(24, 22, 452, 38),
                };
                var peer = new Label
                {
                    Text = $"연결할 PC: {request.Name}\nIP 주소: {request.Address}",
                    Bounds = new Rectangle(24, 70, 452, 48),
                };
                var code = new Label
                {
                    Text = request.Code, Font = codeFont, TextAlign = ContentAlignment.MiddleCenter,
                    Bounds = new Rectangle(24, 126, 452, 70), AccessibleName = "확인 코드 " + request.Code,
                };
                var instruction = new Label
                {
                    Text = "양쪽 PC에 표시된 6자리 숫자를 비교해 주세요.\n같을 때만 두 PC에서 연결을 승인하세요.\n승인하면 마우스·키보드와 파일을 공유합니다.",
                    Bounds = new Rectangle(24, 210, 452, 66),
                };
                var cancel = new Button
                {
                    Text = "취소", DialogResult = DialogResult.Cancel, TabIndex = 0,
                    Bounds = new Rectangle(240, 294, 92, 34),
                };
                var approve = new Button
                {
                    Text = "같은 코드 · 연결 승인", DialogResult = DialogResult.OK, TabIndex = 1,
                    Bounds = new Rectangle(344, 294, 132, 34),
                };
                dialog.Controls.AddRange([heading, peer, code, instruction, cancel, approve]);
                // Enter starts on Cancel; approval requires selecting the approval button.
                dialog.AcceptButton = cancel; dialog.CancelButton = cancel;
                dialog.Shown += (_, _) =>
                {
                    cancel.Focus(); dialog.Activate(); dialog.BringToFront();
                    _ = NativeMethods.SetForegroundWindow(dialog.Handle);
                };
                using var cancellation = token.Register(() => context.Post(_ =>
                {
                    if (!dialog.IsDisposed) { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
                }, null));
                if (token.IsCancellationRequested) { result.TrySetResult(false); return; }
                result.TrySetResult(dialog.ShowDialog(owner) == DialogResult.OK && !token.IsCancellationRequested);
            }
            catch (Exception error) { result.TrySetException(error); }
        }, null);
        return result.Task;
    }
}
