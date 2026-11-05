using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SdrBrightness
{
    // One active monitor, addressed the way both the display-config and color-profile APIs expect.
    sealed class HdrDisplay
    {
        public string Name;
        public string DevicePath;
        public string GdiName; // \\.\DISPLAY1, as DXGI names its outputs
        public LUID AdapterId;
        public uint TargetId;
        public uint SourceId;
        public bool HdrSupported;
        public bool HdrOn;
        public bool IsMain; // Windows' main display

        // The Settings slider runs 0..100 over 80..480 nits; the API counts in 1/1000ths of 80 nits.
        public static int LevelFromSlider(int slider)
        {
            return 1000 + slider * 50;
        }

        public static int SliderFromLevel(int level)
        {
            return (int)Math.Round((level - 1000) / 50.0);
        }

        // SDR white for a slider position: 80 nits at 0, 4 more per step, 480 at 100.
        public static int NitsFromSlider(int slider)
        {
            return 80 + slider * 4;
        }

        // EDID maker + product code, e.g. "DELA2F9" from \\?\DISPLAY#DELA2F9#5&...#{...}.
        public string ModelId
        {
            get
            {
                string[] parts = (DevicePath ?? "").Split('#');
                string id = parts.Length > 2 ? parts[1] : "";
                foreach (char ch in id)
                    if (!char.IsLetterOrDigit(ch))
                        return "Display";
                return id.Length > 0 ? id : "Display";
            }
        }

        public static List<HdrDisplay> GetAll()
        {
            var list = new List<HdrDisplay>();
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            uint pathCount, modeCount;
            int err;
            do
            {
                err = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                if (err != 0)
                    return list;
                paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
                err = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            } while (err == Native.ERROR_INSUFFICIENT_BUFFER);
            if (err != 0)
                return list;

            string main = Native.MainDisplayName();
            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                if (list.Exists(d => d.TargetId == path.targetInfo.id && SameAdapter(d.AdapterId, path.targetInfo.adapterId)))
                    continue; // cloned outputs

                var display = new HdrDisplay
                {
                    AdapterId = path.targetInfo.adapterId,
                    TargetId = path.targetInfo.id,
                    SourceId = path.sourceInfo.id,
                };

                var name = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                name.header = display.Header(Native.DEVICE_INFO_GET_TARGET_NAME, Marshal.SizeOf(name));
                if (Native.DisplayConfigGetDeviceInfo(ref name) == 0)
                {
                    display.Name = name.monitorFriendlyDeviceName;
                    display.DevicePath = name.monitorDevicePath;
                }
                if (string.IsNullOrEmpty(display.Name))
                    display.Name = "Display " + (list.Count + 1);
                if (string.IsNullOrEmpty(display.DevicePath))
                    display.DevicePath = display.Name;

                var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header = display.Header(Native.DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf(source));
                source.header.id = display.SourceId;
                if (Native.DisplayConfigGetDeviceInfo(ref source) == 0)
                    display.GdiName = source.viewGdiDeviceName;
                display.IsMain = main != null && string.Equals(display.GdiName, main, StringComparison.OrdinalIgnoreCase);

                display.ReadColorState();
                list.Add(display);
            }
            return list;
        }

        static bool SameAdapter(LUID a, LUID b)
        {
            return a.LowPart == b.LowPart && a.HighPart == b.HighPart;
        }

        Native.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size)
        {
            return new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = type,
                size = (uint)size,
                adapterId = AdapterId,
                id = TargetId,
            };
        }

        void ReadColorState()
        {
            // Windows 11 24H2+ reports the active mode directly; older builds only have the v1 flags.
            var info2 = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
            info2.header = Header(Native.DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf(info2));
            if (Native.DisplayConfigGetDeviceInfo(ref info2) == 0)
            {
                HdrSupported = (info2.value & 0x10) != 0;
                HdrOn = info2.activeColorMode == Native.ADVANCED_COLOR_MODE_HDR;
                return;
            }

            var info = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header = Header(Native.DEVICE_INFO_GET_ADVANCED_COLOR_INFO, Marshal.SizeOf(info));
            if (Native.DisplayConfigGetDeviceInfo(ref info) == 0)
            {
                HdrSupported = (info.value & 0x1) != 0;
                HdrOn = (info.value & 0x2) != 0 && (info.value & 0x4) == 0;
            }
        }

        public int? GetSdrWhiteLevel()
        {
            var packet = new Native.DISPLAYCONFIG_SDR_WHITE_LEVEL();
            packet.header = Header(Native.DEVICE_INFO_GET_SDR_WHITE_LEVEL, Marshal.SizeOf(packet));
            if (Native.DisplayConfigGetDeviceInfo(ref packet) != 0)
                return null;
            return (int)packet.SDRWhiteLevel;
        }

        // Returns a Win32 error code (0 on success).
        public int SetSdrWhiteLevel(int level, bool final)
        {
            var packet = new Native.DISPLAYCONFIG_SET_SDR_WHITE_LEVEL();
            packet.header = Header(Native.DEVICE_INFO_SET_SDR_WHITE_LEVEL, Marshal.SizeOf(packet));
            packet.SDRWhiteLevel = (uint)level;
            packet.finalValue = (byte)(final ? 1 : 0);
            return Native.DisplayConfigSetDeviceInfo(ref packet);
        }

        // Follows the "Use my settings for this device" choice in Color Management.
        int ProfileScope()
        {
            int scope;
            if (Native.ColorProfileGetDisplayUserScope(AdapterId, SourceId, out scope) == 0)
                return scope;
            return Native.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
        }

        // File name of the default HDR (advanced color) profile, or null.
        public string GetDefaultProfile()
        {
            IntPtr name;
            int hr = Native.ColorProfileGetDisplayDefault(ProfileScope(), AdapterId, SourceId,
                Native.CPT_ICC, Native.CPST_EXTENDED_DISPLAY_COLOR_MODE, out name);
            if (hr != 0 || name == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Native.LocalFree(name);
            }
        }

        // Makes the profile the default HDR profile, associating it first if needed. Returns an HRESULT.
        public int SetDefaultProfile(string fileName)
        {
            int scope = ProfileScope();
            int hr = Native.ColorProfileSetDisplayDefaultAssociation(scope, fileName, Native.CPT_ICC,
                Native.CPST_EXTENDED_DISPLAY_COLOR_MODE, AdapterId, SourceId);
            if (hr != 0)
                hr = Native.ColorProfileAddDisplayAssociation(scope, fileName, AdapterId, SourceId, true, true);
            return hr;
        }
    }
}
