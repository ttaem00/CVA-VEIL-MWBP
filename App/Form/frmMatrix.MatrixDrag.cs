// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmMatrix
{
    private MatrixSurface matrixSurface;

    private string RefreshMatrixConnections()
    {
        var client = new SocketStatus[machines.Length];
        var server = new SocketStatus[machines.Length];
        Array.Fill(client, SocketStatus.NA); Array.Fill(server, SocketStatus.NA);
        string mismatched = string.Empty;
        var sockets = Common.Sk;
        if (sockets != null)
        {
            lock (sockets.TcpSocketsLock)
            {
                if (sockets.TcpSockets != null)
                foreach (var socket in sockets.TcpSockets)
                {
                    for (int i = 0; i < machines.Length; i++)
                    {
                        if (!machines[i].MachineEnabled || !machines[i].MachineName.Equals(socket.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (socket.IsClient) { if (socket.Status > client[i]) client[i] = socket.Status; }
                        else if (socket.Status > server[i]) server[i] = socket.Status;
                    }
                }
            }
        }
        bool changed = false;
        for (int i = 0; i < machines.Length; i++)
        {
            changed |= machines[i].StatusClient != client[i] || machines[i].StatusServer != server[i];
            machines[i].SetConnectionStatus(client[i], server[i]);
            if (machines[i].RequiresReconnect) mismatched += $"[{machines[i].MachineName}]";
        }
        matrixSurface?.RefreshMonitors();
        if (changed) { matrixSurface?.SelectionChanged(); matrixSurface?.Invalidate(); }
        return mismatched;
    }

    // One draft owns all unapplied screen positions. The four native PC editors
    // remain separate connection settings; dragging never reorders or copies them.
    internal sealed class MatrixSurface : Control
    {
        private const string Instructions = "Drag screens until their edges meet. Arrow keys select a screen; Control plus arrows moves it. Shift moves faster. Escape cancels a drag. Apply saves the layout.";
        private readonly FrmMatrix owner;
        private IReadOnlyList<MonitorPlacement> source;
        private MonitorPlacement[] draft = Array.Empty<MonitorPlacement>();
        private int selected, held = -1;
        private Point start, dragOrigin;
        private bool dragging, finishing;
        private float scale = 1;
        private PointF translation;
        private string error;
        internal bool IsDragging => dragging;
        internal MonitorPlacement[] Draft => (MonitorPlacement[])draft.Clone();
        internal Rectangle[] Slots => draft.Select(Tile).ToArray();
        internal bool HasChanges => source != null && draft.Any(m => !source.Any(s => s.Key == m.Key && s.WorldOrigin == m.WorldOrigin));
        private int RowHeight => Math.Max(Font.Height, owner.machines[0].NameEditor.PreferredHeight) + 8;
        private Rectangle Canvas => new(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - owner.machines.Length * RowHeight - Font.Height * 3 - 28));

        internal MatrixSurface(FrmMatrix owner)
        {
            this.owner = owner;
            Name = "computerMatrixSurface";
            AccessibleName = Ui.Text("Screen layout");
            AccessibleDescription = Ui.Text(Instructions);
            AccessibleRole = AccessibleRole.Grouping;
            TabStop = true;
            BackColor = SystemColors.Control;
            Font = owner.groupBoxMachineMatrix.Font;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            for (int i = 0; i < owner.machines.Length; i++)
            {
                var machine = owner.machines[i];
                machine.NameEditor.Dock = DockStyle.None;
                Controls.Add(machine.NameEditor);
                Controls.Add(machine.EnabledBox);
                machine.NameEditor.AccessibleName = Ui.Format("PC {0} name", i + 1);
                machine.EnabledBox.AccessibleName = Ui.Format("Enable PC {0}", i + 1);
                machine.EnabledBox.TabIndex = i * 2;
                machine.NameEditor.TabIndex = i * 2 + 1;
                machine.NameEditor.TextChanged += (_, _) => { owner.RefreshShortcutButtons(); Invalidate(); };
                machine.EnabledBox.CheckedChanged += (_, _) => Invalidate();
            }
            owner.Deactivate += OwnerDeactivate;
            owner.toolTip.SetToolTip(this, AccessibleDescription);
        }

        private void OwnerDeactivate(object sender, EventArgs e) => FinishDrag(false);

        internal void RefreshMonitors()
        {
            var next = MonitorTopology.Monitors;
            if (held >= 0 || ReferenceEquals(source, next)) return;
            var previous = draft;
            draft = next.Select(m =>
            {
                int index = Array.FindIndex(previous, p => p.Key == m.Key);
                bool edited = index >= 0 && source != null && source.Any(s => s.Key == m.Key && s.WorldOrigin != previous[index].WorldOrigin);
                return edited ? m with { WorldOrigin = previous[index].WorldOrigin } : m;
            }).ToArray();
            source = next;
            selected = Math.Min(selected, Math.Max(0, draft.Length - 1));
            Fit(); SelectionChanged(); Invalidate();
        }

        internal void Arrange(Rectangle bounds)
        {
            FinishDrag(false);
            Bounds = bounds;
            RefreshMonitors();
            Fit();
            int top = Canvas.Bottom + Font.Height * 2 + 16;
            foreach (var machine in owner.machines)
            {
                machine.EnabledBox.Location = new Point(40, top + Math.Max(0, (RowHeight - machine.EnabledBox.Height) / 2));
                int left = machine.EnabledBox.Right + 10;
                machine.NameEditor.Bounds = new Rectangle(left, top + 3, Math.Max(1, Width * 3 / 5 - left), machine.NameEditor.PreferredHeight);
                machine.NameEditor.Visible = machine.EnabledBox.Visible = true;
                top += RowHeight;
            }
            Invalidate();
        }

        internal MonitorOrigin[] PrepareDraftOrigins()
        {
            _ = new MonitorLayout(draft); // Reject an overlapping draft before hotplug recovery can reposition it.
            return draft.Select(m => new MonitorOrigin(m.Key.Peer, m.Key.DisplayId, m.WorldOrigin.X, m.WorldOrigin.Y)).ToArray();
        }

        internal void ApplyDraft(MonitorOrigin[] origins)
        {
            Setting.Values.SetMonitorOrigins(origins);
            ClearError();
            source = null;
            RefreshMonitors();
        }

        internal void ShowLayoutError()
        {
            error = Ui.Text("Screens overlap. Move them apart before applying.");
            AccessibleDescription = error;
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
            Invalidate();
        }

        private void ClearError()
        {
            error = null;
            AccessibleDescription = Ui.Text(Instructions);
        }

        private void Fit()
        {
            if (draft.Length == 0) return;
            var world = draft.Select(m => m.WorldBounds).Aggregate(Rectangle.Union);
            var view = Canvas;
            scale = Math.Min(view.Width / (float)world.Width, view.Height / (float)world.Height) * .82f;
            translation = new PointF(view.Left + (view.Width - world.Width * scale) / 2 - world.Left * scale,
                view.Top + (view.Height - world.Height * scale) / 2 - world.Top * scale);
        }

        private Rectangle Tile(MonitorPlacement monitor) => new(
            (int)Math.Round(monitor.WorldOrigin.X * scale + translation.X),
            (int)Math.Round(monitor.WorldOrigin.Y * scale + translation.Y),
            Math.Max(1, (int)Math.Round(monitor.NativeBounds.Width * scale)),
            Math.Max(1, (int)Math.Round(monitor.NativeBounds.Height * scale)));

        private string ComputerName(int index)
        {
            var monitor = draft[index];
            string name = monitor.Key.Peer == (uint)Common.MachineID ? Common.MachineName : MachineStuff.NameFromID((ID)monitor.Key.Peer);
            if (string.IsNullOrWhiteSpace(name)) name = Ui.Text("Computer");
            return name.Trim();
        }

        private int MonitorNumber(int index) => draft.Take(index + 1).Count(m => m.Key.Peer == draft[index].Key.Peer);
        private string Caption(int index) => ComputerName(index) + " · " + MonitorNumber(index);

        private IEnumerable<(int First, int Second, int Side, int Low, int High)> PassableEdges()
        {
            for (int first = 0; first < draft.Length; first++)
            {
                if (!Common.IsConnectedTo((ID)draft[first].Key.Peer)) continue;
                for (int second = first + 1; second < draft.Length; second++)
                {
                    if (!Common.IsConnectedTo((ID)draft[second].Key.Peer)) continue;
                    for (int side = 1; side <= 4; side++)
                        if (MonitorLayout.TrySharedEdge(draft[first].WorldBounds, draft[second].WorldBounds, side, out int low, out int high))
                            yield return (first, second, side, low, high);
                }
            }
        }

        internal void SelectionChanged()
        {
            if (draft.Length == 0) AccessibleName = Ui.Text("Screen layout");
            else
            {
                var size = draft[selected].NativeBounds.Size;
                AccessibleName = $"{Ui.Text("Screen layout")}: {Caption(selected)}, {size.Width} × {size.Height}";
            }
            var neighbours = PassableEdges().Where(edge => edge.First == selected || edge.Second == selected)
                .Select(edge => Caption(edge.First == selected ? edge.Second : edge.First));
            AccessibleDescription = error ?? Ui.Text(Instructions) + " " + Ui.Format("Passable edges: {0}.", string.Join(", ", neighbours.DefaultIfEmpty(Ui.Text("None"))));
            AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var view = Canvas;
            using var canvas = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(243, 246, 250));
            e.Graphics.FillRectangle(canvas, view);
            using var titleFont = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);
            var clip = e.Graphics.Save();
            e.Graphics.SetClip(view);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (draft.Length == 0)
                TextRenderer.DrawText(e.Graphics, Ui.Text("Connect a PC to load its screens."), Font, view, SystemColors.WindowText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            for (int i = 0; i < draft.Length; i++)
            {
                var tile = Tile(draft[i]);
                bool active = i == selected;
                Color face = active ? SystemColors.Highlight : SystemColors.Window;
                Color text = active ? SystemColors.HighlightText : SystemColors.WindowText;
                if (!SystemInformation.HighContrast && active) { face = Color.FromArgb(227, 239, 255); text = Color.FromArgb(22, 55, 94); }
                using var fill = new SolidBrush(face);
                using var bezel = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(42, 51, 65));
                using var border = new Pen(active ? SystemColors.Highlight : SystemColors.ControlDark, active && Focused ? 3 : active ? 2 : 1);
                var radius = new Size(6 * DeviceDpi / 96, 6 * DeviceDpi / 96);
                int rim = Math.Max(3, DeviceDpi * 4 / 96), chin = titleFont.Height + rim * 2;
                var body = new Rectangle(tile.Left + 1, tile.Top + 1, Math.Max(1, tile.Width - 2), Math.Max(1, tile.Height - 2));
                var screen = Rectangle.FromLTRB(body.Left + rim, body.Top + rim, body.Right - rim, body.Bottom - chin);
                e.Graphics.FillRoundedRectangle(bezel, body, radius);
                e.Graphics.DrawRoundedRectangle(border, body, radius);
                if (screen.Width <= 0 || screen.Height <= 0) continue;
                e.Graphics.FillRectangle(fill, screen);
                float numberSize = Font.Size * Math.Max(Font.Height, screen.Height - rim * 2) / Font.Height;
                using var numberFont = new Font(Font.FontFamily, Math.Min(Font.Size * 2.2f, numberSize), FontStyle.Bold);
                TextRenderer.DrawText(e.Graphics, MonitorNumber(i).ToString(), numberFont, screen, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
                var name = new Rectangle(screen.Left, screen.Bottom, screen.Width, chin);
                TextRenderer.DrawText(e.Graphics, ComputerName(i), titleFont, name, SystemColors.Window,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
            }
            // Route and paint share the same half-open interval; gaps and offline peers have no marker.
            using (var edge = new Pen(SystemInformation.HighContrast ? SystemColors.Highlight : Color.SeaGreen, 4 * DeviceDpi / 96f)
                { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            using (var outline = new Pen(SystemColors.Window, 6 * DeviceDpi / 96f))
            {
                foreach (var contact in PassableEdges())
                {
                    var bounds = draft[contact.First].WorldBounds;
                    PointF View(int x, int y) => new(x * scale + translation.X, y * scale + translation.Y);
                    int position = contact.Side switch { 1 => bounds.Right, 2 => bounds.Left, 3 => bounds.Bottom, _ => bounds.Top };
                    var from = contact.Side <= 2 ? View(position, contact.Low) : View(contact.Low, position);
                    var to = contact.Side <= 2 ? View(position, contact.High) : View(contact.High, position);
                    e.Graphics.DrawLine(outline, from, to);
                    e.Graphics.DrawLine(edge, from, to);
                }
            }
            e.Graphics.Restore(clip);
            string status = error ?? (draft.Length == 0 ? string.Empty : Ui.Text(HasChanges ? "Changes pending · Apply to use this layout" : "Changes applied"));
            if (draft.Length != 0)
            {
                var size = draft[selected].NativeBounds.Size;
                TextRenderer.DrawText(e.Graphics, $"{Caption(selected)} · {size.Width} × {size.Height}", titleFont,
                    new Rectangle(view.Left, view.Bottom + 4, view.Width / 2, Font.Height + 5), SystemColors.ControlText,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            TextRenderer.DrawText(e.Graphics, status, Font, new Rectangle(view.Left + view.Width / 2, view.Bottom + 4, view.Width / 2, Font.Height + 4),
                error == null || SystemInformation.HighContrast ? SystemColors.ControlText : Color.Firebrick, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, Ui.Text("Drag to arrange. Marked edges connect online screens. Apply to save."), Font,
                new Rectangle(view.Left, view.Bottom + Font.Height + 7, view.Width, Font.Height + 4), SystemColors.ControlText,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            for (int index = 0; index < owner.machines.Length; index++)
            {
                var machine = owner.machines[index];
                TextRenderer.DrawText(e.Graphics, (index + 1).ToString(), Font,
                    new Rectangle(12, machine.NameEditor.Top, 20, RowHeight), SystemColors.ControlText, TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(e.Graphics, machine.DisplayStatus, Font,
                    new Rectangle(machine.NameEditor.Right + 12, machine.NameEditor.Top, Math.Max(1, Width - machine.NameEditor.Right - 24), RowHeight),
                    SystemInformation.HighContrast ? SystemColors.ControlText : machine.DisplayStatusColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { FinishDrag(false); return; }
            if (e.Button != MouseButtons.Left || held >= 0) return;
            for (int i = draft.Length - 1; i >= 0; i--)
                if (Tile(draft[i]).Contains(e.Location))
                {
                    Focus(); selected = held = i; start = e.Location;
                    dragOrigin = draft[i].WorldOrigin;
                    Capture = true; SelectionChanged(); Invalidate(); break;
                }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (held < 0) { Cursor = draft.Any(m => Tile(m).Contains(e.Location)) ? Cursors.Hand : Cursors.Default; return; }
            var threshold = new Rectangle(start.X - SystemInformation.DragSize.Width / 2,
                start.Y - SystemInformation.DragSize.Height / 2, SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
            if (!dragging && threshold.Contains(e.Location)) return;
            dragging = true;
            Cursor = Cursors.SizeAll;
            var origin = new Point(dragOrigin.X + (int)Math.Round((e.X - start.X) / scale),
                dragOrigin.Y + (int)Math.Round((e.Y - start.Y) / scale));
            draft[held] = draft[held] with { WorldOrigin = Snap(origin) };
            ClearError(); Invalidate();
        }

        private bool Overlaps(Point origin)
        {
            var bounds = new Rectangle(origin, draft[selected].NativeBounds.Size);
            return draft.Any(m => m.Key != draft[selected].Key && m.WorldBounds.IntersectsWith(bounds));
        }

        private Point Snap(Point origin)
        {
            var size = draft[selected].NativeBounds.Size;
            int tolerance = Math.Max(1, (int)Math.Round(8 * DeviceDpi / 96f / scale));
            var others = draft.Where(m => m.Key != draft[selected].Key).Select(m => m.WorldBounds).ToArray();
            bool overlapping = Overlaps(origin);
            bool alignedX = !overlapping && others.Any(b => (origin.X == b.Right || (long)origin.X + size.Width == b.Left)
                && origin.Y < b.Bottom && (long)origin.Y + size.Height > b.Top);
            bool alignedY = !overlapping && others.Any(b => (origin.Y == b.Bottom || (long)origin.Y + size.Height == b.Top)
                && origin.X < b.Right && (long)origin.X + size.Width > b.Left);
            bool parallelX = !overlapping && others.Any(b => (origin.Y == b.Bottom || (long)origin.Y + size.Height == b.Top)
                && (origin.X == b.Left || (long)origin.X + size.Width == b.Right));
            bool parallelY = !overlapping && others.Any(b => (origin.X == b.Right || (long)origin.X + size.Width == b.Left)
                && (origin.Y == b.Top || (long)origin.Y + size.Height == b.Bottom));
            double closest = (double)tolerance * tolerance;
            Point result = overlapping ? draft[selected].WorldOrigin : origin;
            bool Consider(long x, long y)
            {
                if (x < int.MinValue || x + size.Width > int.MaxValue || y < int.MinValue || y + size.Height > int.MaxValue) return false;
                double dx = x - origin.X, dy = y - origin.Y, distance = dx * dx + dy * dy;
                // Keep exact contacts on their own axis without cancelling the other axis's snap.
                if (alignedX && x != origin.X || alignedY && y != origin.Y) return false;
                if (distance > (double)tolerance * tolerance) return false;
                var point = new Point((int)x, (int)y);
                if (Overlaps(point)) return false;
                if (distance != 0 && distance <= closest) { closest = distance; result = point; }
                return true;
            }
            foreach (var bounds in others)
            {
                long x = Math.Abs((long)origin.X - bounds.Left) <= Math.Abs((long)origin.X - bounds.Right + size.Width)
                    ? bounds.Left : (long)bounds.Right - size.Width;
                long y = Math.Abs((long)origin.Y - bounds.Top) <= Math.Abs((long)origin.Y - bounds.Bottom + size.Height)
                    ? bounds.Top : (long)bounds.Bottom - size.Height;
                // Preserve a matched parallel end without blocking a new primary-axis contact.
                if (parallelX || Math.Abs(x - origin.X) > tolerance) x = origin.X;
                if (parallelY || Math.Abs(y - origin.Y) > tolerance) y = origin.Y;
                // Align the parallel ends together with the touching edge; either axis alone
                // can still overlap the other neighbour at a shared corner.
                if (y < bounds.Bottom && y + size.Height > bounds.Top)
                {
                    if (!Consider((long)bounds.Left - size.Width, y)) Consider((long)bounds.Left - size.Width, origin.Y);
                    if (!Consider(bounds.Right, y)) Consider(bounds.Right, origin.Y);
                }
                if (x < bounds.Right && x + size.Width > bounds.Left)
                {
                    if (!Consider(x, (long)bounds.Top - size.Height)) Consider(origin.X, (long)bounds.Top - size.Height);
                    if (!Consider(x, bounds.Bottom)) Consider(origin.X, bounds.Bottom);
                }
            }
            return result;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) FinishDrag(Canvas.Contains(e.Location));
        }

        internal void FinishDrag(bool commit)
        {
            if (held < 0 || finishing) return;
            finishing = true;
            try
            {
                if (!commit || !dragging) draft[held] = draft[held] with { WorldOrigin = dragOrigin };
                held = -1; dragging = false; Capture = false; Cursor = Cursors.Hand;
                Fit(); SelectionChanged(); Invalidate();
            }
            finally { finishing = false; }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && !finishing) FinishDrag(false);
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (held >= 0 && (keyData & Keys.KeyCode) == Keys.Escape) { FinishDrag(false); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (held >= 0 || draft.Length == 0) return;
            var direction = e.KeyCode switch { Keys.Left => new Point(-1, 0), Keys.Right => new Point(1, 0),
                Keys.Up => new Point(0, -1), Keys.Down => new Point(0, 1), _ => Point.Empty };
            if (direction.IsEmpty) return;
            if (e.Control)
            {
                var origin = draft[selected].WorldOrigin;
                int step = e.Shift ? 20 : 1;
                var next = new Point(origin.X + direction.X * step, origin.Y + direction.Y * step);
                if (!Overlaps(next)) draft[selected] = draft[selected] with { WorldOrigin = next };
                ClearError(); Fit();
            }
            else
            {
                var current = Tile(draft[selected]);
                double nearest = double.PositiveInfinity;
                for (int i = 0; i < draft.Length; i++)
                {
                    var candidate = Tile(draft[i]);
                    double dx = candidate.Left + candidate.Width / 2.0 - current.Left - current.Width / 2.0;
                    double dy = candidate.Top + candidate.Height / 2.0 - current.Top - current.Height / 2.0;
                    double distance = dx * dx + dy * dy;
                    if (dx * direction.X + dy * direction.Y > 0 && distance < nearest) { nearest = distance; selected = i; }
                }
            }
            SelectionChanged(); Invalidate(); e.Handled = true;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) FinishDrag(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { FinishDrag(false); owner.Deactivate -= OwnerDeactivate; }
            base.Dispose(disposing);
        }
    }
}
#endif
