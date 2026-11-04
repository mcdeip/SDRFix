using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SdrBrightness
{
    enum StatusKind { Info, Busy, Success, Warning, Error }

    // Borderless Windows 11 style flyout: a slider that snaps to steps of 5, a gamma choice and an HDR-profile toggle.
    sealed class BrightnessPopup : Form
    {
        public const int Step = 5, Min = 0, Max = 100;

        const float WidthDip = 360, RowDip = 56;
        const float Pad = 20, RailYDip = 66, StatusTopDip = 92, GammaTopDip = 122;
        const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        public event Action<int> Previewed;    // value changed while sliding: brightness only
        public event Action<int> Committed;    // value settled: brightness + profile
        public event Action StatusClicked;
        public event Action<bool> HdrToggled;
        public event Action<string> GammaChanged;
        public event Action SetupClicked;

        int value;
        bool sliderEnabled = true;
        bool hdrOn, hdrAvailable;
        string hdrHint = "";
        string status = "";
        StatusKind statusKind;
        bool statusClickable;
        string displayName;
        string gamma = ProfileGenerator.Gammas[0];
        bool[] gammaAvailable = new bool[ProfileGenerator.Gammas.Length];
        bool gammaRow = true;

        Theme theme = Theme.Current();
        float scale;
        Font titleFont, valueFont, smallFont, linkFont, glyphFont, smallGlyphFont;

        bool dragging, commitPending, hoverThumb, hoverStatus, hoverFooter, hoverSetup;
        int hoverGamma = -1;
        int wheelDelta;
        int hiddenAt = Environment.TickCount - 10000;
        IntPtr foregroundAtShow;
        readonly Timer commitTimer = new Timer { Interval = 400 };
        readonly Timer focusWatch = new Timer { Interval = 200 };

        public BrightnessPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "SDR brightness";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            commitTimer.Tick += delegate { FlushCommit(); };
            focusWatch.Tick += delegate { WatchFocus(); };
            UpdateScale();
        }

        public int Value { get { return value; } }
        public bool HdrOn { get { return hdrOn; } }
        public bool Interacting { get { return dragging || commitPending; } }
        public int LastHiddenTick { get { return hiddenAt; } }

        public static int Snap(int v)
        {
            v = (int)Math.Round(v / (double)Step, MidpointRounding.AwayFromZero) * Step;
            return Math.Max(Min, Math.Min(Max, v));
        }

        public void SetState(int value, bool sliderEnabled, bool hdrOn, bool hdrAvailable, string hdrHint, string displayName)
        {
            if (!Interacting)
                this.value = Snap(value);
            this.sliderEnabled = sliderEnabled;
            this.hdrOn = hdrOn;
            this.hdrAvailable = hdrAvailable;
            this.hdrHint = hdrHint ?? "";
            this.displayName = displayName;
            Invalidate();
        }

        // `available[i]` says whether setup's profiles exist for ProfileGenerator.Gammas[i];
        // with none at all the row offers setup instead. `show` hides the row altogether.
        public void SetGamma(string gamma, bool[] available, bool show)
        {
            this.gamma = gamma;
            gammaAvailable = available;
            gammaRow = show; // the height follows on the next ShowNear
            Invalidate();
        }

        float FooterTopDip
        {
            get { return gammaRow ? GammaTopDip + RowDip : GammaTopDip; }
        }

        float HeightDip
        {
            get { return FooterTopDip + RowDip; }
        }

        bool GammaReady
        {
            get { return Array.IndexOf(gammaAvailable, true) >= 0; }
        }

        public void SetStatus(string text, StatusKind kind, bool clickable)
        {
            status = text ?? "";
            statusKind = kind;
            statusClickable = clickable;
            Invalidate();
        }

        // ---- Showing and hiding ----

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: no taskbar button or Alt+Tab entry
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }

        // Opens above the taskbar next to `anchor` (normally the cursor on the tray icon).
        public void ShowNear(Point anchor)
        {
            theme = Theme.Current();
            UpdateScale();
            var size = new Size(S(WidthDip), S(HeightDip));
            Screen screen = Screen.FromPoint(anchor);
            Rectangle work = screen.WorkingArea, bounds = screen.Bounds;
            int m = S(12);
            int x = anchor.X - size.Width / 2, y = work.Bottom - size.Height - m;
            if (work.Top > bounds.Top)
                y = work.Top + m;
            else if (work.Left > bounds.Left)
            {
                x = work.Left + m;
                y = anchor.Y - size.Height / 2;
            }
            else if (work.Right < bounds.Right)
            {
                x = work.Right - size.Width - m;
                y = anchor.Y - size.Height / 2;
            }
            x = Math.Max(work.Left + m, Math.Min(x, work.Right - size.Width - m));
            y = Math.Max(work.Top + m, Math.Min(y, work.Bottom - size.Height - m));

            Bounds = new Rectangle(x, y, size.Width, size.Height);
            hoverThumb = hoverStatus = hoverFooter = hoverSetup = false;
            hoverGamma = -1;
            wheelDelta = 0;
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            foregroundAtShow = Native.GetForegroundWindow();
            focusWatch.Start();
            Invalidate();
        }

        public void HidePopup()
        {
            if (!Visible)
                return;
            dragging = false;
            Capture = false;
            FlushCommit();
            focusWatch.Stop();
            Hide();
            hiddenAt = Environment.TickCount;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HidePopup();
        }

        // Fallback for when Windows refused to give the popup focus, so Deactivate never fires.
        void WatchFocus()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != Handle && foreground != foregroundAtShow)
                HidePopup();
        }

        // ---- Input ----

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !sliderEnabled || !SliderHit(e.Location))
                return;
            dragging = true;
            commitPending = true; // committing on release also re-applies a mismatched profile
            commitTimer.Stop();
            Capture = true;
            ChangeValue(ValueAt(e.X));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                ChangeValue(ValueAt(e.X));
                return;
            }
            float dx = e.X - ThumbX, dy = e.Y - RailY;
            bool thumb = sliderEnabled && dx * dx + dy * dy <= S(12) * S(12);
            bool link = statusClickable && StatusRect.Contains(e.Location);
            bool footer = hdrAvailable && FooterRect.Contains(e.Location);
            int segment = gammaRow && GammaReady ? Draw.SegmentAt(SegmentsRect, gammaAvailable.Length, e.Location) : -1;
            bool setup = gammaRow && !GammaReady && GammaRect.Contains(e.Location);
            if (thumb != hoverThumb || link != hoverStatus || footer != hoverFooter || segment != hoverGamma || setup != hoverSetup)
            {
                hoverThumb = thumb;
                hoverStatus = link;
                hoverFooter = footer;
                hoverGamma = segment;
                hoverSetup = setup;
                Invalidate();
            }
            Cursor = link ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
                return;
            if (dragging)
            {
                dragging = false;
                Capture = false;
                FlushCommit();
                Invalidate();
            }
            else if (statusClickable && StatusRect.Contains(e.Location))
            {
                var handler = StatusClicked;
                if (handler != null)
                    handler();
            }
            else if (hdrAvailable && FooterRect.Contains(e.Location))
                ToggleHdr();
            else if (!gammaRow)
                return;
            else if (GammaReady)
            {
                int segment = Draw.SegmentAt(SegmentsRect, gammaAvailable.Length, e.Location);
                if (segment >= 0)
                    ChooseGamma(segment);
            }
            else if (GammaRect.Contains(e.Location))
            {
                var handler = SetupClicked;
                if (handler != null)
                    handler();
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging)
            {
                dragging = false;
                FlushCommit();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverThumb = hoverStatus = hoverFooter = hoverSetup = false;
            hoverGamma = -1;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!sliderEnabled)
                return;
            wheelDelta += e.Delta;
            int steps = wheelDelta / 120;
            if (steps == 0)
                return;
            wheelDelta -= steps * 120;
            Nudge(steps);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    HidePopup();
                    return true;
                case Keys.Enter:
                    FlushCommit();
                    HidePopup();
                    return true;
                case Keys.Space:
                    if (hdrAvailable)
                        ToggleHdr();
                    return true;
                case Keys.G:
                    for (int i = 1; i < gammaAvailable.Length; i++)
                    {
                        int next = (Array.IndexOf(ProfileGenerator.Gammas, gamma) + i) % gammaAvailable.Length;
                        if (gammaAvailable[next])
                        {
                            ChooseGamma(next);
                            break;
                        }
                    }
                    return true;
            }
            if (sliderEnabled)
            {
                switch (keyData)
                {
                    case Keys.Left:
                    case Keys.Down:
                        Nudge(-1);
                        return true;
                    case Keys.Right:
                    case Keys.Up:
                        Nudge(1);
                        return true;
                    case Keys.PageDown:
                        Nudge(-5);
                        return true;
                    case Keys.PageUp:
                        Nudge(5);
                        return true;
                    case Keys.Home:
                        KeyboardChange(Min);
                        return true;
                    case Keys.End:
                        KeyboardChange(Max);
                        return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Nudge(int steps)
        {
            KeyboardChange(value + steps * Step);
        }

        // Keyboard and wheel changes preview at once and commit after a short pause.
        void KeyboardChange(int v)
        {
            if (!ChangeValue(v))
                return;
            commitPending = true;
            commitTimer.Stop();
            commitTimer.Start();
        }

        bool ChangeValue(int v)
        {
            v = Snap(v);
            if (v == value)
                return false;
            value = v;
            Invalidate();
            var handler = Previewed;
            if (handler != null)
                handler(v);
            return true;
        }

        void FlushCommit()
        {
            commitTimer.Stop();
            if (!commitPending)
                return;
            commitPending = false;
            var handler = Committed;
            if (handler != null)
                handler(value);
        }

        void ToggleHdr()
        {
            hdrOn = !hdrOn;
            Invalidate();
            var handler = HdrToggled;
            if (handler != null)
                handler(hdrOn);
        }

        void ChooseGamma(int index)
        {
            string chosen = ProfileGenerator.Gammas[index];
            if (!gammaAvailable[index] || chosen == gamma)
                return;
            gamma = chosen;
            Invalidate();
            var handler = GammaChanged;
            if (handler != null)
                handler(chosen);
        }

        // ---- Layout ----

        void UpdateScale()
        {
            float dpi;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                dpi = g.DpiX;
            float s = dpi / 96f;
            if (titleFont != null && s == scale)
                return;
            scale = s;
            DisposeFonts();
            titleFont = new Font("Segoe UI", 14 * s, GraphicsUnit.Pixel);
            valueFont = new Font("Segoe UI Semibold", 20 * s, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel);
            linkFont = new Font("Segoe UI", 12 * s, FontStyle.Underline, GraphicsUnit.Pixel);
            glyphFont = new Font(Theme.IconFont, 16 * s, GraphicsUnit.Pixel);
            smallGlyphFont = new Font(Theme.IconFont, 12 * s, GraphicsUnit.Pixel);
        }

        int S(float dip)
        {
            return (int)Math.Round(dip * scale);
        }

        float RailLeft { get { return S(Pad + 10); } }
        float RailRight { get { return ClientSize.Width - S(Pad + 10); } }
        float RailY { get { return S(RailYDip); } }
        float ThumbX { get { return RailLeft + (RailRight - RailLeft) * (value - Min) / (Max - Min); } }

        Rectangle StatusRect
        {
            get { return new Rectangle(S(Pad), S(StatusTopDip), ClientSize.Width - S(2 * Pad), S(18)); }
        }

        Rectangle FooterRect
        {
            get { return new Rectangle(0, S(FooterTopDip), ClientSize.Width, ClientSize.Height - S(FooterTopDip)); }
        }

        Rectangle GammaRect
        {
            get { return new Rectangle(0, S(GammaTopDip), ClientSize.Width, S(RowDip)); }
        }

        // "2.2 | 2.4", or the Set up button when no profiles exist yet.
        RectangleF SegmentsRect
        {
            get
            {
                Rectangle row = GammaRect;
                float w = S(104), h = S(32);
                return new RectangleF(ClientSize.Width - S(Pad) - w, row.Y + (row.Height - h) / 2f, w, h);
            }
        }

        bool SliderHit(Point p)
        {
            return Math.Abs(p.Y - RailY) <= S(16) && p.X >= RailLeft - S(12) && p.X <= RailRight + S(12);
        }

        int ValueAt(int x)
        {
            float t = (x - RailLeft) / (RailRight - RailLeft);
            return Snap((int)Math.Round(Min + t * (Max - Min)));
        }

        // ---- Painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width;
            g.Clear(theme.Background);

            int top = S(16), rowH = S(24);
            TextRenderer.DrawText(g, Theme.GlyphBrightness, glyphFont, new Rectangle(S(Pad), top, S(20), rowH), theme.Text, Line);
            TextRenderer.DrawText(g, "SDR content brightness", titleFont,
                new Rectangle(S(Pad + 28), top, w - S(2 * Pad + 28 + 60 + 64), rowH), theme.Text, Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, HdrDisplay.NitsFromSlider(value) + " nits", smallFont,
                new Rectangle(w - S(Pad + 60 + 64), top + S(2), S(56), rowH), theme.Tertiary, Line | TextFormatFlags.Right);
            TextRenderer.DrawText(g, value.ToString(), valueFont,
                new Rectangle(w - S(Pad + 60), top - S(2), S(60), rowH + S(4)),
                sliderEnabled ? theme.Text : theme.Tertiary, Line | TextFormatFlags.Right);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSlider(g);
            g.SmoothingMode = SmoothingMode.None;
            DrawStatus(g);
            if (gammaRow)
                DrawGammaRow(g, w);
            DrawFooter(g, w);
        }

        void DrawGammaRow(Graphics g, int w)
        {
            Rectangle r = GammaRect;
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, r);
            using (var pen = new Pen(theme.Divider))
                g.DrawLine(pen, 0, r.Y, w, r.Y);

            bool ready = GammaReady;
            int textW = w - S(2 * Pad + 116);
            TextRenderer.DrawText(g, "Gamma", titleFont, new Rectangle(S(Pad), r.Y + S(9), textW, S(20)), theme.Text,
                Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, ready ? "Pure power curve for SDR apps" : "Create profiles for 2.2 and 2.4", smallFont,
                new Rectangle(S(Pad), r.Y + S(29), textW, S(16)), theme.Secondary, Line | TextFormatFlags.EndEllipsis);

            RectangleF seg = SegmentsRect;
            if (ready)
                Draw.Segments(g, theme, seg, ProfileGenerator.Gammas, Array.IndexOf(ProfileGenerator.Gammas, gamma), hoverGamma,
                    gammaAvailable, titleFont, scale);
            else
            {
                Draw.FillRoundRect(g, hoverSetup ? theme.ControlHover : theme.ControlFill, theme.ControlBorder, seg, 4 * scale,
                    Math.Max(1f, scale));
                TextRenderer.DrawText(g, "Set up…", titleFont, Rectangle.Round(seg), theme.Text, Draw.Centered);
            }
        }

        void DrawSlider(Graphics g)
        {
            float x0 = RailLeft, x1 = RailRight, y = RailY, tx = ThumbX;
            Color fill = sliderEnabled ? theme.Accent : theme.Disabled;
            float railH = S(4);

            // A tick for every step of 5, longer at the quarter marks.
            using (var pen = new Pen(Color.FromArgb(120, theme.Tertiary), Math.Max(1f, scale)))
            {
                for (int i = 0; i <= (Max - Min) / Step; i++)
                {
                    float x = x0 + (x1 - x0) * i * Step / (Max - Min);
                    g.DrawLine(pen, x, y + S(14), x, y + S(14) + (i % 5 == 0 ? S(5) : S(3)));
                }
            }

            FillPill(g, sliderEnabled ? theme.Rail : theme.Disabled, x0, y - railH / 2, x1 - x0, railH);
            if (tx > x0)
                FillPill(g, fill, x0, y - railH / 2, tx - x0, railH);

            float outer = S(10);
            float inner = dragging ? S(5) : hoverThumb ? S(7) : S(6);
            using (var brush = new SolidBrush(theme.ThumbFill))
                g.FillEllipse(brush, tx - outer, y - outer, outer * 2, outer * 2);
            using (var pen = new Pen(theme.ThumbBorder, Math.Max(1f, scale)))
                g.DrawEllipse(pen, tx - outer, y - outer, outer * 2, outer * 2);
            using (var brush = new SolidBrush(fill))
                g.FillEllipse(brush, tx - inner, y - inner, inner * 2, inner * 2);
        }

        void DrawStatus(Graphics g)
        {
            Rectangle r = StatusRect;
            string glyph = null;
            Color color = theme.Secondary, glyphColor = theme.Secondary;
            switch (statusKind)
            {
                case StatusKind.Success:
                    glyph = Theme.GlyphCheck;
                    glyphColor = theme.Accent;
                    break;
                case StatusKind.Busy:
                    glyph = Theme.GlyphSync;
                    break;
                case StatusKind.Warning:
                    glyph = Theme.GlyphWarning;
                    color = glyphColor = theme.Warning;
                    break;
                case StatusKind.Error:
                    glyph = Theme.GlyphError;
                    color = glyphColor = theme.Error;
                    break;
            }

            int x = r.X, right = r.Right;
            if (glyph != null)
            {
                TextRenderer.DrawText(g, glyph, smallGlyphFont, new Rectangle(x, r.Y, S(16), r.Height), glyphColor, Line);
                x += S(20);
            }
            if (!string.IsNullOrEmpty(displayName))
            {
                int nameW = Math.Min(TextRenderer.MeasureText(g, displayName, smallFont, Size.Empty, Line).Width, r.Width / 3);
                TextRenderer.DrawText(g, displayName, smallFont, new Rectangle(r.Right - nameW, r.Y, nameW, r.Height),
                    theme.Tertiary, Line | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
                right -= nameW + S(12);
            }
            TextRenderer.DrawText(g, status, statusClickable && hoverStatus ? linkFont : smallFont,
                new Rectangle(x, r.Y, right - x, r.Height), color, Line | TextFormatFlags.EndEllipsis);
        }

        void DrawFooter(Graphics g, int w)
        {
            Rectangle r = FooterRect;
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, r);
            using (var pen = new Pen(theme.Divider))
                g.DrawLine(pen, 0, r.Y, w, r.Y);

            Color text = hdrAvailable ? theme.Text : theme.Tertiary;
            int textW = w - S(2 * Pad + 96);
            TextRenderer.DrawText(g, "HDR content", titleFont, new Rectangle(S(Pad), r.Y + S(9), textW, S(20)), text,
                Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, hdrHint, smallFont, new Rectangle(S(Pad), r.Y + S(29), textW, S(16)),
                hdrAvailable ? theme.Secondary : theme.Tertiary, Line | TextFormatFlags.EndEllipsis);

            // Windows 11 toggle switch with its On/Off label.
            bool on = hdrOn && hdrAvailable;
            float sw = S(40), sh = S(20);
            float sx = w - S(Pad) - sw, sy = r.Y + (r.Height - sh) / 2f;
            TextRenderer.DrawText(g, on ? "On" : "Off", titleFont,
                new Rectangle((int)sx - S(44), (int)sy, S(32), (int)sh), text, Line | TextFormatFlags.Right);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            float knob = hoverFooter ? S(14) : S(12);
            Color knobColor;
            if (on)
            {
                FillPill(g, theme.Accent, sx, sy, sw, sh);
                knobColor = theme.OnAccent;
            }
            else
            {
                knobColor = hdrAvailable ? theme.Secondary : theme.Disabled;
                float penW = Math.Max(1f, scale);
                using (var pen = new Pen(knobColor, penW))
                using (var path = Pill(sx + penW / 2, sy + penW / 2, sw - penW, sh - penW))
                    g.DrawPath(pen, path);
            }
            float cx = on ? sx + sw - sh / 2 : sx + sh / 2, cy = sy + sh / 2;
            using (var brush = new SolidBrush(knobColor))
                g.FillEllipse(brush, cx - knob / 2, cy - knob / 2, knob, knob);
            g.SmoothingMode = SmoothingMode.None;
        }

        static GraphicsPath Pill(float x, float y, float w, float h)
        {
            w = Math.Max(w, h);
            var path = new GraphicsPath();
            path.AddArc(x, y, h, h, 90, 180);
            path.AddArc(x + w - h, y, h, h, 270, 180);
            path.CloseFigure();
            return path;
        }

        static void FillPill(Graphics g, Color color, float x, float y, float w, float h)
        {
            using (var path = Pill(x, y, w, h))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        void DisposeFonts()
        {
            foreach (var font in new[] { titleFont, valueFont, smallFont, linkFont, glyphFont, smallGlyphFont })
                if (font != null)
                    font.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commitTimer.Dispose();
                focusWatch.Dispose();
                DisposeFonts();
            }
            base.Dispose(disposing);
        }
    }
}
