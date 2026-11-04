using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SdrBrightness
{
    static class Settings
    {
        const string Key = @"Software\SdrBrightness";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "SdrBrightness";

        // Monitor device path of the display to control when more than one is connected.
        public static string Display
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                    return key == null ? null : key.GetValue("Display") as string;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    key.SetValue("Display", value ?? "");
            }
        }

        // Which of setup's profile sets the slider uses: "2.2" or "2.4".
        public static string Gamma
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    string value = key == null ? null : key.GetValue("Gamma") as string;
                    return Array.IndexOf(ProfileGenerator.Gammas, value) >= 0 ? value : ProfileGenerator.Gammas[0];
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    key.SetValue("Gamma", value);
            }
        }

        // Whether the slider shows its 2.2 | 2.4 row.
        public static bool ShowGammaToggle
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object value = key == null ? null : key.GetValue("ShowGammaToggle");
                    return !(value is int) || (int)value != 0;
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    key.SetValue("ShowGammaToggle", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        // Set once setup has been offered on first launch, whether or not it was run.
        public static bool SetupOffered
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                    return key != null && key.GetValue("SetupOffered") is int && (int)key.GetValue("SetupOffered") != 0;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    key.SetValue("SetupOffered", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        public static Hotkey GetHotkey(HotkeyAction action)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Key))
            {
                object value = key == null ? null : key.GetValue("Hotkey" + action);
                return value is int ? Hotkey.FromPacked((int)value) : Hotkey.Default(action);
            }
        }

        public static void SetHotkey(HotkeyAction action, Hotkey hotkey)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
                key.SetValue("Hotkey" + action, hotkey.Packed, RegistryValueKind.DWord);
        }

        public static readonly int[] HotkeySteps = { 5, 10, 15, 20, 25 };

        // Slider units per shortcut press; a multiple of 5 so every stop has a profile.
        public static int HotkeyStep
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object value = key == null ? null : key.GetValue("HotkeyStep");
                    return value is int && Array.IndexOf(HotkeySteps, (int)value) >= 0 ? (int)value : HotkeySteps[0];
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    key.SetValue("HotkeyStep", value, RegistryValueKind.DWord);
            }
        }

        public static string DefaultScreenshotFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SDRFix Screenshots"); }
        }

        // Where screenshots are saved as PNG files. The default isn't stored, so it follows Documents if that moves.
        public static string ScreenshotFolder
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    string value = key == null ? null : key.GetValue("ScreenshotFolder") as string;
                    return string.IsNullOrEmpty(value) ? DefaultScreenshotFolder : value;
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (string.IsNullOrEmpty(value) || string.Equals(value.TrimEnd('\\'), DefaultScreenshotFolder, StringComparison.OrdinalIgnoreCase))
                        key.DeleteValue("ScreenshotFolder", false);
                    else
                        key.SetValue("ScreenshotFolder", value);
                }
            }
        }

        static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        public static bool StartWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && string.Equals(key.GetValue(RunValue) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value)
                        key.SetValue(RunValue, Command);
                    else
                        key.DeleteValue(RunValue, false);
                }
            }
        }
    }
}
