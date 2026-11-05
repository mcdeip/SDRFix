using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SdrBrightness
{
    enum StatusKind { Info, Busy, Success, Warning, Error }

    // What the popup shows for one display.
    sealed class PopupDisplay
    {
        public string Name;
        public int Value;
        public bool Enabled = true; // HDR is on, so the slider does something
        public bool HdrOn, HdrAvailable;
        public string HdrHint = "";
        public string Status = "";
        public StatusKind StatusKind;
        public bool StatusClickable;
        public string Gamma = ProfileGenerator.Gammas[0];
        public bool[] GammaAvailable = new bool[ProfileGenerator.Gammas.Length]; // whether setup's set exists per gamma
    }

    // Borderless Windows 11 style flyout. For one display: a slider that snaps to steps of 5, a gamma choice and an
    // HDR-profile toggle. For several: a compact block like that per display, and a switch that links their sliders.
    sealed class BrightnessPopup : Form
    {
        public const int Step = 5, Min = 0, Max = 100;

        const float WidthDip = 360, RowDip = 56;
        const float Pad = 20, RailYDip = 66, StatusTopDip = 92, GammaTopDip = 122;
        const float ControlsDip = 52, SectionDip = GammaTopDip + ControlsDip; // one display's block when there are several
        const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        // The int is the display's index in the list last given to SetDisplays.
        public event Action<int, int> Previewed;   // value changed while sliding: brightness only
        public event Action<int, int> Committed;   // value settled: brightness + profile
        public event Action<int> StatusClicked;
        public event Action<int, bool> HdrToggled;
        public event Action<int, string> GammaChanged;
        public event Action<int> SetupClicked;
        public event Action<bool> LinkToggled;

        List<PopupDisplay> displays = new List<PopupDisplay> { new PopupDisplay() };
        bool nameInStatus; // one display: show its name next to the status
        bool gammaRow = true;
        bool linked;

        Theme theme = Theme.Current();
        float scale;
        Font titleFont, valueFont, smallFont, linkFont, glyphFont, smallGlyphFont;

        // The display keys and the wheel act on: the one under the pointer.
        int active;
        // A change in progress: the display being moved, the values everyone started from (so linked sliders
        // move by the same amount), and which displays the coming commit covers.
        int anchor;
        int[] startValues = new int[0];
        bool[] touched = new bool[0];
        bool dragging, commitPending;

        int hoverThumb = -1, hoverStatus = -1, hoverToggle = -1, hoverSetup = -1, hoverGammaDisplay = -1, hoverGamma = -1;
        bool hoverLink;
        int wheelDelta;
        int hiddenAt = Environment.TickCount - 10000;
        bool anchoredTop;
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

        public bool Interacting { get { return dragging || commitPending; } }
        public int LastHiddenTick { get { return hiddenAt; } }

        public int ValueOf(int index)
        {
            return index >= 0 && index < displays.Count ? displays[index].Value : 0;
        }

        public bool HdrOnOf(int index)
        {
            return index >= 0 && index < displays.Count && displays[index].HdrOn;
        }

        public static int Snap(int v)
        {
            v = (int)Math.Round(v / (double)Step, MidpointRounding.AwayFromZero) * Step;
            return Math.Max(Min, Math.Min(Max, v));
        }

        bool Multi
        {
            get { return displays.Count > 1; }
        }

        // `showGamma` shows the 2.2 | 2.4 choice; `linked` only matters with several displays.
        public void SetDisplays(IList<PopupDisplay> list, bool nameInStatus, bool showGamma, bool linked)
        {
            var next = new List<PopupDisplay>(list);
            if (next.Count == 0)
                next.Add(new PopupDisplay { Enabled = false });
            bool resized = next.Count != displays.Count || showGamma != gammaRow;
            if (Interacting && next.Count == displays.Count)
            {
                for (int i = 0; i < next.Count; i++)
                    next[i].Value = displays[i].Value; // don't jump back mid-change
            }
            else
            {
                if (Interacting)
                    CancelChange();
                foreach (PopupDisplay d in next)
                    d.Value = Snap(d.Value);
            }
            displays = next;
            this.nameInStatus = nameInStatus;
            gammaRow = showGamma;
            this.linked = linked;
            if (active >= displays.Count)
                active = 0;
            if (Visible && resized)
                Refit();
            Invalidate();
        }

        public void SetStatus(int index, string text, StatusKind kind, bool clickable)
        {
            if (index < 0 || index >= displays.Count)
                return;
            displays[index].Status = text ?? "";
            displays[index].StatusKind = kind;
            displays[index].StatusClickable = clickable;
            Invalidate();
        }

        float HeightDip
        {
            get
            {
                if (Multi)
                    return displays.Count * SectionDip + RowDip;
                return (gammaRow ? GammaTopDip + RowDip : GammaTopDip) + RowDip;
            }
        }

        // The size the popup opens at, for the current displays and scale.
        public Size LayoutSize()
        {
            UpdateScale();
            return new Size(S(WidthDip), S(HeightDip));
        }

        static bool GammaReady(PopupDisplay d)
        {
            return Array.IndexOf(d.GammaAvailable, true) >= 0;
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
            Size size = LayoutSize();
            Screen screen = Screen.FromPoint(anchor);
            Rectangle work = screen.WorkingArea, bounds = screen.Bounds;
            int m = S(12);
            int x = anchor.X - size.Width / 2, y = work.Bottom - size.Height - m;
            anchoredTop = false;
            if (work.Top > bounds.Top)
            {
                y = work.Top + m;
                anchoredTop = true;
            }
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
            ClearHover();
            wheelDelta = 0;
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            foregroundAtShow = Native.GetForegroundWindow();
            focusWatch.Start();
            Invalidate();
        }

        // A display came or went while open: grow or shrink from the taskbar side.
        void Refit()
        {
            Size size = LayoutSize();
            Rectangle b = Bounds;
            Bounds = new Rectangle(b.X, anchoredTop ? b.Y : b.Bottom - size.Height, size.Width, size.Height);
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
            if (e.Button != MouseButtons.Left)
                return;
            int i = SliderAt(e.Location);
            if (i < 0 || !displays[i].Enabled)
                return;
            PrepareChange(i);
            dragging = true;
            commitPending = true; // committing on release also re-applies a mismatched profile
            touched[i] = true;
            commitTimer.Stop();
            Capture = true;
            ChangeValue(i, ValueAt(e.X));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                ChangeValue(anchor, ValueAt(e.X));
                return;
            }
            int section = SectionAt(e.Y);
            if (section >= 0)
                active = section; // keys and the wheel follow the pointer

            int thumb = -1, status = -1, toggle = -1, setup = -1, gammaDisplay = -1, gamma = -1;
            bool link = false;
            for (int i = 0; i < displays.Count; i++)
            {
                PopupDisplay d = displays[i];
                float dx = e.X - ThumbX(i), dy = e.Y - RailY(i);
                if (d.Enabled && dx * dx + dy * dy <= S(12) * S(12))
                    thumb = i;
                if (d.StatusClickable && StatusRect(i).Contains(e.Location))
                    status = i;
                if (d.HdrAvailable && ToggleRect(i).Contains(e.Location))
                    toggle = i;
                if (gammaRow && !GammaReady(d) && SetupRect(i).Contains(e.Location))
                    setup = i;
                if (gammaRow && GammaReady(d))
                {
                    int segment = Draw.SegmentAt(SegmentsRect(i), d.GammaAvailable.Length, e.Location);
                    if (segment >= 0)
                    {
                        gammaDisplay = i;
                        gamma = segment;
                    }
                }
            }
            if (Multi)
                link = LinkRect.Contains(e.Location);

            if (thumb != hoverThumb || status != hoverStatus || toggle != hoverToggle || setup != hoverSetup ||
                gammaDisplay != hoverGammaDisplay || gamma != hoverGamma || link != hoverLink)
            {
                hoverThumb = thumb;
                hoverStatus = status;
                hoverToggle = toggle;
                hoverSetup = setup;
                hoverGammaDisplay = gammaDisplay;
                hoverGamma = gamma;
                hoverLink = link;
                Invalidate();
            }
            Cursor = status >= 0 ? Cursors.Hand : Cursors.Default;
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
                return;
            }
            if (Multi && LinkRect.Contains(e.Location))
            {
                ToggleLink();
                return;
            }
            for (int i = 0; i < displays.Count; i++)
            {
                PopupDisplay d = displays[i];
                if (d.StatusClickable && StatusRect(i).Contains(e.Location))
                {
                    var handler = StatusClicked;
                    if (handler != null)
                        handler(i);
                    return;
                }
                if (d.HdrAvailable && ToggleRect(i).Contains(e.Location))
                {
                    ToggleHdr(i);
                    return;
                }
                if (!gammaRow)
                    continue;
                if (GammaReady(d))
                {
                    int segment = Draw.SegmentAt(SegmentsRect(i), d.GammaAvailable.Length, e.Location);
                    if (segment >= 0)
                    {
                        ChooseGamma(i, segment);
                        return;
                    }
                }
                else if (SetupRect(i).Contains(e.Location))
                {
                    var handler = SetupClicked;
                    if (handler != null)
                        handler(i);
                    return;
                }
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
            ClearHover();
            Invalidate();
        }

        void ClearHover()
        {
            hoverThumb = hoverStatus = hoverToggle = hoverSetup = hoverGammaDisplay = hoverGamma = -1;
            hoverLink = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int section = SectionAt(e.Y);
            if (section >= 0)
                active = section;
            if (!displays[active].Enabled)
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
            PopupDisplay d = displays[active];
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
                    if (d.HdrAvailable)
                        ToggleHdr(active);
                    return true;
                case Keys.G:
                    for (int i = 1; i < d.GammaAvailable.Length; i++)
                    {
                        int next = (Array.IndexOf(ProfileGenerator.Gammas, d.Gamma) + i) % d.GammaAvailable.Length;
                        if (d.GammaAvailable[next])
                        {
                            ChooseGamma(active, next);
                            break;
                        }
                    }
                    return true;
                case Keys.L:
                    if (Multi)
                        ToggleLink();
                    return true;
            }
            if (d.Enabled)
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
                        KeyboardChange(active, Min);
                        return true;
                    case Keys.End:
                        KeyboardChange(active, Max);
                        return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Nudge(int steps)
        {
            KeyboardChange(active, displays[active].Value + steps * Step);
        }

        // Keyboard and wheel changes preview at once and commit after a short pause.
        void KeyboardChange(int index, int v)
        {
            PrepareChange(index);
            if (!ChangeValue(index, v))
                return;
            commitPending = true;
            touched[index] = true;
            commitTimer.Stop();
            commitTimer.Start();
        }

        // Starts a change on `index`, or carries on the one in progress if it's the same display.
        void PrepareChange(int index)
        {
            if (commitPending && index != anchor)
                FlushCommit();
            anchor = active = index;
            if (commitPending)
                return;
            startValues = new int[displays.Count];
            touched = new bool[displays.Count];
            for (int i = 0; i < displays.Count; i++)
                startValues[i] = displays[i].Value;
        }

        // Linked displays follow by the same number of steps from where they started, each stopping at 0 and 100.
        bool ChangeValue(int index, int v)
        {
            v = Snap(v);
            int delta = v - startValues[index];
            bool changed = false;
            for (int i = 0; i < displays.Count; i++)
            {
                if (i != index && !(linked && displays[i].Enabled))
                    continue;
                int next = i == index ? v : Snap(startValues[i] + delta);
                if (next == displays[i].Value)
                    continue;
                displays[i].Value = next;
                touched[i] = true;
                changed = true;
                var handler = Previewed;
                if (handler != null)
                    handler(i, next);
            }
            if (changed)
                Invalidate();
            return changed;
        }

        void FlushCommit()
        {
            commitTimer.Stop();
            if (!commitPending)
                return;
            commitPending = false;
            var handler = Committed;
            for (int i = 0; i < displays.Count && i < touched.Length; i++)
                if (touched[i] && handler != null)
                    handler(i, displays[i].Value);
        }

        void CancelChange()
        {
            commitTimer.Stop();
            dragging = commitPending = false;
            Capture = false;
        }

        void ToggleHdr(int index)
        {
            PopupDisplay d = displays[index];
            d.HdrOn = !d.HdrOn;
            Invalidate();
            var handler = HdrToggled;
            if (handler != null)
                handler(index, d.HdrOn);
        }

        void ChooseGamma(int index, int gammaIndex)
        {
            PopupDisplay d = displays[index];
            string chosen = ProfileGenerator.Gammas[gammaIndex];
            if (!d.GammaAvailable[gammaIndex] || chosen == d.Gamma)
                return;
            d.Gamma = chosen;
            Invalidate();
            var handler = GammaChanged;
            if (handler != null)
                handler(index, chosen);
        }

        void ToggleLink()
        {
            FlushCommit();
            linked = !linked;
            Invalidate();
            var handler = LinkToggled;
            if (handler != null)
                handler(linked);
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

        // Top of a display's block; there's only one block when there's one display.
        float TopDip(int index)
        {
            return Multi ? index * SectionDip : 0;
        }

        int SectionAt(int y)
        {
            if (!Multi)
                return 0;
            int i = (int)(y / (SectionDip * scale));
            return i >= 0 && i < displays.Count ? i : -1;
        }

        float RailLeft { get { return S(Pad + 10); } }
        float RailRight { get { return ClientSize.Width - S(Pad + 10); } }
        float RailY(int index) { return S(TopDip(index) + RailYDip); }

        float ThumbX(int index)
        {
            return RailLeft + (RailRight - RailLeft) * (displays[index].Value - Min) / (Max - Min);
        }

        Rectangle StatusRect(int index)
        {
            return new Rectangle(S(Pad), S(TopDip(index) + StatusTopDip), ClientSize.Width - S(2 * Pad), S(18));
        }

        // One display: the gamma row, then the HDR content row as the footer. Several: one row per display with both.
        Rectangle GammaRect
        {
            get { return new Rectangle(0, S(GammaTopDip), ClientSize.Width, S(RowDip)); }
        }

        Rectangle FooterRect
        {
            get
            {
                int top = S(gammaRow ? GammaTopDip + RowDip : GammaTopDip);
                return new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
            }
        }

        Rectangle ControlsRect(int index)
        {
            return new Rectangle(0, S(TopDip(index) + GammaTopDip), ClientSize.Width, S(ControlsDip));
        }

        Rectangle LinkRect
        {
            get
            {
                int top = S(displays.Count * SectionDip);
                return new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
            }
        }

        // "2.2 | 2.4", or the Set up button when no profiles exist yet.
        RectangleF SegmentsRect(int index)
        {
            float w = S(104), h = S(32);
            if (!Multi)
            {
                Rectangle row = GammaRect;
                return new RectangleF(ClientSize.Width - S(Pad) - w, row.Y + (row.Height - h) / 2f, w, h);
            }
            Rectangle r = ControlsRect(index);
            return new RectangleF(S(Pad), r.Y + (r.Height - h) / 2f, w, h);
        }

        Rectangle SetupRect(int index)
        {
            return Multi ? Rectangle.Round(SegmentsRect(index)) : GammaRect;
        }

        // Where a click toggles the HDR content profile.
        Rectangle ToggleRect(int index)
        {
            if (!Multi)
                return FooterRect;
            Rectangle r = ControlsRect(index);
            int left = gammaRow ? (int)SegmentsRect(index).Right + S(8) : 0;
            return new Rectangle(left, r.Y, r.Right - left, r.Height);
        }

        int SliderAt(Point p)
        {
            for (int i = 0; i < displays.Count; i++)
                if (Math.Abs(p.Y - RailY(i)) <= S(16) && p.X >= RailLeft - S(12) && p.X <= RailRight + S(12))
                    return i;
            return -1;
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

            if (!Multi)
            {
                PopupDisplay d = displays[0];
                DrawHeader(g, 0, Theme.GlyphBrightness, "SDR content brightness");
                DrawSlider(g, 0);
                DrawStatus(g, 0, nameInStatus ? d.Name : null);
                if (gammaRow)
                    DrawGammaRow(g, w, d);
                DrawSwitchRow(g, FooterRect, "HDR content", d.HdrHint, d.HdrOn, d.HdrAvailable, hoverToggle == 0);
                return;
            }

            for (int i = 0; i < displays.Count; i++)
            {
                if (i > 0)
                    using (var pen = new Pen(theme.Divider))
                        g.DrawLine(pen, 0, S(TopDip(i)), w, S(TopDip(i)));
                DrawHeader(g, i, Theme.GlyphDisplay, displays[i].Name);
                DrawSlider(g, i);
                DrawStatus(g, i, null);
                DrawControls(g, w, i);
            }
            DrawSwitchRow(g, LinkRect, "Link displays",
                linked ? "All displays move together" : "Shortcuts follow the active window",
                linked, true, hoverLink);
        }

        void DrawHeader(Graphics g, int index, string glyph, string title)
        {
            PopupDisplay d = displays[index];
            int w = ClientSize.Width, top = S(TopDip(index) + 16), rowH = S(24);
            TextRenderer.DrawText(g, glyph, glyphFont, new Rectangle(S(Pad), top, S(20), rowH), theme.Text, Line);
            TextRenderer.DrawText(g, title ?? "", titleFont,
                new Rectangle(S(Pad + 28), top, w - S(2 * Pad + 28 + 60 + 64), rowH), theme.Text, Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, HdrDisplay.NitsFromSlider(d.Value) + " nits", smallFont,
                new Rectangle(w - S(Pad + 60 + 64), top + S(2), S(56), rowH), theme.Tertiary, Line | TextFormatFlags.Right);
            TextRenderer.DrawText(g, d.Value.ToString(), valueFont,
                new Rectangle(w - S(Pad + 60), top - S(2), S(60), rowH + S(4)),
                d.Enabled ? theme.Text : theme.Tertiary, Line | TextFormatFlags.Right);
        }

        void DrawGammaRow(Graphics g, int w, PopupDisplay d)
        {
            Rectangle r = GammaRect;
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, r);
            using (var pen = new Pen(theme.Divider))
                g.DrawLine(pen, 0, r.Y, w, r.Y);

            bool ready = GammaReady(d);
            int textW = w - S(2 * Pad + 116);
            TextRenderer.DrawText(g, "Gamma", titleFont, new Rectangle(S(Pad), r.Y + S(9), textW, S(20)), theme.Text,
                Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, ready ? "Pure power curve for SDR apps" : "Create profiles for 2.2 and 2.4", smallFont,
                new Rectangle(S(Pad), r.Y + S(29), textW, S(16)), theme.Secondary, Line | TextFormatFlags.EndEllipsis);
            DrawGammaChoice(g, 0, d);
        }

        void DrawGammaChoice(Graphics g, int index, PopupDisplay d)
        {
            RectangleF seg = SegmentsRect(index);
            if (GammaReady(d))
                Draw.Segments(g, theme, seg, ProfileGenerator.Gammas, Array.IndexOf(ProfileGenerator.Gammas, d.Gamma),
                    hoverGammaDisplay == index ? hoverGamma : -1, d.GammaAvailable, titleFont, scale);
            else
            {
                Draw.FillRoundRect(g, hoverSetup == index ? theme.ControlHover : theme.ControlFill, theme.ControlBorder, seg, 4 * scale,
                    Math.Max(1f, scale));
                TextRenderer.DrawText(g, "Set up…", titleFont, Rectangle.Round(seg), theme.Text, Draw.Centered);
            }
        }

        // Several displays: the gamma choice on the left, the HDR content switch on the right.
        void DrawControls(Graphics g, int w, int index)
        {
            PopupDisplay d = displays[index];
            Rectangle r = ControlsRect(index);
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, r);
            using (var pen = new Pen(theme.Divider))
                g.DrawLine(pen, 0, r.Y, w, r.Y);
            if (gammaRow)
                DrawGammaChoice(g, index, d);

            float sw = S(40), sh = S(20);
            float sx = w - S(Pad) - sw, sy = r.Y + (r.Height - sh) / 2f;
            Rectangle label = new Rectangle(ToggleRect(index).X, r.Y, (int)sx - S(10) - ToggleRect(index).X, r.Height);
            if (!gammaRow)
                label = new Rectangle(S(Pad), r.Y, (int)sx - S(10) - S(Pad), r.Height);
            TextRenderer.DrawText(g, "HDR content", titleFont, label, d.HdrAvailable ? theme.Text : theme.Tertiary,
                Line | TextFormatFlags.EndEllipsis | (gammaRow ? TextFormatFlags.Right : TextFormatFlags.Left));
            DrawSwitch(g, sx, sy, d.HdrOn && d.HdrAvailable, d.HdrAvailable, hoverToggle == index);
        }

        void DrawSlider(Graphics g, int index)
        {
            PopupDisplay d = displays[index];
            float x0 = RailLeft, x1 = RailRight, y = RailY(index), tx = ThumbX(index);
            Color fill = d.Enabled ? theme.Accent : theme.Disabled;
            float railH = S(4);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // A tick for every step of 5, longer at the quarter marks.
            using (var pen = new Pen(Color.FromArgb(120, theme.Tertiary), Math.Max(1f, scale)))
            {
                for (int i = 0; i <= (Max - Min) / Step; i++)
                {
                    float x = x0 + (x1 - x0) * i * Step / (Max - Min);
                    g.DrawLine(pen, x, y + S(14), x, y + S(14) + (i % 5 == 0 ? S(5) : S(3)));
                }
            }

            FillPill(g, d.Enabled ? theme.Rail : theme.Disabled, x0, y - railH / 2, x1 - x0, railH);
            if (tx > x0)
                FillPill(g, fill, x0, y - railH / 2, tx - x0, railH);

            float outer = S(10);
            float inner = dragging && anchor == index ? S(5) : hoverThumb == index ? S(7) : S(6);
            using (var brush = new SolidBrush(theme.ThumbFill))
                g.FillEllipse(brush, tx - outer, y - outer, outer * 2, outer * 2);
            using (var pen = new Pen(theme.ThumbBorder, Math.Max(1f, scale)))
                g.DrawEllipse(pen, tx - outer, y - outer, outer * 2, outer * 2);
            using (var brush = new SolidBrush(fill))
                g.FillEllipse(brush, tx - inner, y - inner, inner * 2, inner * 2);
            g.SmoothingMode = SmoothingMode.None;
        }

        void DrawStatus(Graphics g, int index, string name)
        {
            PopupDisplay d = displays[index];
            Rectangle r = StatusRect(index);
            string glyph = null;
            Color color = theme.Secondary, glyphColor = theme.Secondary;
            switch (d.StatusKind)
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
            if (!string.IsNullOrEmpty(name))
            {
                int nameW = Math.Min(TextRenderer.MeasureText(g, name, smallFont, Size.Empty, Line).Width, r.Width / 3);
                TextRenderer.DrawText(g, name, smallFont, new Rectangle(r.Right - nameW, r.Y, nameW, r.Height),
                    theme.Tertiary, Line | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
                right -= nameW + S(12);
            }
            TextRenderer.DrawText(g, d.Status, d.StatusClickable && hoverStatus == index ? linkFont : smallFont,
                new Rectangle(x, r.Y, right - x, r.Height), color, Line | TextFormatFlags.EndEllipsis);
        }

        // A footer row: title and hint on the left, a Windows 11 toggle switch with its On/Off label on the right.
        void DrawSwitchRow(Graphics g, Rectangle r, string title, string hint, bool on, bool available, bool hover)
        {
            int w = ClientSize.Width;
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, r);
            using (var pen = new Pen(theme.Divider))
                g.DrawLine(pen, 0, r.Y, w, r.Y);

            Color text = available ? theme.Text : theme.Tertiary;
            int textW = w - S(2 * Pad + 96);
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(S(Pad), r.Y + S(9), textW, S(20)), text,
                Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, hint ?? "", smallFont, new Rectangle(S(Pad), r.Y + S(29), textW, S(16)),
                available ? theme.Secondary : theme.Tertiary, Line | TextFormatFlags.EndEllipsis);

            on = on && available;
            float sw = S(40), sh = S(20);
            float sx = w - S(Pad) - sw, sy = r.Y + (r.Height - sh) / 2f;
            TextRenderer.DrawText(g, on ? "On" : "Off", titleFont,
                new Rectangle((int)sx - S(44), (int)sy, S(32), (int)sh), text, Line | TextFormatFlags.Right);
            DrawSwitch(g, sx, sy, on, available, hover);
        }

        void DrawSwitch(Graphics g, float sx, float sy, bool on, bool available, bool hover)
        {
            float sw = S(40), sh = S(20);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float knob = hover ? S(14) : S(12);
            Color knobColor;
            if (on)
            {
                FillPill(g, theme.Accent, sx, sy, sw, sh);
                knobColor = theme.OnAccent;
            }
            else
            {
                knobColor = available ? theme.Secondary : theme.Disabled;
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
