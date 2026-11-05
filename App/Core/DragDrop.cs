// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.PowerToys.Telemetry;
using MouseWithoutBorders.Class;

// <summary>
//     Drag/Drop implementation.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
namespace MouseWithoutBorders.Core;

/* Common.DragDrop.cs
 * Drag&Drop is one complicated implementation of the tool with some tricks.
 *
 * SEQUENCE OF EVENTS:
 * DragDropStep01: MachineX: Remember mouse down state since it could be a start of a dragging
 * DragDropStep02: MachineY: Send a message to the MachineX to ask it to check if it is
 *                           doing drag/drop
 * DragDropStep03: MachineX: Got explorerDragDrop, send WM_CHECK_EXPLORER_DRAG_DROP to its mainForm
 * DragDropStep04: MachineX: Show Mouse Without Borders Helper form at mouse cursor to get DragEnter event.
 * DragDropStepXX: MachineX: Mouse Without Borders Helper: Called by DragEnter, check if dragging a single file,
 *                           remember the file (set as its window caption)
 * DragDropStep05: MachineX: Get the file name from Mouse Without Borders Helper, hide Mouse Without Borders Helper window
 * DragDropStep06: MachineX: Broadcast a message saying that it has some drag file.
 * DragDropStep08: MachineY: Got ClipboardDragDrop, isDropping set, get the MachineX name from the package.
 * DragDropStep09: MachineY: Since isDropping is true, show up the drop form (looks like an icon).
 * DragDropStep10: MachineY: MouseUp, set isDropping to false, hide the drop "icon" and get data.
 * DragDropStep11: MachineX: Mouse move back without drop event, cancelling drag/dop
 *                           SendClipboardBeatDragDropEnd
 * DragDropStep12: MachineY: Hide the drop "icon" when received ClipboardDragDropEnd.
 *
 * FROM VERSION 1.6.3: Drag/Drop is temporary removed, Drop action cannot be done from a lower integrity app to a higher one.
 * We have to run a helper process...
 * http://forums.microsoft.com/MSDN/ShowPost.aspx?PageIndex=1&SiteID=1&PageID=1&PostID=736086
 *
 * 2008.10.28: Trying to restore the Drag/Drop feature by adding the drag/drop helper process. Coming in version
 * 1.6.5
 * */

internal static partial class DragDrop
{
    internal const int GestureMarker = 0x4D574247;
    internal const int CaptureContextMessage = 0x40A;
    private const int BeginGesture = 0, ReleaseGesture = 1, CancelGesture = 2;
    private sealed record ControllerGesture(ID Source, int Number);
    private sealed class SourceGesture(ID controller, int number, ID target)
    {
        internal readonly ID Controller = controller;
        internal readonly int Number = number;
        internal ID Target = target;
        internal int Offer;
        internal bool Released, Cancelled, Captured;
    }
    private static int gestureNumber;
    private static ControllerGesture controllerGesture;
    private static SourceGesture sourceGesture;
    private static bool HasPendingGesture => controllerGesture != null || sourceGesture is { Cancelled: false, Released: false };
    private static bool isDragging;

    internal static bool IsDragging
    {
        get => DragDrop.isDragging;
        set => DragDrop.isDragging = value;
    }

    internal static void DragDropStep01(int wParam)
    {
        if (wParam is not (WM.WM_LBUTTONDOWN or WM.WM_LBUTTONUP) || !Setting.Values.TransferFile)
        {
            return;
        }

        if (wParam == WM.WM_LBUTTONDOWN)
        {
            gestureNumber = Interlocked.Increment(ref Package.PackageID);
            controllerGesture = null;
            MouseDown = true;
            DragMachine = MachineStuff.desMachineID;
            MachineStuff.dropMachineID = ID.NONE;
            Logger.LogDebug("DragDropStep01: MouseDown");
        }
        else if (wParam == WM.WM_LBUTTONUP)
        {
            MouseDown = false;
            Logger.LogDebug("DragDropStep01: MouseUp");
        }

    }

    internal static void DragDropStep02()
    {
        controllerGesture ??= new ControllerGesture(DragMachine, gestureNumber);
        SendGesture(controllerGesture, MachineStuff.newDesMachineID, BeginGesture);
    }

    internal static void DragDropStep03(DATA package, ID controller)
    {
        if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
        {
            return;
        }

        // Unmarked old requests have no gesture identity; ordinary clipboard traffic is independent.
        if (package.Des != Common.MachineID || package.Machine3 != (ID)GestureMarker || package.Machine1 == ID.NONE
            || controller is ID.NONE or ID.ALL || package.Src is ID.NONE or ID.ALL) return;
        int command = (int)package.Machine2;
        SourceGesture intent;
        int revoked = 0;
        bool revokeSelection = true;
        ID previousTarget = ID.NONE;
        lock (cancelSync)
        {
            intent = sourceGesture;
            bool same = intent != null && intent.Controller == controller && intent.Number == (int)package.Machine1;
            if (command == BeginGesture)
            {
                if (same && (intent.Cancelled || intent.Released)) return;
                if (!same)
                {
                    if (!MouseDown) return;
                    if (intent is { Released: false })
                    { intent.Cancelled = true; revoked = intent.Offer; previousTarget = intent.Target; }
                    intent = sourceGesture = new SourceGesture(controller, (int)package.Machine1, package.Src);
                }
            }
            else if (!same || intent.Cancelled || intent.Released || command is not (ReleaseGesture or CancelGesture)) return;
            if (intent.Target != package.Src || command == CancelGesture)
            {
                revoked = intent.Offer; previousTarget = intent.Target;
                if (revoked != 0 && command != CancelGesture)
                {
                    intent.Offer = QueuedFileTransfer.RetargetOffer(revoked);
                    offeredFiles = intent.Offer;
                    intent.Cancelled = intent.Offer == 0;
                    revokeSelection = false;
                }
                else intent.Offer = 0;
            }
            intent.Target = package.Src;
            intent.Released = command == ReleaseGesture;
            intent.Cancelled |= command == CancelGesture;
            if (intent.Released || intent.Cancelled) IsDragging = false;
        }
        if (revoked != 0) CloseGestureOffer(previousTarget, revoked, revokeSelection);
        if (intent.Cancelled) return;
        if (intent.Offer == 0)
        {
            if (command == BeginGesture) QueueCapture(intent);
        }
        else SendGestureOffer(intent);
    }

    private static void SendGesture(ControllerGesture request, ID target, int command)
    {
        var packet = new DATA { Type = PackageType.ExplorerDragDrop, Src = target, Des = request.Source,
            Machine1 = (ID)request.Number, Machine2 = (ID)command, Machine3 = (ID)GestureMarker, MachineName = Common.MachineName };
        if (request.Source == Common.MachineID) DragDropStep03(packet, Common.MachineID);
        else Common.SkSend(packet, null, false);
    }

    private static void QueueCapture(SourceGesture intent) => Common.DoSomethingInUIThread(() =>
        NativeMethods.PostMessage(Common.MainForm.Handle, NativeMethods.WM_CHECK_EXPLORER_DRAG_DROP,
            new IntPtr((long)(uint)intent.Controller), new IntPtr(intent.Number)));

    internal static void DragDropStep04(ID controller, int gesture)
    {
        SourceGesture intent;
        lock (cancelSync)
        {
            intent = sourceGesture;
            if (intent == null || intent.Controller != controller || intent.Number != gesture
                || intent.Cancelled || intent.Captured) return;
        }
        if (!IsDropping)
        {
            IntPtr h = (IntPtr)NativeMethods.FindWindow(null, Helper.HELPER_FORM_TEXT);
            if (h != IntPtr.Zero)
            {
                _ = NativeMethods.SendMessage(h, CaptureContextMessage, new IntPtr((long)(uint)controller), new IntPtr(gesture));

                Common.MainForm.Hide();
                Common.MainFormVisible = false;

                Point p = default;

                // NativeMethods.SetWindowText(h, "");
                _ = NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_SHOWWINDOW);

                for (int i = -10; i < 10; i++)
                {
                    lock (cancelSync)
                        if (intent != sourceGesture || intent.Cancelled || intent.Captured) break;

                    _ = NativeMethods.GetCursorPos(ref p);
                    Logger.LogDebug("DragDropStep04: Moving Mouse Without Borders Helper to (" + p.X.ToString(CultureInfo.CurrentCulture) + ", " + p.Y.ToString(CultureInfo.CurrentCulture) + ")");
                    _ = NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, p.X - 100 + i, p.Y - 100 + i, 200, 200, 0);
                    _ = NativeMethods.SendMessage(h, 0x000F, IntPtr.Zero, IntPtr.Zero); // WM_PAINT
                    Thread.Sleep(20);
                    Application.DoEvents();

                    // if (GetText(h).Length > 1) break;
                }
            }
            else
            {
                Logger.LogDebug("DragDropStep04: Mouse without Borders Helper not found!");
            }
        }
        else
        {
            Logger.LogDebug("DragDropStep04: IsDropping == true, skip checking");
        }

        Logger.LogDebug("DragDropStep04: Got WM_CHECK_EXPLORER_DRAG_DROP, done with processing jump to DragDropStep05...");
    }

    private static int offeredFiles;
    private static int incomingFiles;
    private static ID incomingFileSource;
    internal static bool IsIncomingOffer(int id) => IsDropping && incomingFiles == id;

    internal static void OfferAccepted(int id)
    {
        if (offeredFiles == id) IsDragging = false;
    }

    internal static void DragDropFiles(string[] paths, ID controller, int gesture)
    {
        SourceGesture intent;
        lock (cancelSync)
        {
            intent = sourceGesture;
            if (intent == null || intent.Controller != controller || intent.Number != gesture || intent.Cancelled || intent.Captured) return;
            intent.Captured = true;
        }
        PublishGestureSelection(intent, paths);
    }

    private static void PublishGestureSelection(SourceGesture intent, string[] paths)
    {
        try
        {
            int offer = QueuedFileTransfer.Offer(paths);
            lock (cancelSync)
            {
                if (intent.Cancelled || intent.Offer != 0 || intent != sourceGesture && !intent.Released)
                { QueuedFileTransfer.RevokeOffer(offer); return; }
                intent.Offer = offer;
                if (intent == sourceGesture)
                {
                    offeredFiles = offer;
                    IsDragging = !intent.Released;
                    MachineStuff.dropMachineID = intent.Target;
                }
            }
            SendGestureOffer(intent);
        }
        catch (Exception error) { Logger.Log(error); Common.ShowToolTip(error.Message, 5000, ToolTipIcon.Warning); }
    }

    private static DATA GestureOffer(SourceGesture intent) => new()
    {
        Type = PackageType.ClipboardDragDropOperation, Src = Common.MachineID, Des = intent.Target,
        MachineName = Common.MachineName, Machine1 = intent.Released ? (ID)1 : ID.NONE,
        Machine2 = (ID)intent.Offer, Machine3 = (ID)QueuedFileTransfer.Marker,
    };

    private static void SendGestureOffer(SourceGesture intent)
    {
        DATA packet;
        lock (cancelSync)
        {
            if (intent.Cancelled || intent.Offer == 0) return;
            packet = GestureOffer(intent);
        }
        Common.SkSend(packet, null, false);
    }

    private static void CloseGestureOffer(ID target, int offer, bool revokeSelection)
    {
        if (revokeSelection) QueuedFileTransfer.RevokeOffer(offer);
        var packet = new DATA { Type = PackageType.ClipboardDragDropEnd, Src = Common.MachineID, Des = target,
            Machine1 = Common.MachineID, Machine2 = (ID)offer, Machine3 = (ID)CancelDragMarker };
        _ = Task.Run(() => Common.SkSend(packet, null, false));
    }

    internal static void DragDropStep08(DATA package)
    {
        Receiver.GetNameOfMachineWithClipboardData(package);
        Logger.LogDebug("DragDropStep08: ClipboardDragDrop Received. machine with drag file was set");
    }

    internal static void DragDropStep08_2(DATA package)
    {
        if (package.Des == Common.MachineID && !Common.RunOnLogonDesktop && !Common.RunOnScrSaverDesktop)
        {
            int nextOffer = package.Machine3 == (ID)QueuedFileTransfer.Marker ? (int)package.Machine2 : 0;
            if (package.Machine3 == (ID)QueuedFileTransfer.Marker && package.Machine1 == (ID)1 && nextOffer != 0)
            {
                DragDropStep10(nextOffer, package.Src, package.Machine4 == (ID)1);
                return;
            }
            lock (cancelSync)
            {
                if (WasDragCancelled(package.Src, nextOffer)) return;
                if (incomingFiles != 0 && (incomingFiles != nextOffer || incomingFileSource != package.Src))
                    RememberCancelledDrag(incomingFileSource, incomingFiles, final: false);
                dragCancelledUntilPress = false;
                incomingFiles = nextOffer;
                incomingFileSource = package.Src;
                IsDropping = true;
            }
            var source = MachineStuff.MachinePool.TryFindMachineByID(incomingFileSource);
            if (incomingFiles != 0 && source.Count > 0) DurableTransfers.Preview(source[0].Name.Trim(), incomingFiles);
            Common.DoSomethingInUIThread(() =>
            {
                if (IsDropping) _ = NativeMethods.PostMessage(Common.MainForm.Handle, NativeMethods.WM_SHOW_DRAG_DROP, IntPtr.Zero, IntPtr.Zero);
            });
            MachineStuff.dropMachineID = Common.MachineID;
            Logger.LogDebug("DragDropStep08_2: ClipboardDragDropOperation Received. IsDropping set");
        }
    }

    internal static void DragDropStep09(int wParam)
    {
        if (wParam == WM.WM_LBUTTONUP)
        {
            var request = Interlocked.Exchange(ref controllerGesture, null);
            if (request != null) SendGesture(request, Common.DesMachineID, ReleaseGesture);
        }
        if (wParam == WM.WM_MOUSEMOVE && IsDropping)
        {
            // Show/Move form
            Common.DoSomethingInUIThread(() =>
            {
                _ = NativeMethods.PostMessage(Common.MainForm.Handle, NativeMethods.WM_SHOW_DRAG_DROP, (IntPtr)0, (IntPtr)0);
            });
        }
        else if (wParam == WM.WM_LBUTTONUP)
        {
            ID source;
            lock (cancelSync)
            {
                // Modern offers commit only through their original controller's source relay.
                if (!IsDropping || incomingFiles != 0) return;
                source = incomingFileSource;
            }
            DragDropStep10(0, source);
        }
    }

    internal static void PeerDisconnected(SocketStuff owner, ID peer)
    {
        lock (cancelSync)
        {
            if (!ReferenceEquals(Common.Sk, owner) || !IsDropping || incomingFileSource != peer) return;
            RememberCancelledDrag(peer, incomingFiles, final: false);
            IsDropping = false;
            incomingFiles = 0;
            incomingFileSource = ID.NONE;
        }
        Common.DoSomethingInUIThread(() =>
        {
            if (!IsDropping) _ = NativeMethods.SendMessage(Common.MainForm.Handle, NativeMethods.WM_HIDE_DRAG_DROP, IntPtr.Zero, IntPtr.Zero);
        });
    }

    private static void DragDropStep10(int offer, ID sourceId, bool useReceivingFolder = false)
    {
        Logger.LogDebug("DragDropStep10: Hide the form and get data...");
        lock (cancelSync)
        {
            if (offer != 0)
            {
                if (WasDragCancelled(sourceId, offer, finalOnly: true)) return;
                RememberCancelledDrag(sourceId, offer); // Also prevents a completed offer's late preview from reviving it.
            }
            if (incomingFiles == offer && incomingFileSource == sourceId)
            {
                IsDropping = IsDragging = false;
                incomingFiles = 0;
            }
        }

        Common.DoSomethingInUIThread(() =>
        {
            if (!IsDropping) _ = NativeMethods.PostMessage(Common.MainForm.Handle, NativeMethods.WM_HIDE_DRAG_DROP, (IntPtr)0, (IntPtr)0);
        });

        PowerToysTelemetry.Log.WriteEvent(new MouseWithoutBorders.Telemetry.MouseWithoutBordersDragAndDropEvent());
        if (offer != 0)
        {
            Common.DoSomethingInUIThread(() =>
            {
                try
                {
                    var sources = MachineStuff.MachinePool.TryFindMachineByID(sourceId);
                    if (sources.Count == 0) throw new IOException("The sending PC disconnected.");
                    string peer = sources[0].Name.Trim();
                    var preparation = DurableTransfers.BeginPreparation(offer, peer, false);
                    string folder;
                    try
                    {
                        folder = useReceivingFolder
                            ? TransferReceivePreferences.PrepareFallback(Setting.Values.DefaultReceivingFolder, TransferReceivePreferences.DesktopFolder)
                            : TransferDropDestination.ResolveAndOpen();
                    }
                    catch (Exception error) { preparation.Fail(error); throw; }
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            preparation.Token.ThrowIfCancellationRequested();
                            DurableTransfers.RequestPreparedOffer(preparation, folder);
                        }
                        catch (Exception error) { preparation.Fail(error); Logger.Log("Transfer unavailable: " + error.Message); Common.ShowToolTip(error.Message, 5000, ToolTipIcon.Error); }
                    });
                }
                catch (Exception error) { Logger.Log(error); Common.ShowToolTip(error.Message, 5000, ToolTipIcon.Error); }
            });
        }
        else Clipboard.GetRemoteClipboard("desktop");
    }

    internal static void DragDropStep11()
    {
        CancelControllerGesture();
        Logger.LogDebug("DragDropStep11: Mouse drag coming back, canceling drag/drop");
        IsDropping = false;
        IsDragging = false;
        DragMachine = (ID)1;
        MouseDown = false;
    }

    internal static void DragDropStep12()
    {
        Logger.LogDebug("DragDropStep12: ClipboardDragDropEnd received");
        IsDropping = false;
        Clipboard.LastIDWithClipboardData = ID.NONE;

        Common.DoSomethingInUIThread(() =>
        {
            _ = NativeMethods.PostMessage(Common.MainForm.Handle, NativeMethods.WM_HIDE_DRAG_DROP, (IntPtr)0, (IntPtr)0);
        });
    }

    private static void CancelControllerGesture()
    {
        var request = Interlocked.Exchange(ref controllerGesture, null);
        ID target = Common.DesMachineID;
        if (request != null) _ = Task.Run(() => SendGesture(request, target, CancelGesture));
    }

    internal static void ChangeDropMachine()
    {
        var request = controllerGesture;
        if (request != null) SendGesture(request, MachineStuff.newDesMachineID, BeginGesture);
    }

    private static bool isDropping;
    private static ID dragMachine;

    internal static ID DragMachine
    {
        get => DragDrop.dragMachine;
        set => DragDrop.dragMachine = value;
    }

    internal static bool IsDropping
    {
        get => DragDrop.isDropping;
        set => DragDrop.isDropping = value;
    }

    internal static bool MouseDown { get; set; }
}
