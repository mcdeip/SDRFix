using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SdrBrightness
{
    // Creates setup's profile sets for a display: pick the gamma to start with and enter its peak brightness.
    sealed class SetupWindow : Form
    {
        const float WidthDip = 460, HeightDip = 364, Pad = 24;
        const float GammaRowDip = 128, PeakRowDip = 188, InfoDip = 248, MessageDip = 270, FooterDip = 300;
        const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        public event Action<string> ProfilesCreated; // the gamma to switch to

        readonly HdrDisplay display;
        readonly Primaries primaries;
        readonly Theme theme = Theme.Current();
        readonly float scale;
        readonly Font titleFont, bodyFont, labelFont, smallFont, glyphFont;
        readonly SegmentPicker picker;
        readonly TextBox peakBox;
        readonly FlatButton createButton, closeButton;
        string message = "";
        StatusKind messageKind;

        public SetupWindow(HdrDisplay display, string gamma, int peakNits, bool firstRun)
        {
            this.display = display;
            primaries = display != null ? Primaries.ForDisplay(display.DevicePath) : Primaries.Srgb();
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                scale = g.DpiX / 96f;

            Text = "SDRFix setup";
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

            int w = ClientSize.Width;
            picker = new SegmentPicker(theme, scale, ProfileGenerator.Gammas)
            {
                Font = labelFont,
                Value = gamma,
                BackColor = theme.Background,
                Bounds = new Rectangle(w - S(Pad + 110), S(GammaRowDip), S(110), S(40)),
            };

            Rectangle field = FieldRect;
            peakBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = labelFont,
                Text = peakNits.ToString(CultureInfo.InvariantCulture),
                TextAlign = HorizontalAlignment.Right,
                MaxLength = 5,
                BackColor = theme.ControlFill,
                ForeColor = theme.Text,
            };
            peakBox.Bounds = new Rectangle(field.X + S(10), field.Y + (field.Height - peakBox.PreferredHeight) / 2 + 1,
                field.Width - S(20), peakBox.PreferredHeight);
            peakBox.KeyPress += (s, e) => e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
            peakBox.TextChanged += delegate
            {
                if (messageKind == StatusKind.Error)
                    SetMessage("", StatusKind.Info);
            };
            peakBox.GotFocus += delegate { UpdateField(); };
            peakBox.LostFocus += delegate { UpdateField(); };

            int footer = S(FooterDip), buttonH = S(36), buttonY = footer + (ClientSize.Height - footer - buttonH) / 2;
            closeButton = new FlatButton(theme, scale)
            {
                Text = firstRun ? "Not now" : "Cancel",
                Font = bodyFont,
                BackColor = theme.Footer,
                Bounds = new Rectangle(w - S(Pad - 2) - S(104), buttonY, S(104), buttonH),
            };
            createButton = new FlatButton(theme, scale)
            {
                Text = "Create profiles",
                Primary = true,
                Font = bodyFont,
                BackColor = theme.Footer,
                Bounds = new Rectangle(closeButton.Left - S(4) - S(144), buttonY, S(144), buttonH),
            };
            createButton.Click += delegate { Create(); };
            closeButton.Click += delegate { Close(); };
            AcceptButton = createButton;
            CancelButton = closeButton;

            Controls.Add(picker);
            Controls.Add(peakBox);
            Controls.Add(createButton);
            Controls.Add(closeButton);
            ActiveControl = peakBox;
            peakBox.SelectAll();

            if (display == null)
            {
                createButton.Enabled = false;
                SetMessage("No display found", StatusKind.Error);
            }
            else if (!display.HdrSupported)
                SetMessage(display.Name + " doesn't report HDR support", StatusKind.Warning);
            else if (!primaries.FromEdid)
                SetMessage("Couldn't read the display's EDID · using sRGB primaries", StatusKind.Warning);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = theme.Dark ? 1 : 0;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        int S(float dip)
        {
            return (int)Math.Round(dip * scale);
        }

        Rectangle FieldRect
        {
            get { return new Rectangle(ClientSize.Width - S(Pad + 36 + 92), S(PeakRowDip + 4), S(92), S(32)); }
        }

        void UpdateField()
        {
            peakBox.BackColor = peakBox.Focused ? theme.InputFocused : theme.ControlFill;
            Invalidate(Rectangle.Inflate(FieldRect, S(2), S(2)));
        }

        void SetMessage(string text, StatusKind kind)
        {
            message = text;
            messageKind = kind;
            Invalidate(new Rectangle(0, S(MessageDip), ClientSize.Width, S(20)));
        }

        void Create()
        {
            int peak;
            if (!int.TryParse(peakBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out peak) || peak < 100 || peak > 10000)
            {
                SetMessage("Enter a peak brightness from 100 to 10000 nits", StatusKind.Error);
                peakBox.Focus();
                peakBox.SelectAll();
                return;
            }

            SetMessage("Creating profiles…", StatusKind.Busy);
            Update();
            Cursor = Cursors.WaitCursor;
            try
            {
                ProfileGenerator.CreateAll(ProfileLibrary.Directory, display.ModelId, display.Name, primaries, peak);
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, StatusKind.Error);
                return;
            }
            finally
            {
                ProfileLibrary.Invalidate();
                Cursor = Cursors.Default;
            }

            var handler = ProfilesCreated;
            if (handler != null)
                handler(picker.Value);
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width, x = S(Pad), textW = w - S(2 * Pad);
            g.Clear(theme.Background);

            TextRenderer.DrawText(g, "Set up gamma profiles", titleFont, new Rectangle(x, S(20), textW, S(30)), theme.Text, Line);
            TextRenderer.DrawText(g,
                "Creates a profile for every slider step (0, 5 … 100) in gamma 2.2 and 2.4, each correcting " +
                "SDR apps to a pure power curve at that step's brightness, plus a plain HDR profile for HDR content.",
                bodyFont, new Rectangle(x, S(58), textW, S(60)), theme.Secondary,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int rightW = S(140);
            DrawRow(g, S(GammaRowDip), "Gamma", "2.2 for a lit room, 2.4 for a dark one", textW - rightW);
            DrawRow(g, S(PeakRowDip), "Peak brightness", "Your display's max HDR nits · min is 0", textW - rightW);

            Rectangle field = FieldRect;
            float pen = Math.Max(1f, scale);
            Draw.FillRoundRect(g, peakBox.Focused ? theme.InputFocused : theme.ControlFill, theme.ControlBorder, field, 4 * scale, pen);
            using (var brush = new SolidBrush(peakBox.Focused ? theme.Accent : theme.Tertiary))
            {
                int lineH = peakBox.Focused ? S(2) : Math.Max(1, S(1));
                g.FillRectangle(brush, field.X + S(3), field.Bottom - lineH, field.Width - S(6), lineH);
            }
            TextRenderer.DrawText(g, "nits", labelFont, new Rectangle(field.Right + S(8), field.Y, S(36), field.Height), theme.Secondary, Line);

            string info = display == null ? "No display"
                : display.Name + " · " + (primaries.FromEdid ? "color primaries from EDID" : "sRGB primaries") +
                  " · " + ProfileGenerator.Gammas.Length * ((BrightnessPopup.Max - BrightnessPopup.Min) / BrightnessPopup.Step + 1) +
                  " gamma profiles + 1 HDR";
            TextRenderer.DrawText(g, info, smallFont, new Rectangle(x, S(InfoDip), textW, S(18)), theme.Tertiary,
                Line | TextFormatFlags.EndEllipsis);
            DrawMessage(g, new Rectangle(x, S(MessageDip), textW, S(20)));

            int footer = S(FooterDip);
            using (var brush = new SolidBrush(theme.Footer))
                g.FillRectangle(brush, 0, footer, w, ClientSize.Height - footer);
            using (var divider = new Pen(theme.Divider))
                g.DrawLine(divider, 0, footer, w, footer);
        }

        void DrawRow(Graphics g, int top, string label, string hint, int width)
        {
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(S(Pad), top + S(1), width, S(20)), theme.Text, Line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, hint, smallFont, new Rectangle(S(Pad), top + S(21), width, S(18)), theme.Secondary, Line | TextFormatFlags.EndEllipsis);
        }

        void DrawMessage(Graphics g, Rectangle r)
        {
            if (string.IsNullOrEmpty(message))
                return;
            string glyph = Theme.GlyphSync;
            Color color = theme.Secondary;
            if (messageKind == StatusKind.Error)
            {
                glyph = Theme.GlyphError;
                color = theme.Error;
            }
            else if (messageKind == StatusKind.Warning)
            {
                glyph = Theme.GlyphWarning;
                color = theme.Warning;
            }
            TextRenderer.DrawText(g, glyph, glyphFont, new Rectangle(r.X, r.Y, S(16), r.Height), color, Line);
            TextRenderer.DrawText(g, message, smallFont, new Rectangle(r.X + S(20), r.Y, r.Width - S(20), r.Height), color,
                Line | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var font in new[] { titleFont, bodyFont, labelFont, smallFont, glyphFont })
                    font.Dispose();
            base.Dispose(disposing);
        }
    }
}
