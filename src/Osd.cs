using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SdrBrightness
{
    // Brief indicator for shortcut presses, like Windows' volume flyout. It never takes focus
    // and lets clicks through, so a game or video keeps running undisturbed underneath.
    sealed class Osd : Form
    {
        const float WidthDip = 272, HeightDip = 52, Pad = 18;
        const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        readonly Timer hideTimer = new Timer { Interval = 1500 };
        Theme theme = Theme.Current();
        float scale;
        Font textFont, smallFont, valueFont, glyphFont;
        int value = -1;
        string text = "", glyph = Theme.GlyphBrightness;

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "SDR brightness indicator";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            hideTimer.Tick += delegate
            {
                hideTimer.Stop();
                Hide();
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;       // CS_DROPSHADOW
                cp.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8; // NOACTIVATE, TOOLWINDOW, TRANSPARENT, TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }

        public void ShowLevel(int value)
        {
            this.value = value;
            Present();
        }

        public void ShowText(string glyph, string text)
        {
            value = -1;
            this.glyph = glyph;
            this.text = text;
            Present();
        }

        // Bottom center of the screen the cursor is on, above the taskbar.
        void Present()
        {
            theme = Theme.Current();
            UpdateScale();
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = S(WidthDip), h = S(HeightDip);
            Bounds = new Rectangle(work.Left + (work.Width - w) / 2, work.Bottom - h - S(24), w, h);
            if (!Visible)
                Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Invalidate();
            hideTimer.Stop();
            hideTimer.Start();
        }

        void UpdateScale()
        {
            float dpi;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                dpi = g.DpiX;
            float s = dpi / 96f;
            if (textFont != null && s == scale)
                return;
            scale = s;
            DisposeFonts();
            textFont = new Font("Segoe UI", 14 * s, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 13 * s, GraphicsUnit.Pixel);
            valueFont = new Font("Segoe UI Semibold", 15 * s, GraphicsUnit.Pixel);
            glyphFont = new Font(Theme.IconFont, 16 * s, GraphicsUnit.Pixel);
        }

        int S(float dip)
        {
            return (int)Math.Round(dip * scale);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(theme.Background);
            TextRenderer.DrawText(g, value >= 0 ? Theme.GlyphBrightness : glyph, glyphFont,
                new Rectangle(S(Pad), 0, S(20), h), theme.Text, Line);

            if (value < 0)
            {
                TextRenderer.DrawText(g, text, textFont, new Rectangle(S(Pad + 30), 0, w - S(2 * Pad + 30), h), theme.Text,
                    Line | TextFormatFlags.EndEllipsis);
                return;
            }

            // Right to left: SDR white in nits, the value, then the bar. Widths are sized for the
            // widest values ("100", "480 nits") so nothing shifts as the number changes.
            int right = w - S(Pad);
            int nitsW = TextRenderer.MeasureText(g, HdrDisplay.NitsFromSlider(BrightnessPopup.Max) + " nits", smallFont, Size.Empty, Line).Width;
            int valueW = TextRenderer.MeasureText(g, BrightnessPopup.Max.ToString(), valueFont, Size.Empty, Line).Width;
            var nitsRect = new Rectangle(right - nitsW, 0, nitsW, h);
            var valueRect = new Rectangle(nitsRect.X - S(10) - valueW, 0, valueW, h);
            float x0 = S(Pad + 32), x1 = valueRect.X - S(14), y = h / 2f, railH = S(4);
            float fill = x0 + (x1 - x0) * (value - BrightnessPopup.Min) / (BrightnessPopup.Max - BrightnessPopup.Min);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Draw.FillRoundRect(g, theme.Rail, Color.Empty, new RectangleF(x0, y - railH / 2, x1 - x0, railH), railH / 2, 0);
            if (fill > x0)
                Draw.FillRoundRect(g, theme.Accent, Color.Empty, new RectangleF(x0, y - railH / 2, Math.Max(railH, fill - x0), railH), railH / 2, 0);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, value.ToString(), valueFont, valueRect, theme.Text, Line | TextFormatFlags.Right);
            TextRenderer.DrawText(g, HdrDisplay.NitsFromSlider(value) + " nits", smallFont, nitsRect, theme.Secondary,
                Line | TextFormatFlags.Right);
        }

        void DisposeFonts()
        {
            foreach (var font in new[] { textFont, smallFont, valueFont, glyphFont })
                if (font != null)
                    font.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hideTimer.Dispose();
                DisposeFonts();
            }
            base.Dispose(disposing);
        }
    }
}
