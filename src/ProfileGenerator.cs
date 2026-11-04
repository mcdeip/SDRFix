using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SdrBrightness
{
    // CIE xy chromaticities of a display's red, green and blue primaries and its white point.
    sealed class Primaries
    {
        public double Rx, Ry, Gx, Gy, Bx, By, Wx, Wy;
        public bool FromEdid;

        public static Primaries Srgb()
        {
            return new Primaries { Rx = 0.64, Ry = 0.33, Gx = 0.30, Gy = 0.60, Bx = 0.15, By = 0.06, Wx = 0.3127, Wy = 0.3290 };
        }

        // The chromaticity block of the monitor's EDID (bytes 25-34, 10 bits per coordinate), else sRGB.
        public static Primaries ForDisplay(string devicePath)
        {
            byte[] e = ReadEdid(devicePath);
            if (e == null)
                return Srgb();
            Func<int, int, double> c = (hi, lo) => ((e[hi] << 2) | lo) / 1024.0;
            var p = new Primaries
            {
                Rx = c(27, (e[25] >> 6) & 3), Ry = c(28, (e[25] >> 4) & 3),
                Gx = c(29, (e[25] >> 2) & 3), Gy = c(30, e[25] & 3),
                Bx = c(31, (e[26] >> 6) & 3), By = c(32, (e[26] >> 4) & 3),
                Wx = c(33, (e[26] >> 2) & 3), Wy = c(34, e[26] & 3),
                FromEdid = true,
            };
            return p.Plausible() ? p : Srgb();
        }

        bool Plausible()
        {
            double[] xy = { Rx, Ry, Gx, Gy, Bx, By, Wx, Wy };
            for (int i = 0; i < xy.Length; i += 2)
                if (xy[i] <= 0 || xy[i + 1] <= 0 || xy[i] + xy[i + 1] >= 1)
                    return false;
            return true;
        }

        // \\?\DISPLAY#DELA2F9#5&9d7a230&1&UID8453#{...} is stored under Enum\DISPLAY\DELA2F9\5&9d7a230&1&UID8453.
        static byte[] ReadEdid(string devicePath)
        {
            string[] parts = (devicePath ?? "").Split('#');
            if (parts.Length < 3)
                return null;
            try
            {
                string path = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters";
                using (var key = Registry.LocalMachine.OpenSubKey(path))
                {
                    var edid = key == null ? null : key.GetValue("EDID") as byte[];
                    if (edid == null || edid.Length < 128 || edid[0] != 0 || edid[1] != 0xFF || edid[7] != 0)
                        return null;
                    return edid;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // Writes MHC2 profiles the way ColorControl builds them for SDR content in HDR mode
    // ("Pure Power" transfer, native gamut), one per slider step for each gamma.
    static class ProfileGenerator
    {
        public const string Prefix = "SdrBrightness-";
        public static readonly string[] Gammas = { "2.2", "2.4" };
        const int LutSize = 1024;

        public static string SdrFileName(string model, string gamma, int value)
        {
            return Prefix + model + "-" + gamma + "-" + value.ToString("000", CultureInfo.InvariantCulture) + ".icm";
        }

        public static string HdrFileName(string model)
        {
            return Prefix + model + "-HDR.icm";
        }

        // Every slider step for both gammas, plus a gamma-neutral profile for HDR content.
        // Returns the number of files written.
        public static int CreateAll(string directory, string model, string displayName, Primaries primaries, int peakNits)
        {
            int count = 0;
            foreach (string gamma in Gammas)
            {
                double g = double.Parse(gamma, CultureInfo.InvariantCulture);
                for (int v = BrightnessPopup.Min; v <= BrightnessPopup.Max; v += BrightnessPopup.Step)
                {
                    int white = HdrDisplay.NitsFromSlider(v);
                    string description = v + " · Gamma " + gamma + " · " + white + " nits";
                    Write(Path.Combine(directory, SdrFileName(model, gamma, v)), Build(description, primaries, peakNits, SdrLut(white, g)));
                    count++;
                }
            }
            CreateHdr(directory, model, displayName, primaries, peakNits);
            return count + 1;
        }

        // Identity curve over 0 nits to the display's peak, e.g. "HDR 1300 nits (AW3926QW)" in Windows' list.
        public static void CreateHdr(string directory, string model, string displayName, Primaries primaries, int peakNits)
        {
            string description = "HDR " + peakNits + " nits" + (string.IsNullOrEmpty(displayName) ? "" : " (" + displayName + ")");
            Write(Path.Combine(directory, HdrFileName(model)), Build(description, primaries, peakNits, new double[] { 0, 1 }));
        }

        static void Write(string path, byte[] data)
        {
            try
            {
                File.WriteAllBytes(path, data);
            }
            catch (UnauthorizedAccessException)
            {
                throw new IOException("Windows won't let this app replace " + Path.GetFileName(path) + " (it was created by another account)");
            }
        }

        // ---- The SDR curve ----

        // Windows decodes SDR apps with the piecewise sRGB curve and scales them to SDR white.
        // For each PQ code this recovers the sRGB signal the app sent, decodes it with a pure
        // power gamma instead, and fades back to the untouched signal approaching SDR white,
        // leaving everything brighter than SDR white (HDR highlights) alone.
        public static double[] SdrLut(double whiteNits, double gamma)
        {
            var lut = new double[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double signal = i / (double)(LutSize - 1);
                double nits = PqToNits(signal);
                double corrected = NitsToPq(whiteNits * Math.Pow(SrgbEncode(nits / whiteNits), gamma));
                double fade = Math.Min(1, nits / whiteNits);
                lut[i] = corrected + fade * (signal - corrected);
            }
            return lut;
        }

        const double M1 = 0.1593017578125, M2 = 78.84375, C1 = 0.8359375, C2 = 18.8515625, C3 = 18.6875;

        static double PqToNits(double signal)
        {
            double p = Math.Pow(signal, 1 / M2);
            return 10000 * Math.Pow(Math.Max(p - C1, 0) / (C2 - C3 * p), 1 / M1);
        }

        static double NitsToPq(double nits)
        {
            double p = Math.Pow(Math.Max(nits, 0) / 10000, M1);
            return Math.Pow((C1 + C2 * p) / (1 + C3 * p), M2);
        }

        static double SrgbEncode(double linear)
        {
            if (linear >= 1)
                return 1;
            if (linear <= 0)
                return 0;
            return linear <= 0.00313066844250063 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        }

        // ---- ICC v4 display profile ----

        static readonly double[] D50 = { 0.9642, 1.0, 0.8249 };
        static readonly double[,] Bradford = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };

        public static byte[] Build(string description, Primaries p, int peakNits, double[] lut)
        {
            double[,] chad = Adaptation(p.Wx, p.Wy);
            double[,] colorants = Multiply(chad, RgbToXyz(p));
            byte[] trc = SrgbTrc();

            var tags = new List<KeyValuePair<string, byte[]>>
            {
                Tag("desc", Mluc(description)),
                Tag("cprt", Mluc("Created by SDRFix")),
                Tag("wtpt", Xyz(D50[0], D50[1], D50[2])),
                Tag("chad", Sf32(chad)),
                Tag("rXYZ", Xyz(colorants[0, 0], colorants[1, 0], colorants[2, 0])),
                Tag("gXYZ", Xyz(colorants[0, 1], colorants[1, 1], colorants[2, 1])),
                Tag("bXYZ", Xyz(colorants[0, 2], colorants[1, 2], colorants[2, 2])),
                Tag("rTRC", trc),
                Tag("gTRC", trc),
                Tag("bTRC", trc),
                Tag("chrm", Chromaticity(p)),
                Tag("lumi", Xyz(0, peakNits, 0)),
                Tag("MHC2", Mhc2(0, peakNits, lut)),
            };

            // Tag data follows the table, 4-byte aligned; tags sharing one array share its bytes.
            var data = new BeWriter();
            var offsets = new Dictionary<byte[], int>();
            int dataStart = 128 + 4 + 12 * tags.Count;
            foreach (var tag in tags)
            {
                if (offsets.ContainsKey(tag.Value))
                    continue;
                offsets[tag.Value] = dataStart + data.Length;
                data.Bytes(tag.Value);
                data.Align();
            }

            int size = dataStart + data.Length;
            var w = new BeWriter();
            DateTime now = DateTime.UtcNow;
            w.U32((uint)size);
            w.U32(0);                         // preferred CMM
            w.U32(0x04300000);                // version 4.3
            w.Sig("mntr");
            w.Sig("RGB ");
            w.Sig("XYZ ");
            foreach (int part in new[] { now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second })
                w.U16(part);
            w.Sig("acsp");
            w.Sig("MSFT");
            w.Zeros(24);                      // flags, manufacturer, model, attributes, rendering intent
            w.S15(D50[0]);
            w.S15(D50[1]);
            w.S15(D50[2]);
            w.Zeros(48);                      // creator, profile ID (filled below), reserved
            w.U32((uint)tags.Count);
            foreach (var tag in tags)
            {
                w.Sig(tag.Key);
                w.U32((uint)offsets[tag.Value]);
                w.U32((uint)tag.Value.Length);
            }
            w.Bytes(data.ToArray());

            byte[] icc = w.ToArray();
            SetProfileId(icc);
            return icc;
        }

        static KeyValuePair<string, byte[]> Tag(string signature, byte[] data)
        {
            return new KeyValuePair<string, byte[]>(signature, data);
        }

        // MD5 of the profile with the flags, rendering intent and ID fields zeroed (ICC v4 7.2.18).
        static void SetProfileId(byte[] icc)
        {
            var copy = (byte[])icc.Clone();
            Array.Clear(copy, 44, 4);
            Array.Clear(copy, 64, 4);
            Array.Clear(copy, 84, 16);
            using (var md5 = MD5.Create())
                Array.Copy(md5.ComputeHash(copy), 0, icc, 84, 16);
        }

        static byte[] Mluc(string text)
        {
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
            var w = new BeWriter();
            w.Sig("mluc");
            w.U32(0);
            w.U32(1);                         // one record
            w.U32(12);                        // record size
            w.Sig("enUS");
            w.U32((uint)utf16.Length);
            w.U32(28);
            w.Bytes(utf16);
            return w.ToArray();
        }

        static byte[] Xyz(double x, double y, double z)
        {
            var w = new BeWriter();
            w.Sig("XYZ ");
            w.U32(0);
            w.S15(x);
            w.S15(y);
            w.S15(z);
            return w.ToArray();
        }

        static byte[] Sf32(double[,] m)
        {
            var w = new BeWriter();
            w.Sig("sf32");
            w.U32(0);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    w.S15(m[i, j]);
            return w.ToArray();
        }

        // The sRGB curve as a type 3 parametric curve, as lcms writes it.
        static byte[] SrgbTrc()
        {
            var w = new BeWriter();
            w.Sig("para");
            w.U32(0);
            w.U16(3);
            w.U16(0);
            w.S15(2.4);
            w.S15(1 / 1.055);
            w.S15(0.055 / 1.055);
            w.S15(1 / 12.92);
            w.S15(0.04045);
            return w.ToArray();
        }

        static byte[] Chromaticity(Primaries p)
        {
            var w = new BeWriter();
            w.Sig("chrm");
            w.U32(0);
            w.U16(3);
            w.U16(0);
            foreach (double v in new[] { p.Rx, p.Ry, p.Gx, p.Gy, p.Bx, p.By })
                w.U32((uint)Math.Round(v * 65536));
            return w.ToArray();
        }

        // Windows' HDR calibration data: luminance range, a 3x4 matrix (identity: leave Windows'
        // own gamut mapping alone) and one PQ-in, PQ-out curve per channel.
        static byte[] Mhc2(double minNits, double maxNits, double[] lut)
        {
            int lutBytes = 8 + 4 * lut.Length;
            var w = new BeWriter();
            w.Sig("MHC2");
            w.U32(0);
            w.U32((uint)lut.Length);
            w.S15(minNits);
            w.S15(maxNits);
            w.U32(36);
            for (int c = 0; c < 3; c++)
                w.U32((uint)(84 + c * lutBytes));
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                    w.S15(r == c ? 1 : 0);
            for (int c = 0; c < 3; c++)
            {
                w.Sig("sf32");
                w.U32(0);
                foreach (double v in lut)
                    w.S15(v);
            }
            return w.ToArray();
        }

        // ---- Colorimetry ----

        static double[] XyzOf(double x, double y)
        {
            return new[] { x / y, 1.0, (1 - x - y) / y };
        }

        // Linear RGB to XYZ for the primaries, scaled so RGB white lands on the display's white (Y = 1).
        static double[,] RgbToXyz(Primaries p)
        {
            double[][] columns = { XyzOf(p.Rx, p.Ry), XyzOf(p.Gx, p.Gy), XyzOf(p.Bx, p.By) };
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] = columns[j][i];
            double[] s = Apply(Invert(m), XyzOf(p.Wx, p.Wy));
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] *= s[j];
            return m;
        }

        // Bradford adaptation from the display's white point to the D50 connection space.
        static double[,] Adaptation(double wx, double wy)
        {
            double[] source = Apply(Bradford, XyzOf(wx, wy)), target = Apply(Bradford, D50);
            var scale = new double[3, 3];
            for (int i = 0; i < 3; i++)
                scale[i, i] = target[i] / source[i];
            return Multiply(Invert(Bradford), Multiply(scale, Bradford));
        }

        static double[,] Multiply(double[,] a, double[,] b)
        {
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        m[i, j] += a[i, k] * b[k, j];
            return m;
        }

        static double[] Apply(double[,] m, double[] v)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    r[i] += m[i, k] * v[k];
            return r;
        }

        static double[,] Invert(double[,] m)
        {
            double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                       - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                       + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    // Cofactor of (j, i), transposed.
                    int r0 = (j + 1) % 3, r1 = (j + 2) % 3, c0 = (i + 1) % 3, c1 = (i + 2) % 3;
                    r[i, j] = (m[r0, c0] * m[r1, c1] - m[r0, c1] * m[r1, c0]) / det;
                }
            return r;
        }

        sealed class BeWriter
        {
            readonly MemoryStream stream = new MemoryStream();

            public int Length { get { return (int)stream.Length; } }

            public void U32(uint v)
            {
                stream.WriteByte((byte)(v >> 24));
                stream.WriteByte((byte)(v >> 16));
                stream.WriteByte((byte)(v >> 8));
                stream.WriteByte((byte)v);
            }

            public void U16(int v)
            {
                stream.WriteByte((byte)(v >> 8));
                stream.WriteByte((byte)v);
            }

            // s15Fixed16Number
            public void S15(double v)
            {
                U32(unchecked((uint)(int)Math.Round(v * 65536)));
            }

            public void Sig(string four)
            {
                Bytes(Encoding.ASCII.GetBytes(four));
            }

            public void Zeros(int count)
            {
                Bytes(new byte[count]);
            }

            public void Bytes(byte[] b)
            {
                stream.Write(b, 0, b.Length);
            }

            public void Align()
            {
                while (stream.Length % 4 != 0)
                    stream.WriteByte(0);
            }

            public byte[] ToArray()
            {
                return stream.ToArray();
            }
        }
    }
}
