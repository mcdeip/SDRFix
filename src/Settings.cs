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

        // Which of setup's profile sets a display uses, "2.2" or "2.4". Each display model keeps its own
        // choice; one that hasn't made any yet starts from the last choice made on any display.
        public static string GetGamma(string model)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Key))
            {
                string value = key == null ? null : (key.GetValue("Gamma-" + model) as string ?? key.GetValue("Gamma") as string);
                return Array.IndexOf(ProfileGenerator.Gammas, value) >= 0 ? value : ProfileGenerator.Gammas[0];
            }
        }

        public static void SetGamma(string model, string gamma)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                key.SetValue("Gamma", gamma);
                if (!string.IsNullOrEmpty(model))
                    key.SetValue("Gamma-" + model, gamma);
            }
        }

        // With several HDR displays: whether their sliders move together, and the brightness shortcuts change
        // all of them rather than just the display with the active window.
        public static bool LinkDisplays
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object value = key == null ? null : key.GetValue("LinkDisplays");
                    return !(value is int) || (int)value != 0;
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    key.SetValue("LinkDisplays", value ? 1 : 0, RegistryValueKind.DWord);
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
