using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SdrBrightness
{
    // One display's desktop image as Windows composed it. In HDR mode that's linear FP16 scRGB, where
    // 1.0 is 80 nits and SDR content is scaled so its white sits at White (SDR white level / 1000).
    sealed class DesktopImage
    {
        public Rectangle Bounds;  // desktop coordinates, physical pixels
        public string DeviceName; // \\.\DISPLAY1
        public float White = 1;
        public short[] Half;      // RGBA halves in HDR mode
        public byte[] Bgra;       // 8-bit BGRA otherwise
    }

    // Screenshots that look like SDR ones in HDR mode. Ordinary capture squeezes the HDR desktop into
    // 8 bits by clipping at 80 nits, so with SDR brightness above 0 everything bright blows out. Here the
    // FP16 desktop is read through DXGI desktop duplication instead, divided by SDR white and encoded
    // with the sRGB curve Windows decoded it with, which gives back SDR content's original pixel values.
    static class ScreenCapture
    {
        static readonly float[] HalfTable = BuildHalfTable();
        static readonly float[] Thresholds = BuildThresholds();

        // Every display overlapping `area` (all of them when null).
        public static List<DesktopImage> Capture(Rectangle? area)
        {
            var whites = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (HdrDisplay d in HdrDisplay.GetAll())
            {
                int? level = d.HdrOn && d.GdiName != null ? d.GetSdrWhiteLevel() : null;
                if (level.HasValue && level.Value >= 1000)
                    whites[d.GdiName] = level.Value / 1000f;
            }

            var images = new List<DesktopImage>();
            Guid iid = Dxgi.IID_IDXGIFactory1;
            IntPtr factory;
            Check(Dxgi.CreateDXGIFactory1(ref iid, out factory), "DXGI");
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (Dxgi.Method<Dxgi.GetByIndex>(factory, 12)(factory, a, out adapter) != 0) // EnumAdapters1
                        break;
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            if (Dxgi.Method<Dxgi.GetByIndex>(adapter, 7)(adapter, o, out output) != 0) // EnumOutputs
                                break;
                            try
                            {
                                Dxgi.OUTPUT_DESC desc;
                                Dxgi.Method<Dxgi.GetOutputDesc>(output, 7)(output, out desc);
                                var bounds = Rectangle.FromLTRB(desc.Left, desc.Top, desc.Right, desc.Bottom);
                                if (desc.AttachedToDesktop == 0 || (area.HasValue && !bounds.IntersectsWith(area.Value)))
                                    continue;
                                if (desc.Rotation > 1)
                                    throw new NotSupportedException("Rotated displays aren't supported yet");
                                DesktopImage image = Duplicate(adapter, output, bounds);
                                image.DeviceName = desc.DeviceName;
                                float white;
                                if (image.Half != null && whites.TryGetValue(desc.DeviceName, out white))
                                    image.White = white;
                                images.Add(image);
                            }
                            finally
                            {
                                Marshal.Release(output);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
            if (images.Count == 0)
                throw new InvalidOperationException("No display to capture");
            return images;
        }

        public static Rectangle Union(IList<DesktopImage> images)
        {
            Rectangle all = images[0].Bounds;
            foreach (DesktopImage image in images)
                all = Rectangle.Union(all, image.Bounds);
            return all;
        }

        static DesktopImage Duplicate(IntPtr adapter, IntPtr output, Rectangle bounds)
        {
            IntPtr device = IntPtr.Zero, context = IntPtr.Zero, newer = IntPtr.Zero, dupl = IntPtr.Zero;
            IntPtr resource = IntPtr.Zero, texture = IntPtr.Zero, staging = IntPtr.Zero;
            bool holding = false;
            try
            {
                int level;
                Check(Dxgi.D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out device, out level, out context), "Direct3D");

                // DuplicateOutput1 can hand over the FP16 desktop; plain DuplicateOutput only ever gives 8 bits.
                Guid iid = Dxgi.IID_IDXGIOutput5;
                if (Marshal.QueryInterface(output, ref iid, out newer) == 0)
                {
                    int[] formats = { Dxgi.FORMAT_R16G16B16A16_FLOAT, Dxgi.FORMAT_B8G8R8A8_UNORM };
                    Check(Dxgi.Method<Dxgi.DuplicateOutput1>(newer, 26)(newer, device, 0, (uint)formats.Length, formats, out dupl), "Desktop duplication");
                }
                else
                {
                    iid = Dxgi.IID_IDXGIOutput1;
                    Check(Marshal.QueryInterface(output, ref iid, out newer), "Desktop duplication");
                    Check(Dxgi.Method<Dxgi.DuplicateOutput>(newer, 22)(newer, device, out dupl), "Desktop duplication");
                }

                // Only a frame Windows composed after the duplication started holds the desktop image; one
                // without a present time is empty. If the screen is completely still, nudge a frame out of it.
                FrameNudge nudge = null;
                try
                {
                    for (int attempt = 0; attempt < 5 && resource == IntPtr.Zero; attempt++)
                    {
                        Dxgi.FRAME_INFO info;
                        int hr = Dxgi.Method<Dxgi.AcquireNextFrame>(dupl, 8)(dupl, attempt == 0 ? 150u : 500u, out info, out resource);
                        if (hr != Dxgi.WAIT_TIMEOUT)
                        {
                            Check(hr, "Capture");
                            holding = true;
                            if (info.LastPresentTime != 0)
                                break;
                            Marshal.Release(resource);
                            resource = IntPtr.Zero;
                            Dxgi.Method<Dxgi.NoArgs>(dupl, 14)(dupl); // ReleaseFrame
                            holding = false;
                        }
                        if (nudge == null && attempt > 0)
                            nudge = FrameNudge.ShowAt(bounds.Location);
                    }
                }
                finally
                {
                    if (nudge != null)
                        nudge.Dispose();
                }
                if (resource == IntPtr.Zero)
                    throw new InvalidOperationException("Windows didn't hand over the desktop image");

                iid = Dxgi.IID_ID3D11Texture2D;
                Check(Marshal.QueryInterface(resource, ref iid, out texture), "Capture");
                Dxgi.TEXTURE2D_DESC desc;
                Dxgi.Method<Dxgi.GetTextureDesc>(texture, 10)(texture, out desc);
                Dxgi.TEXTURE2D_DESC copy = desc;
                copy.MipLevels = 1;
                copy.ArraySize = 1;
                copy.SampleCount = 1;
                copy.SampleQuality = 0;
                copy.Usage = 3;              // D3D11_USAGE_STAGING
                copy.BindFlags = 0;
                copy.CPUAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
                copy.MiscFlags = 0;
                Check(Dxgi.Method<Dxgi.CreateTexture2D>(device, 5)(device, ref copy, IntPtr.Zero, out staging), "Capture");
                Dxgi.Method<Dxgi.CopyResource>(context, 47)(context, staging, texture);

                Dxgi.MAPPED map;
                Check(Dxgi.Method<Dxgi.Map>(context, 14)(context, staging, 0, 1, 0, out map), "Capture"); // D3D11_MAP_READ
                try
                {
                    int w = (int)desc.Width, h = (int)desc.Height;
                    var image = new DesktopImage { Bounds = new Rectangle(bounds.Location, new Size(w, h)) };
                    if (desc.Format == Dxgi.FORMAT_R16G16B16A16_FLOAT)
                    {
                        image.Half = new short[w * h * 4];
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(IntPtr.Add(map.Data, y * (int)map.RowPitch), image.Half, y * w * 4, w * 4);
                    }
                    else if (desc.Format == Dxgi.FORMAT_B8G8R8A8_UNORM || desc.Format == Dxgi.FORMAT_B8G8R8A8_UNORM_SRGB)
                    {
                        image.Bgra = new byte[w * h * 4];
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(IntPtr.Add(map.Data, y * (int)map.RowPitch), image.Bgra, y * w * 4, w * 4);
                    }
                    else
                        throw new NotSupportedException("Unexpected desktop format " + desc.Format);
                    return image;
                }
                finally
                {
                    Dxgi.Method<Dxgi.Unmap>(context, 15)(context, staging, 0);
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { staging, texture, resource })
                    if (p != IntPtr.Zero)
                        Marshal.Release(p);
                if (holding)
                    Dxgi.Method<Dxgi.NoArgs>(dupl, 14)(dupl);
                foreach (IntPtr p in new[] { dupl, newer, context, device })
                    if (p != IntPtr.Zero)
                        Marshal.Release(p);
            }
        }

        static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new InvalidOperationException(what + " failed (0x" + hr.ToString("X8") + ")");
        }

        // ---- Encoding ----

        // `crop` of the desktop as an 8-bit sRGB bitmap. SDR content comes back exactly as apps drew it.
        // When something in the crop is brighter than SDR white (HDR content), highlights roll off
        // smoothly into white instead of clipping, and `toneMapped` says so.
        public static Bitmap Render(IList<DesktopImage> images, Rectangle crop, out bool toneMapped)
        {
            float peak = 0;
            foreach (DesktopImage image in images)
                if (image.Half != null)
                    peak = Math.Max(peak, Peak(image, Rectangle.Intersect(crop, image.Bounds)));
            toneMapped = peak > 1.01f; // anything less is FP16 rounding on SDR white

            var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            bitmap.SetResolution(96, 96);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, crop.Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * crop.Height]; // black where no display covers the crop
                foreach (DesktopImage image in images)
                {
                    Rectangle part = Rectangle.Intersect(crop, image.Bounds);
                    if (part.IsEmpty)
                        continue;
                    if (image.Bgra != null)
                        CopyBgra(image, part, crop, bytes, data.Stride);
                    else if (toneMapped)
                        EncodeToneMapped(image, part, crop, bytes, data.Stride, new ToneCurve(image.White * 80, peak * image.White * 80));
                    else
                        EncodeExact(image, part, crop, bytes, data.Stride);
                }
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        // Brightest channel in `part`, relative to SDR white.
        static float Peak(DesktopImage image, Rectangle part)
        {
            float max = 0;
            int stride = image.Bounds.Width * 4;
            for (int y = part.Top; y < part.Bottom; y++)
            {
                int i = (y - image.Bounds.Top) * stride + (part.Left - image.Bounds.Left) * 4;
                for (int x = part.Left; x < part.Right; x++, i += 4)
                {
                    float r = HalfTable[(ushort)image.Half[i]], g = HalfTable[(ushort)image.Half[i + 1]], b = HalfTable[(ushort)image.Half[i + 2]];
                    float m = r > g ? r : g;
                    if (b > m)
                        m = b;
                    if (m > max)
                        max = m;
                }
            }
            return max / image.White;
        }

        static void CopyBgra(DesktopImage image, Rectangle part, Rectangle crop, byte[] bytes, int stride)
        {
            int src = image.Bounds.Width * 4;
            for (int y = part.Top; y < part.Bottom; y++)
            {
                int i = (y - image.Bounds.Top) * src + (part.Left - image.Bounds.Left) * 4;
                int o = (y - crop.Top) * stride + (part.Left - crop.Left) * 3;
                for (int x = part.Left; x < part.Right; x++, i += 4, o += 3)
                {
                    bytes[o] = image.Bgra[i];
                    bytes[o + 1] = image.Bgra[i + 1];
                    bytes[o + 2] = image.Bgra[i + 2];
                }
            }
        }

        static void EncodeExact(DesktopImage image, Rectangle part, Rectangle crop, byte[] bytes, int stride)
        {
            // Every possible half value straight to its 8-bit code at this display's SDR white.
            var code = new byte[65536];
            for (int h = 0; h < code.Length; h++)
                code[h] = Encode(HalfTable[h] / image.White);

            int src = image.Bounds.Width * 4;
            for (int y = part.Top; y < part.Bottom; y++)
            {
                int i = (y - image.Bounds.Top) * src + (part.Left - image.Bounds.Left) * 4;
                int o = (y - crop.Top) * stride + (part.Left - crop.Left) * 3;
                for (int x = part.Left; x < part.Right; x++, i += 4, o += 3)
                {
                    bytes[o] = code[(ushort)image.Half[i + 2]];
                    bytes[o + 1] = code[(ushort)image.Half[i + 1]];
                    bytes[o + 2] = code[(ushort)image.Half[i]];
                }
            }
        }

        static void EncodeToneMapped(DesktopImage image, Rectangle part, Rectangle crop, byte[] bytes, int stride, ToneCurve curve)
        {
            float inv = 1 / image.White;
            int src = image.Bounds.Width * 4;
            for (int y = part.Top; y < part.Bottom; y++)
            {
                int i = (y - image.Bounds.Top) * src + (part.Left - image.Bounds.Left) * 4;
                int o = (y - crop.Top) * stride + (part.Left - crop.Left) * 3;
                for (int x = part.Left; x < part.Right; x++, i += 4, o += 3)
                {
                    float r = Math.Max(0, HalfTable[(ushort)image.Half[i]] * inv);
                    float g = Math.Max(0, HalfTable[(ushort)image.Half[i + 1]] * inv);
                    float b = Math.Max(0, HalfTable[(ushort)image.Half[i + 2]] * inv);
                    float m = r > g ? r : g;
                    if (b > m)
                        m = b;
                    if (m > curve.Knee)
                    {
                        // Scale all three channels together so bright colors keep their hue.
                        float s = curve.Map(m) / m;
                        r *= s;
                        g *= s;
                        b *= s;
                    }
                    bytes[o] = Encode(b);
                    bytes[o + 1] = Encode(g);
                    bytes[o + 2] = Encode(r);
                }
            }
        }

        // Linear light relative to SDR white -> nearest 8-bit sRGB code.
        static byte Encode(float v)
        {
            int lo = 0, hi = 255;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (v > Thresholds[mid])
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return (byte)lo;
        }

        // Thresholds[k] is the linear value halfway between codes k and k + 1.
        static float[] BuildThresholds()
        {
            var t = new float[255];
            for (int k = 0; k < t.Length; k++)
            {
                double v = (k + 0.5) / 255;
                t[k] = (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        static float[] BuildHalfTable()
        {
            var table = new float[65536];
            for (int h = 0; h < table.Length; h++)
            {
                int exponent = (h >> 10) & 0x1F, mantissa = h & 0x3FF;
                double v;
                if (exponent == 0)
                    v = mantissa * Math.Pow(2, -24);
                else if (exponent == 31)
                    v = mantissa == 0 ? 65504 : 0; // infinity as the largest value, NaN as black
                else
                    v = (1 + mantissa / 1024.0) * Math.Pow(2, exponent - 15);
                table[h] = (float)((h & 0x8000) != 0 ? -v : v);
            }
            return table;
        }

        // BT.2390's EETF: brightness up to the knee is kept, the rest rolls off so the crop's peak
        // lands exactly on SDR white. Values are relative to SDR white.
        sealed class ToneCurve
        {
            const int Steps = 4096;
            public readonly float Knee;
            readonly float peak;
            readonly float[] table = new float[Steps + 1];

            public ToneCurve(double whiteNits, double peakNits)
            {
                peakNits = Math.Min(peakNits, 10000);
                double pqPeak = Pq(peakNits), maxLum = Pq(whiteNits) / pqPeak, ks = Math.Max(0, 1.5 * maxLum - 0.5);
                Knee = (float)(PqToNits(ks * pqPeak) / whiteNits);
                peak = (float)(peakNits / whiteNits);
                for (int i = 0; i <= Steps; i++)
                {
                    double rel = Knee + (peak - Knee) * i / Steps;
                    double e = Pq(rel * whiteNits) / pqPeak, t = (e - ks) / (1 - ks), t2 = t * t, t3 = t2 * t;
                    double mapped = (2 * t3 - 3 * t2 + 1) * ks + (t3 - 2 * t2 + t) * (1 - ks) + (-2 * t3 + 3 * t2) * maxLum;
                    table[i] = (float)Math.Min(1, PqToNits(mapped * pqPeak) / whiteNits);
                }
            }

            public float Map(float rel)
            {
                if (rel >= peak)
                    return table[Steps];
                float pos = (rel - Knee) / (peak - Knee) * Steps;
                int i = (int)pos;
                return table[i] + (table[i + 1] - table[i]) * (pos - i);
            }

            const double M1 = 0.1593017578125, M2 = 78.84375, C1 = 0.8359375, C2 = 18.8515625, C3 = 18.6875;

            static double Pq(double nits)
            {
                double y = Math.Pow(Math.Max(0, nits) / 10000, M1);
                return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
            }

            static double PqToNits(double e)
            {
                double p = Math.Pow(Math.Max(0, e), 1 / M2);
                return 10000 * Math.Pow(Math.Max(p - C1, 0) / (C2 - C3 * p), 1 / M1);
            }
        }
    }

    // A tiny, nearly transparent window in a corner. Showing it makes Windows compose a new desktop
    // frame, which a still screen otherwise never would. At 1% opacity it can't show up in the capture.
    sealed class FrameNudge : Form
    {
        FrameNudge(Point at)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(at, new Size(2, 2));
            BackColor = Color.Black;
            Opacity = 0.01;
        }

        public static FrameNudge ShowAt(Point at)
        {
            var nudge = new FrameNudge(at);
            nudge.Show();
            nudge.Update();
            return nudge;
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
                cp.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8; // NOACTIVATE, TOOLWINDOW, TRANSPARENT, TOPMOST
                return cp;
            }
        }
    }

    // Just enough DXGI and Direct3D 11, called through the COM vtables.
    static class Dxgi
    {
        public static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        public static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        public static readonly Guid IID_IDXGIOutput5 = new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        public static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        public const int FORMAT_R16G16B16A16_FLOAT = 10, FORMAT_B8G8R8A8_UNORM = 87, FORMAT_B8G8R8A8_UNORM_SRGB = 91;
        public const int WAIT_TIMEOUT = unchecked((int)0x887A0027);

        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        public static T Method<T>(IntPtr com, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size), typeof(T));
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetByIndex(IntPtr self, uint index, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetOutputDesc(IntPtr self, out OUTPUT_DESC desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DuplicateOutput(IntPtr self, IntPtr device, out IntPtr duplication);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DuplicateOutput1(IntPtr self, IntPtr device, uint flags, uint formatCount, int[] formats, out IntPtr duplication);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int AcquireNextFrame(IntPtr self, uint timeoutMs, out FRAME_INFO info, out IntPtr resource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int NoArgs(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void GetTextureDesc(IntPtr self, out TEXTURE2D_DESC desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int CreateTexture2D(IntPtr self, ref TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int Map(IntPtr self, IntPtr resource, uint subresource, int mapType, uint flags, out MAPPED mapped);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void Unmap(IntPtr self, IntPtr resource, uint subresource);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation; // 0 unspecified, 1 identity
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MAPPED
        {
            public IntPtr Data;
            public uint RowPitch;
            public uint DepthPitch;
        }
    }
}
