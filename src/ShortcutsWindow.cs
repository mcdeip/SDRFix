using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SdrBrightness
{
    // Click it, then press a key combination. Backspace or Delete clears it.
    sealed class HotkeyBox : Control
    {
        const int WM_KEYUP = 0x101, WM_SYSKEYUP = 0x105, WM_CHAR = 0x102, WM_SYSCHAR = 0x106;

        readonly Theme theme;
        readonly float scale;
        Hotkey hotkey;
        int held; // modifiers down while no key has been pressed yet
        bool hover;

        public event EventHandler Changed;
        public event EventHandler Rejected; // a typing key without a modifier

        public HotkeyBox(Theme theme, float scale)
        {
            this.theme = theme;
            this.scale = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public Hotkey Hotkey
        {
            get { return hotkey; }
            set
            {
                hotkey = value;
                Invalidate();
            }
        }

        static int CurrentModifiers()
        {
            Keys keys = ModifierKeys;
            int mods = 0;
            if ((keys & Keys.Alt) != 0)
                mods |= Hotkey.Alt;
            if ((keys & Keys.Control) != 0)
                mods |= Hotkey.Control;
            if ((keys & Keys.Shift) != 0)
                mods |= Hotkey.Shift;
            if (Native.GetKeyState(0x5B) < 0 || Native.GetKeyState(0x5C) < 0)
                mods |= Hotkey.Win;
            return mods;
        }

        // Every key press reaches here first, including Alt combinations and F10.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            int mods = CurrentModifiers();
            switch (key)
            {
                case Keys.ShiftKey:
                case Keys.ControlKey:
                case Keys.Menu:
                case Keys.LWin:
                case Keys.RWin:
                    held = mods;
                    Invalidate();
                    return true;
            }
            if (mods == 0)
            {
                if (key == Keys.Tab || key == Keys.Escape)
                    return base.ProcessCmdKey(ref msg, keyData);
                if (key == Keys.Back || key == Keys.Delete)
                {
                    Set(new Hotkey(0, Keys.None));
                    return true;
                }
                if (IsTypingKey(key))
                {
                    var handler = Rejected;
                    if (handler != null)
                        handler(this, EventArgs.Empty);
                    return true;
                }
            }
            Set(new Hotkey(mods, key));
            return true;
        }

        // Keys that would stop typing everywhere if they were taken without a modifier.
        static bool IsTypingKey(Keys key)
        {
            return (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                   (key >= Keys.NumPad0 && key <= Keys.Divide) || key >= Keys.OemSemicolon ||
                   key == Keys.Space || key == Keys.Return || key == Keys.Left || key == Keys.Right ||
                   key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End ||
                   key == Keys.Prior || key == Keys.Next || key == Keys.Insert;
        }

        void Set(Hotkey value)
        {
            held = 0;
            hotkey = value;
            Invalidate();
            var handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYUP || m.Msg == WM_SYSKEYUP)
            {
                held = CurrentModifiers();
                Invalidate();
                // Releasing Alt alone would otherwise open the window menu and swallow the next key.
                if (m.Msg == WM_SYSKEYUP)
                    return;
            }
            if (m.Msg == WM_CHAR || m.Msg == WM_SYSCHAR)
                return; // no beeps for keys that were captured
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            held = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            float pad = 2 * scale, pen = Math.Max(1f, scale);
            var r = new RectangleF(pad, pad, Width - 2 * pad, Height - 2 * pad);
            Draw.FillRoundRect(g, Focused ? theme.InputFocused : hover ? theme.ControlHover : theme.ControlFill,
                theme.ControlBorder, r, 4 * scale, pen);
            using (var brush = new SolidBrush(Focused ? theme.Accent : theme.Tertiary))
            {
                float lineH = Focused ? 2 * scale : pen;
                g.FillRectangle(brush, r.X + 3 * scale, r.Bottom - lineH, r.Width - 6 * scale, lineH);
            }

            string text;
            Color color = theme.Text;
            if (Focused && held != 0)
                text = Hotkey.ModifierText(held) + "…";
            else if (!hotkey.IsEmpty)
                text = hotkey.ToString();
            else
            {
                text = Focused ? "Press a shortcut" : "None";
                color = theme.Tertiary;
            }
            TextRenderer.DrawText(g, text, Font, Rectangle.Round(r), color, Draw.Centered | TextFormatFlags.EndEllipsis);
        }
    }

    // Rebind the global shortcuts and pick how far one press moves the slider.
    sealed class ShortcutsWindow : Form
    {
        const float WidthDip = 480, HeightDip = 624, Pad = 24, FirstRowDip = 112, RowDip = 54, FooterDip = 560;
        const float SectionDip = FirstRowDip + 4 * RowDip + 6, ShotsDip = SectionDip + 34;
        const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        // One per HotkeyManager.Actions entry.
        static readonly string[] Labels = { "Brightness down", "Brightness up", "HDR content profile", "Active window", "Whole desktop", "Region" };
        static readonly string[] Hints =
        {
            "Moves the slider down one step",
            "Moves the slider up one step",
            "Turns the HDR profile on or off",
            "The window in front",
            "Every display",
            "Drag to pick an area",
        };

        readonly HotkeyManager manager;
        readonly Theme theme = Theme.Current();
        readonly float scale;
        readonly Font titleFont, bodyFont, labelFont, smallFont, glyphFont, sectionFont;
        readonly Dictionary<HotkeyAction, HotkeyBox> boxes = new Dictionary<HotkeyAction, HotkeyBox>();
        readonly SegmentPicker stepPicker;
        readonly FlatButton saveButton, cancelButton, defaultsButton;
        string message = "";
        StatusKind messageKind;

        public ShortcutsWindow(HotkeyManager manager)
        {
            this.manager = manager;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                scale = g.DpiX / 96f;

            Text = "SDRFix shortcuts";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }
            BackColor = theme.Background;
            ForeColor = theme.Text;
            ClientSize = new Size(S(WidthDip), S(HeightDip));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            titleFont = new Font("Segoe UI Semibold", 20 * scale, GraphicsUnit.Pixel);
            bodyFont = new Font("Segoe UI", 13 * scale, GraphicsUnit.Pixel);
            labelFont = new Font("Segoe UI", 14 * scale, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
            glyphFont = new Font(Theme.IconFont, 12 * scale, GraphicsUnit.Pixel);
            sectionFont = new Font("Segoe UI Semibold", 14 * scale, GraphicsUnit.Pixel);

            int w = ClientSize.Width;
            for (int i = 0; i < HotkeyManager.Actions.Length; i++)
            {
                HotkeyAction action = HotkeyManager.Actions[i];
                var box = new HotkeyBox(theme, scale)
                {
                    Font = labelFont,
                    BackColor = theme.Background,
                    Hotkey = Settings.GetHotkey(action),
                    Bounds = new Rectangle(w - S(Pad - 2) - S(196), S(ActionTop(i)) + S(2), S(196), S(36)),
                };
                box.Changed += delegate { SetMessage("", StatusKind.Info); };
                box.Rejected += delegate { SetMessage("Add Ctrl, Alt, Shift or Win to that key", StatusKind.Error); };
                boxes[action] = box;
                Controls.Add(box);
            }

            var steps = Array.ConvertAll(Settings.HotkeySteps, s => s.ToString(CultureInfo.InvariantCulture));
            stepPicker = new SegmentPicker(theme, scale, steps)
            {
                Font = labelFont,
                BackColor = theme.Background,
                Value = Settings.HotkeyStep.ToString(CultureInfo.InvariantCulture),
                Bounds = new Rectangle(w - S(Pad - 2) - S(214), S(FirstRowDip + 3 * RowDip) + S(2), S(214), S(36)),
            };
            Controls.Add(stepPicker);

            int footer = S(FooterDip), buttonH = S(36), buttonY = footer + (ClientSize.Height - footer - buttonH) / 2;
            cancelButton = new FlatButton(theme, scale) { Text = "Cancel", Font = bodyFont, BackColor = theme.Footer };
            cancelButton.Bounds = new Rectangle(w - S(Pad - 2) - S(104), buttonY, S(104), buttonH);
            saveButton = new FlatButton(theme, scale) { Text = "Save", Primary = true, Font = bodyFont, BackColor = theme.Footer };
            saveButton.Bounds = new Rectangle(cancelButton.Left - S(4) - S(104), buttonY, S(104), buttonH);
            defaultsButton = new FlatButton(theme, scale) { Text = "Use defaults", Font = bodyFont, BackColor = theme.Footer };
            defaultsButton.Bounds = new Rectangle(S(Pad - 2), buttonY, S(124), buttonH);
            saveButton.Click += delegate { Save(); };
            cancelButton.Click += delegate { Close(); };
            defaultsButton.Click += delegate
            {
                foreach (HotkeyAction action in HotkeyManager.Actions)
                    boxes[action].Hotkey = Hotkey.Default(action);
                stepPicker.Value = Settings.HotkeySteps[0].ToString(CultureInfo.InvariantCulture);
                SetMessage("", StatusKind.Info);
            };
            Controls.Add(saveButton);
            Controls.Add(cancelButton);
            Controls.Add(defaultsButton);
            CancelButton = cancelButton;
            ActiveControl = saveButton;

            if (manager.Failed.Count > 0)
                SetMessage(TakenMessage(manager.Failed), StatusKind.Warning);
            // Registered shortcuts would never reach the capture boxes, so they're off while this is open.
            manager.Pause(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = theme.Dark ? 1 : 0;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            manager.Pause(false);
            base.OnFormClosed(e);
        }

        int S(float dip)
        {
            return (int)Math.Round(dip * scale);
        }

        // The brightness and HDR rows come first, then step per press, then the screenshot section.
        static float ActionTop(int index)
        {
            return index < 3 ? FirstRowDip + index * RowDip : ShotsDip + (index - 3) * RowDip;
        }

        static string TakenMessage(IList<HotkeyAction> actions)
        {
            var names = new List<string>();
            foreach (HotkeyAction action in actions)
                names.Add(Settings.GetHotkey(action).ToString());
            return string.Join(", ", names.ToArray()) + (names.Count == 1 ? " is" : " are") + " already used by another app";
        }

        void SetMessage(string text, StatusKind kind)
        {
            message = text;
            messageKind = kind;
            Invalidate(new Rectangle(0, S(FooterDip - 30), ClientSize.Width, S(22)));
        }

        void Save()
        {
            var seen = new Dictionary<Hotkey, HotkeyAction>();
            foreach (HotkeyAction action in HotkeyManager.Actions)
            {
                Hotkey hotkey = boxes[action].Hotkey;
                HotkeyAction other;
                if (!hotkey.IsEmpty && seen.TryGetValue(hotkey, out other))
                {
                    SetMessage(hotkey + " is set for two actions", StatusKind.Error);
                    return;
                }
                seen[hotkey] = action;
            }

            foreach (HotkeyAction action in HotkeyManager.Actions)
                Settings.SetHotkey(action, boxes[action].Hotkey);
            Settings.HotkeyStep = int.Parse(stepPicker.Value, CultureInfo.InvariantCulture);

            manager.Pause(false);
            if (manager.Failed.Count == 0)
            {
                Close();
                return;
            }
            SetMessage(TakenMessage(manager.Failed) + " · pick another", StatusKind.Error);
            manager.Pause(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width, x = S(Pad), textW = w - S(2 * Pad);
            g.Clear(theme.Background);

            TextRenderer.DrawText(g, "Keyboard shortcuts", titleFont, new Rectangle(x, S(20), textW, S(30)), theme.Text, Line);
            TextRenderer.DrawText(g,
                "These work in any app, including full-screen games. Click a shortcut and press new keys; Backspace clears it.",
                bodyFont, new Rectangle(x, S(58), textW, S(40)), theme.Secondary, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int labelW = textW - S(220);
            for (int i = 0; i < Labels.Length; i++)
                DrawRow(g, S(ActionTop(i)), Labels[i], Hints[i], labelW);
            DrawRow(g, S(FirstRowDip + 3 * RowDip), "Step per press", "Slider units · profiles exist every 5", labelW);

            int section = S(SectionDip);
            TextRenderer.DrawText(g, "Screenshots", sectionFont, new Rectangle(x, section, textW, S(20)), theme.Text, Line);
            int titleW = TextRenderer.MeasureText(g, "Screenshots", sectionFont, Size.Empty, Line).Width;
            TextRenderer.DrawText(g, "  ·  copied to the clipboard, as they'd look in SDR", smallFont,
                new Rectangle(x + titleW, section, textW - titleW, S(20)), theme.Secondary, Line | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrEmpty(message))
            {
                var r = new Rectangle(x, S(FooterDip - 30), textW, S(20));
                bool error = messageKind == StatusKind.Error;
                Color color = error ? theme.Error : theme.Warning;
                TextRenderer.DrawText(g, error ? Theme.GlyphError : Theme.GlyphWarning, glyphFont,
                    new Rectangle(r.X, r.Y, S(16), r.Height), color, Line);
                TextRenderer.DrawText(g, message, smallFont, new Rectangle(r.X + S(20), r.Y, r.Width - S(20), r.Height), color,
                    Line | TextFormatFlags.EndEllipsis);
            }

            int footer = S(FooterDip);
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, 0, footer, w, ClientSize.Height - footer);
            using (var divider = new Pen(theme.Divider))
                g.DrawLine(divider, 0, footer, w, footer);
        }

        void DrawRow(Graphics g, int top, string label, string hint, int width)
        {
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(S(Pad), top + S(1), width, S(20)), theme.Text, Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, hint, smallFont, new Rectangle(S(Pad), top + S(21), width, S(18)), theme.Secondary,
                Line | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var font in new[] { titleFont, bodyFont, labelFont, smallFont, glyphFont, sectionFont })
                    font.Dispose();
            base.Dispose(disposing);
        }
    }
}
