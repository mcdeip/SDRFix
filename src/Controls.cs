using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SdrBrightness
{
    static class Draw
    {
        public const TextFormatFlags Centered = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRoundRect(Graphics g, Color fill, Color border, RectangleF r, float radius, float penWidth)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var inner = new RectangleF(r.X + penWidth / 2, r.Y + penWidth / 2, r.Width - penWidth, r.Height - penWidth);
            using (var path = RoundRect(inner, radius))
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
                if (border != Color.Empty)
                    using (var pen = new Pen(border, penWidth))
                        g.DrawPath(pen, path);
            }
            g.SmoothingMode = mode;
        }

        public static void FocusRing(Graphics g, Theme theme, RectangleF r, float radius, float scale)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Math.Max(1f, 2 * scale);
            using (var pen = new Pen(theme.Text, w))
            using (var path = RoundRect(new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w), radius + w))
                g.DrawPath(pen, path);
            g.SmoothingMode = mode;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Segmented choice ("2.2 | 2.4"): the selected segment is filled with the accent color.
        public static void Segments(Graphics g, Theme theme, RectangleF r, string[] labels, int selected, int hover,
            bool[] enabled, Font font, float scale)
        {
            float pen = Math.Max(1f, scale), radius = 4 * scale, inset = 3 * scale;
            FillRoundRect(g, theme.ControlFill, theme.ControlBorder, r, radius, pen);
            for (int i = 0; i < labels.Length; i++)
            {
                RectangleF seg = SegmentRect(r, labels.Length, i);
                bool on = enabled == null || enabled[i];
                var inner = new RectangleF(seg.X + inset, seg.Y + inset, seg.Width - 2 * inset, seg.Height - 2 * inset);
                Color text = on ? theme.Text : theme.Disabled;
                if (i == selected && on)
                {
                    FillRoundRect(g, theme.Accent, Color.Empty, inner, radius - 1, pen);
                    text = theme.OnAccent;
                }
                else if (i == hover && on)
                    FillRoundRect(g, theme.ControlHover, Color.Empty, inner, radius - 1, pen);
                TextRenderer.DrawText(g, labels[i], font, Rectangle.Round(seg), text, Centered);
            }
        }

        public static RectangleF SegmentRect(RectangleF r, int count, int index)
        {
            float w = r.Width / count;
            return new RectangleF(r.X + w * index, r.Y, w, r.Height);
        }

        public static int SegmentAt(RectangleF r, int count, Point p)
        {
            if (!r.Contains(p))
                return -1;
            return Math.Min(count - 1, (int)((p.X - r.X) / (r.Width / count)));
        }
    }

    // Windows 11 style push button; the accent-colored one is the default action.
    sealed class FlatButton : Control, IButtonControl
    {
        readonly Theme theme;
        readonly float scale;
        bool hover, pressed;

        public bool Primary;

        public FlatButton(Theme theme, float scale)
        {
            this.theme = theme;
            this.scale = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value)
        {
        }

        public void PerformClick()
        {
            if (Enabled)
                OnClick(EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter)
                PerformClick();
            else if (e.KeyCode == Keys.Space)
                SetPressed(true);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space && pressed)
            {
                SetPressed(false);
                PerformClick();
            }
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
                SetPressed(true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            SetPressed(false);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            SetPressed(false);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        void SetPressed(bool value)
        {
            if (pressed == value)
                return;
            pressed = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            float pad = 2 * scale, radius = 4 * scale, pen = Math.Max(1f, scale);
            var r = new RectangleF(pad, pad, Width - 2 * pad, Height - 2 * pad);

            Color fill, border, text;
            if (Primary)
            {
                fill = !Enabled ? theme.Disabled
                    : pressed ? Draw.Mix(theme.Accent, BackColor, 0.2f)
                    : hover ? Draw.Mix(theme.Accent, BackColor, 0.1f) : theme.Accent;
                border = Color.Empty;
                text = Enabled ? theme.OnAccent : theme.Tertiary;
            }
            else
            {
                fill = pressed ? theme.ControlPressed : hover && Enabled ? theme.ControlHover : theme.ControlFill;
                border = theme.ControlBorder;
                text = Enabled ? (pressed ? theme.Secondary : theme.Text) : theme.Tertiary;
            }
            Draw.FillRoundRect(g, fill, border, r, radius, pen);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), text, Draw.Centered | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                Draw.FocusRing(g, theme, new RectangleF(0, 0, Width, Height), radius, scale);
        }
    }

    // Keyboard- and mouse-operable segmented choice, e.g. "2.2 | 2.4".
    sealed class SegmentPicker : Control
    {
        readonly Theme theme;
        readonly float scale;
        readonly string[] options;
        int selected, hover = -1;

        public event EventHandler Changed;

        public SegmentPicker(Theme theme, float scale, string[] options)
        {
            this.theme = theme;
            this.scale = scale;
            this.options = options;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public string Value
        {
            get { return options[selected]; }
            set
            {
                selected = Math.Max(0, Array.IndexOf(options, value));
                Invalidate();
            }
        }

        RectangleF Inner
        {
            get
            {
                float pad = 2 * scale;
                return new RectangleF(pad, pad, Width - 2 * pad, Height - 2 * pad);
            }
        }

        void Choose(int index)
        {
            index = Math.Max(0, Math.Min(options.Length - 1, index));
            if (index == selected)
                return;
            selected = index;
            Invalidate();
            var handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
                Choose(selected - 1);
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
                Choose(selected + 1);
            else if (e.KeyCode == Keys.Space)
                Choose((selected + 1) % options.Length);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Draw.SegmentAt(Inner, options.Length, e.Location);
            if (h != hover)
            {
                hover = h;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int index = Draw.SegmentAt(Inner, options.Length, e.Location);
            if (e.Button == MouseButtons.Left && index >= 0)
                Choose(index);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Draw.Segments(e.Graphics, theme, Inner, options, selected, hover, null, Font, scale);
            if (Focused && ShowFocusCues)
                Draw.FocusRing(e.Graphics, theme, new RectangleF(0, 0, Width, Height), 4 * scale, scale);
        }
    }
}
