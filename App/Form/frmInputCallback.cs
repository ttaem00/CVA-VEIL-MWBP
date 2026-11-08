// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// <summary>
//     Keyboard/Mouse hook callbacks are handled in the message loop of this form.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

[module: SuppressMessage("Microsoft.Globalization", "CA1300:SpecifyMessageBoxOptions", Scope = "member", Target = "MouseWithoutBorders.frmInputCallback.#InstallKeyboardAndMouseHook()", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders
{
    internal partial class FrmInputCallback : System.Windows.Forms.Form
    {
        internal FrmInputCallback()
        {
            InitializeComponent();
        }

        private void FrmInputCallback_Load(object sender, EventArgs e)
        {
            InstallKeyboardAndMouseHook();
            if (!InputHook.ObserveDevices(Handle)) Logger.Log("Raw input health observation could not be registered.");
            if (!RegisterRecoveryHotkey()) Logger.Log("Windows emergency input hotkey is unavailable; another application may own it.");
            Left = 0;
            Top = 0;
            Width = 0;
            Height = 0;
        }

        private void FrmInputCallback_Shown(object sender, EventArgs e)
        {
            Common.InputCallbackForm = this;
            Visible = false;
        }

        private void FrmInputCallback_FormClosing(object sender, FormClosingEventArgs e)
        {
            InputHook.ObserveDevices(IntPtr.Zero);
            UnregisterHotKey(Handle, RecoveryHotkeyId);
            if (e.CloseReason != CloseReason.WindowsShutDown)
            {
                // e.Cancel = true;
            }
        }

        private int healthCheckPending;
        private int forceRecovery;
        internal const int RecoveryHotkeyId = 0x4d57;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        internal bool RegisterRecoveryHotkey() => RegisterHotKey(Handle, RecoveryHotkeyId, 0x4003, 8);

        internal void CheckInputHealth(bool force = false)
        {
            if (force) Interlocked.Exchange(ref forceRecovery, 1);
            if (IsDisposed || Interlocked.Exchange(ref healthCheckPending, 1) != 0) return;
            try
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        bool requested = Interlocked.Exchange(ref forceRecovery, 0) != 0;
                        if (ReferenceEquals(Common.InputCallbackForm, this)) Common.Hook?.RecoverMissedInput(requested);
                    }
                    finally { Interlocked.Exchange(ref healthCheckPending, 0); }
                });
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref healthCheckPending, 0); }
        }

        internal void NotifyInputPeerFailure(SocketStuff owner, uint peer)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    // Recheck on the hook thread: the user may already have
                    // switched to another peer while the failed writer stopped.
                    if (ReferenceEquals(Common.InputCallbackForm, this) && ReferenceEquals(Common.Sk, owner)
                        && (uint)Common.DesMachineID == peer && !Common.IsConnectedTo((ID)peer))
                        Event.ReturnToLocalControl(Common.LastPos);
                });
            }
            catch (InvalidOperationException) { } // Closing/replacing this input desktop.
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x00ff && ReferenceEquals(Common.InputCallbackForm, this))
                Common.Hook?.ObserveRawInput(message.LParam);
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == RecoveryHotkeyId)
                CheckInputHealth(force: true);
            base.WndProc(ref message); // DefWindowProc owns WM_INPUT cleanup.
        }

        internal void InstallKeyboardAndMouseHook()
        {
            /*
             * Install hooks:
             * According to http://msdn.microsoft.com/en-us/library/ms644986(VS.85).aspx
             * we need to process the hook callback message fast enough so make the message loop thread a
             * high priority thread. (Mouse/Keyboard event is important)
             * */
            try
            {
                Common.Hook = new InputHook();
                Common.Hook.MouseEvent += new InputHook.MouseEvHandler(Event.MouseEvent);
                Common.Hook.KeyboardEvent += new InputHook.KeybdEvHandler(Event.KeybdEvent);

                Logger.Log("(((((Keyboard/Mouse hooks installed/reinstalled!)))))");
                /* The hook is called in the context of the thread that installed it.
                 * The call is made by sending a message to the thread that installed the hook.
                 * Therefore, the thread that installed the hook must have a message loop!!!
                 * */
            }
            catch (Win32Exception e)
            {
                _ = MessageBox.Show(
                    "Error installing Mouse/keyboard hook, error code: " + e.ErrorCode,
                    Application.ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Common.MainForm.Quit(false, false); // we are [STAThread] :)
            }

            // Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
    }
}
