using System;
using System.Runtime.InteropServices;

namespace SdrBrightness
{
    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    static class Native
    {
        // ---- Display configuration (user32) ----

        public const uint QDC_ONLY_ACTIVE_PATHS = 2;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        public const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        public const uint DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;
        public const uint DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
        // Undocumented: what the Settings app's "SDR content brightness" slider uses.
        public const uint DEVICE_INFO_SET_SDR_WHITE_LEVEL = 0xFFFFFFEE;

        public const uint ADVANCED_COLOR_MODE_HDR = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // Only the size matters; mode details are never read.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName; // \\.\DISPLAY1
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit 0 supported, bit 1 enabled, bit 2 wide color enforced
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit 4 HDR supported, bit 5 HDR user enabled
            public uint colorEncoding;
            public uint bitsPerColorChannel;
            public uint activeColorMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel; // 1000 = 80 nits
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
            public byte finalValue; // 0 while dragging, 1 to commit
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_SDR_WHITE_LEVEL setPacket);

        // ---- Color profiles (mscms) ----

        public const int WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER = 1;
        public const int CPT_ICC = 0;
        public const int CPST_EXTENDED_DISPLAY_COLOR_MODE = 8;

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profileName, int profileType,
            int profileSubType, LUID targetAdapterID, uint sourceID);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileAddDisplayAssociation(int scope, string profileName, LUID targetAdapterID,
            uint sourceID, bool setAsDefault, bool associateAsAdvancedColor);

        [DllImport("mscms.dll")]
        public static extern int ColorProfileGetDisplayDefault(int scope, LUID targetAdapterID, uint sourceID,
            int profileType, int profileSubType, out IntPtr profileName);

        [DllImport("mscms.dll")]
        public static extern int ColorProfileGetDisplayUserScope(LUID targetAdapterID, uint sourceID, out int scope);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetColorDirectoryW(string machineName, System.Text.StringBuilder buffer, ref uint size);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr mem);

        // ---- Windows / shell ----

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // The visible frame in physical pixels, without the invisible resize borders.
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        // Waits for the next desktop composition.
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int size);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        // Switches this thread to per-monitor DPI awareness until disposed, so window and desktop
        // coordinates are physical pixels on every display. Windows created meanwhile keep it.
        public static IDisposable PerMonitorDpi()
        {
            try
            {
                return new DpiScope(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));
            }
            catch (EntryPointNotFoundException)
            {
                return new DpiScope(IntPtr.Zero); // before Windows 10 1607
            }
        }

        sealed class DpiScope : IDisposable
        {
            readonly IntPtr previous;

            public DpiScope(IntPtr previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                if (previous != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previous);
            }
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint MAPVK_VK_TO_CHAR = 2;

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int vk);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemes();

        // Lets native popup menus follow the system dark/light theme.
        public static void AllowDarkMenus()
        {
            try
            {
                SetPreferredAppMode(1); // AllowDark
                FlushMenuThemes();
            }
            catch (Exception)
            {
                // Older Windows builds: menus just stay light.
            }
        }
    }
}
