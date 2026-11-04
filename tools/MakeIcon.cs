using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SdrBrightness
{
    // Build step: renders the exe icon (white sun on an amber tile) into a multi-size .ico.
    static class MakeIcon
    {
        static void Main(string[] args)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (Bitmap bitmap = Render(size))
                    images.Add(size >= 256 ? Png(bitmap) : Dib(bitmap));

            using (var w = new BinaryWriter(File.Create(args[0])))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images)
                    w.Write(image);
            }
        }

        static Bitmap Render(int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var rect = new RectangleF(0, 0, size, size);
                float d = size * 0.44f;
                using (var tile = new GraphicsPath())
                using (var brush = new LinearGradientBrush(rect, Color.FromArgb(0xFF, 0xC8, 0x3D), Color.FromArgb(0xF7, 0x63, 0x0C), 90f))
                {
                    tile.AddArc(0, 0, d, d, 180, 90);
                    tile.AddArc(size - d, 0, d, d, 270, 90);
                    tile.AddArc(size - d, size - d, d, d, 0, 90);
                    tile.AddArc(0, size - d, d, d, 90, 90);
                    tile.CloseFigure();
                    g.FillPath(brush, tile);
                }
                using (Bitmap sun = Theme.RenderGlyph(Theme.GlyphBrightness, size, Color.White, 0.72f))
                    g.DrawImage(sun, 0, 0, size, size);
            }
            return bitmap;
        }

        static byte[] Png(Bitmap bitmap)
        {
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // Classic 32-bit icon frame: BITMAPINFOHEADER, bottom-up BGRA rows, empty AND mask.
        static byte[] Dib(Bitmap bitmap)
        {
            int size = bitmap.Width, maskStride = ((size + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(size);
                w.Write(size * 2);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(0);
                w.Write(size * size * 4 + maskStride * size);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                    {
                        Color c = bitmap.GetPixel(x, y);
                        w.Write(c.B);
                        w.Write(c.G);
                        w.Write(c.R);
                        w.Write(c.A);
                    }
                w.Write(new byte[maskStride * size]);
                return ms.ToArray();
            }
        }
    }
}
