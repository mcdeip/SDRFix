using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SdrBrightness
{
    sealed class ColorProfile
    {
        public string FileName;
        public string Description;
        public double PeakNits;  // lumi tag, 0 when absent
        public string Model;     // set on profiles created by setup
        public string Gamma;     // "2.2" / "2.4" on setup's SDR profiles
        public bool IsHdr;       // gamma-neutral profile for HDR content

        public string Label
        {
            get { return string.IsNullOrEmpty(Description) ? FileName : Description; }
        }
    }

    // Indexes the HDR (MHC2) profiles in the system color folder:
    // - setup's sets, "SdrBrightness-DELA2F9-2.2-040.icm" (and "...-HDR.icm"), per display and gamma;
    // - hand-made ones, where "40.icm" or a description like "40 (sRGB built-in)" belongs to slider value 40,
    //   and "HDR.icc" / description "HDR" is the gamma-neutral profile for HDR content.
    static class ProfileLibrary
    {
        static readonly object gate = new object();
        static Dictionary<int, ColorProfile> byValue = new Dictionary<int, ColorProfile>();
        static Dictionary<string, ColorProfile> byFile = new Dictionary<string, ColorProfile>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, ColorProfile> generated = new Dictionary<string, ColorProfile>(StringComparer.OrdinalIgnoreCase);
        static ColorProfile hdrProfile;
        static string scannedDir;
        static DateTime scannedStamp;

        static string Key(string model, string gamma, int value)
        {
            return model + "|" + gamma + "|" + value;
        }

        // Once setup has made profiles for this display, only its set for `gamma` is used.
        public static ColorProfile Find(string model, string gamma, int value)
        {
            lock (gate)
            {
                Rescan();
                ColorProfile profile;
                if (HasSet(model, null))
                    return generated.TryGetValue(Key(model, gamma, value), out profile) ? profile : null;
                return byValue.TryGetValue(value, out profile) ? profile : null;
            }
        }

        // Setup's HDR profile for this display (0 nits to its peak) once it exists, else one you named HDR.
        public static ColorProfile HdrFor(string model)
        {
            lock (gate)
            {
                Rescan();
                ColorProfile profile;
                return generated.TryGetValue(Key(model, "HDR", 0), out profile) ? profile : hdrProfile;
            }
        }

        // True when setup has made SDR profiles for this display (in `gamma`, or in any gamma when null).
        public static bool HasGamma(string model, string gamma)
        {
            lock (gate)
            {
                Rescan();
                return HasSet(model, gamma);
            }
        }

        static bool HasSet(string model, string gamma)
        {
            foreach (ColorProfile profile in generated.Values)
                if (profile.Gamma != null && string.Equals(profile.Model, model, StringComparison.OrdinalIgnoreCase)
                    && (gamma == null || profile.Gamma == gamma))
                    return true;
            return false;
        }

        public static ColorProfile Get(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            lock (gate)
            {
                Rescan();
                ColorProfile profile;
                return byFile.TryGetValue(fileName, out profile) ? profile : null;
            }
        }

        public static string Describe(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "none";
            ColorProfile profile = Get(fileName);
            return profile != null ? profile.Label : fileName;
        }

        public static bool IsHdr(string fileName)
        {
            ColorProfile profile = Get(fileName);
            return profile != null && profile.IsHdr;
        }

        // Forces a rescan; rewriting files in place doesn't touch the folder's timestamp.
        public static void Invalidate()
        {
            lock (gate)
                scannedDir = null;
        }

        public static string Directory
        {
            get
            {
                var buffer = new StringBuilder(260);
                uint size = (uint)(buffer.Capacity * 2);
                if (Native.GetColorDirectoryW(null, buffer, ref size))
                    return buffer.ToString();
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color");
            }
        }

        static void Rescan()
        {
            string dir = Directory;
            DateTime stamp;
            try
            {
                stamp = System.IO.Directory.GetLastWriteTimeUtc(dir);
            }
            catch (Exception)
            {
                return;
            }
            if (dir == scannedDir && stamp == scannedStamp)
                return;

            var values = new Dictionary<int, ColorProfile>();
            var priorities = new Dictionary<int, int>();
            var files = new Dictionary<string, ColorProfile>(StringComparer.OrdinalIgnoreCase);
            var sets = new Dictionary<string, ColorProfile>(StringComparer.OrdinalIgnoreCase);
            ColorProfile hdr = null;
            int hdrPriority = 0;

            string[] paths;
            try
            {
                paths = System.IO.Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                return;
            }
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                string ext = Path.GetExtension(path);
                if (!ext.Equals(".icm", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".icc", StringComparison.OrdinalIgnoreCase))
                    continue;
                string description;
                double peak;
                if (!TryReadHdrProfile(path, out description, out peak))
                    continue;

                var profile = new ColorProfile { FileName = Path.GetFileName(path), Description = description, PeakNits = peak };
                files[profile.FileName] = profile;
                string stem = Path.GetFileNameWithoutExtension(path).Trim();

                if (ParseGenerated(stem, profile))
                {
                    sets[profile.IsHdr ? Key(profile.Model, "HDR", 0) : Key(profile.Model, profile.Gamma, ValueOf(stem))] = profile;
                    continue;
                }

                // A bare number as the file name wins over a number at the start of the description.
                int value = LeadingNumber(stem, true);
                int priority = 2;
                if (value < 0)
                {
                    value = LeadingNumber(description, false);
                    priority = 1;
                }
                if (value >= 0)
                {
                    int existing;
                    if (!priorities.TryGetValue(value, out existing) || priority > existing)
                    {
                        values[value] = profile;
                        priorities[value] = priority;
                    }
                }

                int candidate = stem.Equals("HDR", StringComparison.OrdinalIgnoreCase) ? 2
                    : (description != null && description.Trim().Equals("HDR", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                if (candidate > 0)
                    profile.IsHdr = true;
                if (candidate > hdrPriority)
                {
                    hdr = profile;
                    hdrPriority = candidate;
                }
            }

            byValue = values;
            byFile = files;
            generated = sets;
            hdrProfile = hdr;
            scannedDir = dir;
            scannedStamp = stamp;
        }

        // "SdrBrightness-<model>-<gamma>-<value>" or "SdrBrightness-<model>-HDR".
        static bool ParseGenerated(string stem, ColorProfile profile)
        {
            if (!stem.StartsWith(ProfileGenerator.Prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            string[] parts = stem.Substring(ProfileGenerator.Prefix.Length).Split('-');
            if (parts.Length == 2 && parts[1].Equals("HDR", StringComparison.OrdinalIgnoreCase))
            {
                profile.Model = parts[0];
                profile.IsHdr = true;
                return true;
            }
            if (parts.Length == 3 && Array.IndexOf(ProfileGenerator.Gammas, parts[1]) >= 0 && ValueOf(stem) >= 0)
            {
                profile.Model = parts[0];
                profile.Gamma = parts[1];
                return true;
            }
            return false;
        }

        static int ValueOf(string stem)
        {
            int dash = stem.LastIndexOf('-'), value;
            if (dash < 0 || !int.TryParse(stem.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value > 100)
                return -1;
            return value;
        }

        // Value 0..100 from "40" (whole) or "40 (sRGB built-in)" (leading), else -1.
        static int LeadingNumber(string text, bool whole)
        {
            if (string.IsNullOrEmpty(text))
                return -1;
            text = text.Trim();
            int i = 0, n = 0;
            while (i < text.Length && i < 4 && text[i] >= '0' && text[i] <= '9')
                n = n * 10 + (text[i++] - '0');
            if (i == 0 || n > 100)
                return -1;
            if (i < text.Length && (whole || char.IsLetterOrDigit(text[i])))
                return -1;
            return n;
        }

        // True for ICC profiles carrying an MHC2 tag (the ones Windows applies in HDR mode).
        static bool TryReadHdrProfile(string path, out string description, out double peak)
        {
            description = null;
            peak = 0;
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 132 || info.Length > (4 << 20))
                    return false;
                byte[] b = File.ReadAllBytes(path);
                if (Encoding.ASCII.GetString(b, 36, 4) != "acsp")
                    return false;

                uint count = BE(b, 128);
                bool mhc2 = false;
                for (int i = 0; i < count && i < 256; i++)
                {
                    int entry = 132 + i * 12;
                    if (entry + 12 > b.Length)
                        break;
                    string sig = Encoding.ASCII.GetString(b, entry, 4);
                    uint offset = BE(b, entry + 4);
                    if (sig == "MHC2")
                        mhc2 = true;
                    else if (sig == "desc")
                        description = ReadText(b, offset);
                    else if (sig == "lumi" && offset + 20 <= b.Length)
                        peak = (int)BE(b, (int)offset + 12) / 65536.0;
                }
                return mhc2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string ReadText(byte[] b, uint offset)
        {
            long off = offset;
            if (off + 12 > b.Length)
                return null;
            string type = Encoding.ASCII.GetString(b, (int)off, 4);
            if (type == "mluc")
            {
                if (off + 28 > b.Length || BE(b, (int)off + 8) == 0)
                    return null;
                long length = BE(b, (int)off + 20), start = off + BE(b, (int)off + 24);
                if (start + length > b.Length)
                    return null;
                return Encoding.BigEndianUnicode.GetString(b, (int)start, (int)length).TrimEnd('\0');
            }
            if (type == "desc")
            {
                long length = BE(b, (int)off + 8);
                if (off + 12 + length > b.Length)
                    return null;
                return Encoding.ASCII.GetString(b, (int)off + 12, (int)length).TrimEnd('\0');
            }
            return null;
        }

        static uint BE(byte[] b, int o)
        {
            return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        }
    }
}
