using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace SdrBrightness
{
    // Windows 11 flyout colors for the current light/dark setting and accent.
    sealed class Theme
    {
        public const string GlyphBrightness = "";
        public const string GlyphCheck = "";
        public const string GlyphWarning = "";
        public const string GlyphError = "";
        public const string GlyphSync = "";
        public const string GlyphCamera = "";
        public const string GlyphDisplay = "";

        const string PersonalizeKey =@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public bool Dark;
        public Color Background, Footer, Divider;
        public Color Text, Secondary, Tertiary;
        public Color Rail, ThumbFill, ThumbBorder, Accent, OnAccent;
        public Color Warning, Error, Disabled;
        public Color ControlFill, ControlHover, ControlPressed, ControlBorder, InputFocused;

        public static Theme Current()
        {
            bool dark = ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
            var t = new Theme { Dark = dark };
            if (dark)
            {
                t.Background = Color.FromArgb(0x2B, 0x2B, 0x2B);
                t.Footer = Color.FromArgb(0x20, 0x20, 0x20);
                t.Divider = Color.FromArgb(0x1A, 0x1A, 0x1A);
                t.Text = Color.White;
                t.Secondary = Color.FromArgb(0xCF, 0xCF, 0xCF);
                t.Tertiary = Color.FromArgb(0x9A, 0x9A, 0x9A);
                t.Rail = Color.FromArgb(0x8A, 0x8A, 0x8A);
                t.ThumbFill = Color.FromArgb(0x45, 0x45, 0x45);
                t.ThumbBorder = Color.FromArgb(0x52, 0x52, 0x52);
                t.OnAccent = Color.Black;
                t.Warning = Color.FromArgb(0xFC, 0xE1, 0x00);
                t.Error = Color.FromArgb(0xFF, 0x99, 0xA4);
                t.Disabled = Color.FromArgb(0x5C, 0x5C, 0x5C);
                t.ControlFill = Color.FromArgb(0x37, 0x37, 0x37);
                t.ControlHover = Color.FromArgb(0x3D, 0x3D, 0x3D);
                t.ControlPressed = Color.FromArgb(0x32, 0x32, 0x32);
                t.ControlBorder = Color.FromArgb(0x48, 0x48, 0x48);
                t.InputFocused = Color.FromArgb(0x1F, 0x1F, 0x1F);
            }
            else
            {
                t.Background = Color.FromArgb(0xF9, 0xF9, 0xF9);
                t.Footer = Color.FromArgb(0xEE, 0xEE, 0xEE);
                t.Divider = Color.FromArgb(0xE0, 0xE0, 0xE0);
                t.Text = Color.FromArgb(0x1B, 0x1B, 0x1B);
                t.Secondary = Color.FromArgb(0x5F, 0x5F, 0x5F);
                t.Tertiary = Color.FromArgb(0x8A, 0x8A, 0x8A);
                t.Rail = Color.FromArgb(0x86, 0x86, 0x86);
                t.ThumbFill = Color.White;
                t.ThumbBorder = Color.FromArgb(0xD6, 0xD6, 0xD6);
                t.OnAccent = Color.White;
                t.Warning = Color.FromArgb(0x9D, 0x5D, 0x00);
                t.Error = Color.FromArgb(0xC4, 0x2B, 0x1C);
                t.Disabled = Color.FromArgb(0xC6, 0xC6, 0xC6);
                t.ControlFill = Color.FromArgb(0xFD, 0xFD, 0xFD);
                t.ControlHover = Color.FromArgb(0xF4, 0xF4, 0xF4);
                t.ControlPressed = Color.FromArgb(0xEC, 0xEC, 0xEC);
                t.ControlBorder = Color.FromArgb(0xD6, 0xD6, 0xD6);
                t.InputFocused = Color.White;
            }
            t.Accent = ReadAccent(dark);
            return t;
        }

        public static bool TaskbarIsLight()
        {
            return ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) != 0;
        }

        // AccentPalette holds eight RGBA swatches, lightest first; Windows 11 controls use
        // "Light 2" on dark surfaces and "Dark 1" on light ones.
        static Color ReadAccent(bool dark)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var palette = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (palette != null && palette.Length >= 32)
                    {
                        int i = (dark ? 1 : 4) * 4;
                        return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
                    }
                }
            }
            catch (Exception)
            {
            }
            return dark ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x00, 0x5F, 0xB8);
        }

        static int ReadDword(string path, string name, int fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(path))
                {
                    object value = key == null ? null : key.GetValue(name);
                    return value is int ? (int)value : fallback;
                }
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        static string iconFont;

        public static string IconFont
        {
            get
            {
                if (iconFont == null)
                    iconFont = IsInstalled("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                return iconFont;
            }
        }

        static bool IsInstalled(string family)
        {
            using (var font = new Font(family, 10f))
                return string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase);
        }

        // Draws an icon-font glyph centered and scaled to `fill` of a size x size bitmap.
        public static Bitmap RenderGlyph(string glyph, int size, Color color, float fill)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var family = new FontFamily(IconFont))
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                path.AddString(glyph, family, 0, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF bounds = path.GetBounds();
                float scale = size * fill / Math.Max(bounds.Width, bounds.Height);
                using (var m = new Matrix())
                {
                    m.Translate(size / 2f, size / 2f);
                    m.Scale(scale, scale);
                    m.Translate(-(bounds.X + bounds.Width / 2f), -(bounds.Y + bounds.Height / 2f));
                    path.Transform(m);
                }
                g.FillPath(brush, path);
            }
            return bitmap;
        }
    }
}
