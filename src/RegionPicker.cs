using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SdrBrightness
{
    // Freezes the screen and lets you drag out the area to keep: one full-screen overlay per display
    // shows the capture dimmed, with the selection at full brightness. Esc or right-click cancels.
    sealed class RegionPicker
    {
        readonly List<PickerWindow> windows = new List<PickerWindow>();
        bool finished;

        public event Action<Rectangle> Picked; // desktop coordinates
        public event Action Closed;

        // Create and show inside Native.PerMonitorDpi(), so each overlay maps 1:1 onto its display's pixels.
        public RegionPicker(IList<DesktopImage> images)
        {
            Theme theme = Theme.Current();
            foreach (DesktopImage image in images)
            {
                bool toneMapped;
                windows.Add(new PickerWindow(this, ScreenCapture.Render(images, image.Bounds, out toneMapped), image.Bounds, theme));
            }
        }

        public void Show()
        {
            foreach (PickerWindow w in windows)
                w.Show();
            PickerWindow under = windows.Find(w => w.Origin.Contains(Cursor.Position)) ?? windows[0];
            under.Activate();
            Native.SetForegroundWindow(under.Handle);
        }

        internal bool Owns(IntPtr hwnd)
        {
            return windows.Exists(w => w.IsHandleCreated && w.Handle == hwnd);
        }

        internal void Finish(Rectangle? area)
        {
            if (finished)
                return;
            finished = true;
            foreach (PickerWindow w in windows)
                w.Close();
            if (area.HasValue && Picked != null)
                Picked(area.Value);
            if (Closed != null)
                Closed();
        }
    }

    sealed class PickerWindow : Form
    {
        readonly RegionPicker owner;
        readonly Bitmap image, dimmed;
        readonly Theme theme;
        readonly Rectangle origin; // this display in desktop coordinates
        Font font;
        float scale = 1;
        Point start;
        Rectangle selection; // client coordinates
        bool dragging;

        public PickerWindow(RegionPicker owner, Bitmap image, Rectangle bounds, Theme theme)
        {
            this.owner = owner;
            this.image = image;
            this.theme = theme;
            origin = bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = bounds;
            Cursor = Cursors.Cross;
            Text = "SDRFix region";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            dimmed = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dimmed))
            using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            {
                var all = new Rectangle(Point.Empty, image.Size);
                g.DrawImage(image, all, all, GraphicsUnit.Pixel);
                g.FillRectangle(shade, all);
            }
        }

        public Rectangle Origin
        {
            get { return origin; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                scale = Native.GetDpiForWindow(Handle) / 96f;
            }
            catch (EntryPointNotFoundException)
            {
                using (var g = CreateGraphics())
                    scale = g.DpiX / 96f;
            }
            if (scale <= 0)
                scale = 1;
            font = new Font("Segoe UI", 13 * scale, GraphicsUnit.Pixel);
        }

        int S(float dip)
        {
            return (int)Math.Round(dip * scale);
        }

        // ---- Input ----

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                owner.Finish(null);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Switching to another app cancels; moving to the overlay on another display doesn't.
            BeginInvoke((Action)delegate
            {
                if (!owner.Owns(Native.GetForegroundWindow()))
                    owner.Finish(null);
            });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                owner.Finish(null);
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;
            Rectangle before = Dirty();
            dragging = true;
            start = e.Location;
            selection = Rectangle.Empty;
            Capture = true;
            Invalidate(Rectangle.Union(before, Dirty()));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging)
                return;
            Rectangle before = Dirty();
            int x = Math.Max(0, Math.Min(ClientSize.Width, e.X)), y = Math.Max(0, Math.Min(ClientSize.Height, e.Y));
            selection = Rectangle.FromLTRB(Math.Min(start.X, x), Math.Min(start.Y, y), Math.Max(start.X, x), Math.Max(start.Y, y));
            Invalidate(Rectangle.Union(before, Dirty()));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !dragging)
                return;
            dragging = false;
            Capture = false;
            if (selection.Width >= 4 && selection.Height >= 4)
            {
                owner.Finish(new Rectangle(origin.X + selection.X, origin.Y + selection.Y, selection.Width, selection.Height));
                return;
            }
            Rectangle before = Dirty();
            selection = Rectangle.Empty; // a click, not a drag: start over
            Invalidate(Rectangle.Union(before, Dirty()));
        }

        // ---- Drawing ----

        // What changes when the selection moves: the frame around it and the label.
        Rectangle Dirty()
        {
            if (selection.IsEmpty)
                return LabelRect("Drag to select an area · Esc to cancel");
            return Rectangle.Union(Rectangle.Inflate(selection, 3, 3), LabelRect(SizeText()));
        }

        string SizeText()
        {
            return selection.Width + " × " + selection.Height;
        }

        // The hint sits top center; the size label just below the selection, or inside it at the bottom edge.
        Rectangle LabelRect(string text)
        {
            if (font == null)
                return Rectangle.Empty;
            Size size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
            int w = size.Width + S(20), h = size.Height + S(10);
            if (selection.IsEmpty)
                return new Rectangle((ClientSize.Width - w) / 2, S(24), w, h);
            int x = Math.Min(selection.X, ClientSize.Width - w);
            int y = selection.Bottom + S(8);
            if (y + h > ClientSize.Height)
                y = Math.Max(0, selection.Bottom - h - S(8));
            return new Rectangle(Math.Max(0, x), y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);
            if (selection.IsEmpty)
            {
                DrawLabel(g, "Drag to select an area · Esc to cancel");
                return;
            }
            Rectangle lit = Rectangle.Intersect(selection, clip);
            if (!lit.IsEmpty)
                g.DrawImage(image, lit, lit, GraphicsUnit.Pixel);
            using (var outline = new Pen(Color.FromArgb(160, 0, 0, 0)))
                g.DrawRectangle(outline, selection.X - 2, selection.Y - 2, selection.Width + 3, selection.Height + 3);
            using (var edge = new Pen(Color.White))
                g.DrawRectangle(edge, selection.X - 1, selection.Y - 1, selection.Width + 1, selection.Height + 1);
            DrawLabel(g, SizeText());
        }

        void DrawLabel(Graphics g, string text)
        {
            Rectangle r = LabelRect(text);
            if (r.IsEmpty)
                return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Draw.FillRoundRect(g, theme.Background, theme.ControlBorder, r, r.Height / 2f, Math.Max(1f, scale));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            TextRenderer.DrawText(g, text, font, r, theme.Text, Draw.Centered);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                image.Dispose();
                dimmed.Dispose();
                if (font != null)
                    font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
