using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

// Draws docs/images/gamma-chart.png for the README: how bright dark SDR values end up in HDR mode
// with Windows' piecewise sRGB decode versus a pure 2.2 or 2.4 power curve, at 200-nit SDR white.
static class GammaChart
{
    const float K = 2;                 // drawn at 2x for high-DPI screens
    const float W = 820, H = 470;      // base size
    const float Left = 70, Right = 172, Top = 112, Bottom = 58;
    const double White = 200, MaxCode = 48, MaxNits = 6;

    static readonly Color Surface = Hex("#1a1a19"), Primary = Hex("#f0efec"), Secondary = Hex("#c3c2b7"),
        Muted = Hex("#898781"), Grid = Hex("#2c2c2a"), Axis = Hex("#383835");

    struct Series
    {
        public string Name;
        public Color Color;
        public Func<double, double> Decode;
    }

    static readonly Series[] All =
    {
        new Series { Name = "Windows (sRGB)", Color = Hex("#3987e5"), Decode = Srgb },
        new Series { Name = "Gamma 2.2", Color = Hex("#d95926"), Decode = v => Math.Pow(v, 2.2) },
        new Series { Name = "Gamma 2.4", Color = Hex("#199e70"), Decode = v => Math.Pow(v, 2.4) },
    };

    static double Srgb(double v)
    {
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    static double Nits(Series s, double code)
    {
        return White * s.Decode(code / 255);
    }

    static float X(double code) { return (float)(Left + (W - Left - Right) * code / MaxCode) * K; }
    static float Y(double nits) { return (float)(Top + (H - Top - Bottom) * (1 - nits / MaxNits)) * K; }

    static Color Hex(string hex)
    {
        return ColorTranslator.FromHtml(hex);
    }

    static Font F(float px, bool semibold = false)
    {
        return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", px * K, GraphicsUnit.Pixel);
    }

    static void Text(Graphics g, string text, Font font, Color color, float x, float y, StringAlignment align = StringAlignment.Near,
        StringAlignment line = StringAlignment.Near)
    {
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat { Alignment = align, LineAlignment = line })
            g.DrawString(text, font, brush, x, y, format);
    }

    static void Main(string[] args)
    {
        var inv = CultureInfo.InvariantCulture;
        using (var bmp = new Bitmap((int)(W * K), (int)(H * K)))
        using (var g = Graphics.FromImage(bmp))
        using (var title = F(19, true))
        using (var body = F(13))
        using (var small = F(12))
        using (var label = F(13, true))
        {
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Text(g, "Shadows in HDR mode at 200-nit SDR white", title, Primary, 24 * K, 20 * K);
            Text(g, "How bright the darkest 8-bit SDR values are shown. All three curves meet at 200 nits at code 255.",
                body, Secondary, 24 * K, 50 * K);

            // Legend: a short line in the series color, the name in ink.
            float lx = 24 * K, ly = 82 * K;
            foreach (Series s in All)
            {
                using (var pen = new Pen(s.Color, 3 * K) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, lx, ly, lx + 18 * K, ly);
                Text(g, s.Name, small, Secondary, lx + 26 * K, ly, StringAlignment.Near, StringAlignment.Center);
                lx += (26 + g.MeasureString(s.Name, small).Width / K + 22) * K;
            }

            // Grid, axis and tick labels.
            using (var grid = new Pen(Grid, 1 * K))
            using (var axis = new Pen(Axis, 1 * K))
            {
                for (int n = 0; n <= MaxNits; n++)
                {
                    g.DrawLine(n == 0 ? axis : grid, X(0), Y(n), X(MaxCode), Y(n));
                    Text(g, n.ToString(inv), small, Muted, X(0) - 10 * K, Y(n), StringAlignment.Far, StringAlignment.Center);
                }
                for (int c = 0; c <= MaxCode; c += 8)
                {
                    g.DrawLine(axis, X(c), Y(0), X(c), Y(0) + 5 * K);
                    Text(g, c.ToString(inv), small, Muted, X(c), Y(0) + 9 * K, StringAlignment.Center);
                }
            }
            Text(g, "nits", small, Muted, X(0) - 10 * K, Y(MaxNits) - 22 * K, StringAlignment.Far);
            Text(g, "8-bit SDR code value", small, Muted, (X(0) + X(MaxCode)) / 2, Y(0) + 30 * K, StringAlignment.Center);

            // The curves, sampled finely enough to stay smooth.
            foreach (Series s in All)
            {
                var points = new PointF[(int)(MaxCode * 4) + 1];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new PointF(X(i / 4.0), Y(Nits(s, i / 4.0)));
                using (var pen = new Pen(s.Color, 2.5f * K) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, points);
            }

            // Direct labels at the line ends: name in ink, value in secondary ink.
            foreach (Series s in All)
            {
                double end = Nits(s, MaxCode);
                float y = Y(end);
                using (var brush = new SolidBrush(s.Color))
                    g.FillEllipse(brush, X(MaxCode) - 4 * K, y - 4 * K, 8 * K, 8 * K);
                Text(g, s.Name, label, Primary, X(MaxCode) + 12 * K, y - 9 * K, StringAlignment.Near, StringAlignment.Center);
                Text(g, end.ToString("0.0", inv) + " nits", small, Secondary, X(MaxCode) + 12 * K, y + 9 * K,
                    StringAlignment.Near, StringAlignment.Center);
            }

            // One callout where the gap matters most to the eye.
            const int callout = 13;
            double win = Nits(All[0], callout), g22 = Nits(All[1], callout);
            using (var ring = new Pen(Surface, 2 * K))
                foreach (Series s in new[] { All[0], All[1] })
                {
                    float cy = Y(Nits(s, callout));
                    using (var brush = new SolidBrush(s.Color))
                        g.FillEllipse(brush, X(callout) - 5 * K, cy - 5 * K, 10 * K, 10 * K);
                    g.DrawEllipse(ring, X(callout) - 5 * K, cy - 5 * K, 10 * K, 10 * K);
                }
            using (var leader = new Pen(Muted, 1 * K) { DashStyle = DashStyle.Dot })
                g.DrawLine(leader, X(callout), Y(win) - 8 * K, X(callout), Y(3.55));
            Text(g, "Code " + callout + ": " + win.ToString("0.00", inv) + " nits on Windows vs " + g22.ToString("0.00", inv) +
                " at gamma 2.2", body, Primary, X(callout) - 4 * K, Y(3.55) - 40 * K);
            Text(g, (win / g22).ToString("0.0", inv) + "× brighter, so near-black turns gray", body, Secondary,
                X(callout) - 4 * K, Y(3.55) - 20 * K);

            bmp.Save(args[0], ImageFormat.Png);
        }

        foreach (int code in new[] { 5, 13, 26, 51, 128, 255 })
        {
            Console.Write(code.ToString(inv).PadLeft(3) + " (" + (code / 2.55).ToString("0", inv) + "%)");
            foreach (Series s in All)
                Console.Write("  " + s.Name + " " + Nits(s, code).ToString("0.000", inv));
            Console.WriteLine("  ratio " + (Nits(All[0], code) / Nits(All[1], code)).ToString("0.00", inv));
        }
    }
}
