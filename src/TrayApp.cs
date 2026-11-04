using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SdrBrightness
{
    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenu menu = new ContextMenu();
        readonly BrightnessPopup popup = new BrightnessPopup();
        readonly Osd osd = new Osd();
        readonly HotkeyManager hotkeys = new HotkeyManager();
        readonly System.Windows.Forms.Timer hotkeyCommit = new System.Windows.Forms.Timer { Interval = 400 };
        readonly Applier applier;

        List<HdrDisplay> displays = new List<HdrDisplay>();
        HdrDisplay display;
        int rawValue = -1;     // Windows' slider position; can be off-step if moved in Settings
        string activeProfile;  // file name of the default HDR profile
        string lastError;
        bool hdrWanted;        // the HDR content profile was last asked for, even if Windows hasn't caught up yet
        int commitSeq;
        int hotkeyValue = -1;  // where shortcut presses have moved the slider before they commit
        IntPtr iconHandle;
        SetupWindow setup;
        ShortcutsWindow shortcuts;
        RegionPicker picker;

        public TrayApp()
        {
            IntPtr unused = popup.Handle; // gives the applier a thread to report back on
            applier = new Applier(popup);
            applier.Applied += OnApplied;

            popup.Previewed += value =>
            {
                if (display != null && display.HdrOn)
                    applier.Submit(new ApplyRequest { Display = display, Value = value });
            };
            popup.Committed += value => Commit(value, popup.HdrOn);
            popup.StatusClicked += () => Commit(popup.Value, popup.HdrOn);
            popup.HdrToggled += on => Commit(popup.Value, on);
            popup.GammaChanged += gamma =>
            {
                Settings.Gamma = gamma;
                Commit(popup.Value, popup.HdrOn);
            };
            popup.SetupClicked += () => ShowSetup(false);
            hotkeys.Pressed += OnHotkey;
            hotkeyCommit.Tick += delegate { FlushHotkey(); };

            menu.Popup += delegate
            {
                // Another app may have released a shortcut it was holding.
                if (hotkeys.Failed.Count > 0 && shortcuts == null)
                    hotkeys.Apply();
                RefreshState();
                BuildMenu();
            };
            tray.ContextMenu = menu;
            tray.MouseClick += OnTrayClick;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            RefreshState();
            UpdateTrayIcon();
            tray.Visible = true;
            hotkeys.Apply();

            if (!Settings.SetupOffered)
                popup.BeginInvoke((Action)delegate { ShowSetup(true); });
        }

        string Model
        {
            get { return display != null ? display.ModelId : null; }
        }

        int CurrentValue
        {
            get { return BrightnessPopup.Snap(rawValue >= 0 ? rawValue : popup.Value); }
        }

        // Called from another thread when the exe is launched a second time.
        public void ShowFromSignal()
        {
            popup.BeginInvoke((Action)ShowPopupAtTray);
        }

        void ShowPopupAtTray()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ShowPopup(new Point(work.Right, work.Bottom));
        }

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (popup.Visible)
                popup.HidePopup();
            else if (unchecked(Environment.TickCount - popup.LastHiddenTick) > 400) // else this click just closed it
                ShowPopup(Cursor.Position);
        }

        void ShowPopup(Point anchor)
        {
            RefreshState();
            popup.ShowNear(anchor);
        }

        void Commit(int value, bool hdr, bool reload = false)
        {
            if (display == null || !display.HdrOn)
                return;
            hdrWanted = hdr;
            popup.SetStatus("Applying…", StatusKind.Busy, false);
            applier.Submit(new ApplyRequest
            {
                Display = display,
                Value = value,
                Final = true,
                Hdr = hdr,
                Gamma = Settings.Gamma,
                Reload = reload,
                Seq = ++commitSeq,
            });
        }

        void OnApplied(ApplyResult result)
        {
            rawValue = result.Value;
            activeProfile = result.ActiveProfile;
            lastError = result.Error;
            if (result.Seq == commitSeq)
                hdrWanted = ProfileLibrary.IsHdr(activeProfile);
            UpdateTooltip();
            if (!popup.Interacting && popup.Value == result.Value)
                PushToPopup();
        }

        // ---- Shortcuts ----

        void OnHotkey(HotkeyAction action)
        {
            if (action == HotkeyAction.ScreenshotWindow || action == HotkeyAction.ScreenshotDesktop || action == HotkeyAction.ScreenshotRegion)
            {
                Screenshot(action); // with HDR off too
                return;
            }
            if (display == null || !display.HdrOn)
                RefreshState();
            if (display == null || !display.HdrOn)
            {
                osd.ShowText(Theme.GlyphWarning, display == null ? "No display found" : "HDR is off on " + display.Name);
                return;
            }

            if (action == HotkeyAction.ToggleHdr)
            {
                FlushHotkey();
                if (ProfileLibrary.HdrFor(Model) == null)
                {
                    osd.ShowText(Theme.GlyphWarning, "No HDR profile · run setup from the tray menu");
                    return;
                }
                bool on = !hdrWanted;
                Commit(CurrentValue, on);
                osd.ShowText(Theme.GlyphBrightness, on ? "HDR content profile on" : "HDR content profile off · gamma " + Settings.Gamma);
                if (popup.Visible && !popup.Interacting)
                    PushToPopup();
                return;
            }

            int step = Settings.HotkeyStep * (action == HotkeyAction.BrightnessUp ? 1 : -1);
            int next = BrightnessPopup.Snap((hotkeyValue >= 0 ? hotkeyValue : CurrentValue) + step);
            hotkeyValue = next;
            applier.Submit(new ApplyRequest { Display = display, Value = next }); // brightness now, profile once presses stop
            hotkeyCommit.Stop();
            hotkeyCommit.Start();
            if (popup.Visible)
            {
                if (!popup.Interacting)
                {
                    rawValue = next;
                    PushToPopup();
                }
            }
            else
                osd.ShowLevel(next);
        }

        void FlushHotkey()
        {
            hotkeyCommit.Stop();
            if (hotkeyValue < 0)
                return;
            int value = hotkeyValue;
            hotkeyValue = -1;
            Commit(value, hdrWanted);
        }

        // ---- Screenshots ----

        void Screenshot(HotkeyAction action)
        {
            if (picker != null)
                return; // already picking a region
            if (osd.Visible)
            {
                osd.Hide(); // keep the indicator out of the picture
                Native.DwmFlush();
                Native.DwmFlush();
            }
            try
            {
                using (Native.PerMonitorDpi())
                {
                    if (action == HotkeyAction.ScreenshotRegion)
                    {
                        List<DesktopImage> all = ScreenCapture.Capture(null);
                        picker = new RegionPicker(all);
                        picker.Picked += area => CopyScreenshot(all, area, "Region");
                        picker.Closed += delegate { picker = null; };
                        picker.Show();
                        return;
                    }
                    Rectangle? target = action == HotkeyAction.ScreenshotWindow ? ActiveWindowBounds() : (Rectangle?)null;
                    List<DesktopImage> images = ScreenCapture.Capture(target);
                    Rectangle shot = ScreenCapture.Union(images);
                    if (target.HasValue)
                        shot = Rectangle.Intersect(shot, target.Value);
                    CopyScreenshot(images, shot, action == HotkeyAction.ScreenshotWindow ? "Window" : "Desktop");
                }
            }
            catch (Exception ex)
            {
                picker = null;
                osd.ShowText(Theme.GlyphWarning, "Screenshot failed · " + ex.Message);
            }
        }

        // The foreground window's visible frame, or the display under the pointer for the desktop and taskbar.
        static Rectangle ActiveWindowBounds()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            var name = new StringBuilder(64);
            if (hwnd != IntPtr.Zero)
                Native.GetClassName(hwnd, name, name.Capacity);
            string kind = name.ToString();
            Native.RECT r;
            if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd) || kind == "Progman" || kind == "WorkerW" ||
                kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd" ||
                Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                return Screen.FromPoint(Cursor.Position).Bounds;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        void CopyScreenshot(IList<DesktopImage> images, Rectangle area, string what)
        {
            if (area.Width <= 0 || area.Height <= 0)
            {
                osd.ShowText(Theme.GlyphWarning, "Nothing on screen to capture");
                return;
            }
            bool toneMapped, copied = true, saved = true;
            using (Bitmap bitmap = ScreenCapture.Render(images, area, out toneMapped))
            using (var png = new MemoryStream())
            {
                bitmap.Save(png, ImageFormat.Png);
                try
                {
                    SavePng(png);
                }
                catch (Exception)
                {
                    saved = false; // a missing drive or a folder we can't write to; the clipboard still gets it
                }
                // A bitmap for every app, plus PNG for the ones that prefer it.
                png.Position = 0;
                var data = new DataObject();
                data.SetData(DataFormats.Bitmap, true, bitmap);
                data.SetData("PNG", false, png);
                try
                {
                    Clipboard.SetDataObject(data, true, 5, 100);
                }
                catch (ExternalException)
                {
                    copied = false;
                }
            }
            if (copied && saved)
                osd.ShowText(Theme.GlyphCamera, what + " copied and saved" + (toneMapped ? " · HDR tone-mapped" : ""));
            else if (copied)
                osd.ShowText(Theme.GlyphWarning, what + " copied · couldn't save it to the screenshot folder");
            else if (saved)
                osd.ShowText(Theme.GlyphWarning, what + " saved · couldn't open the clipboard");
            else
                osd.ShowText(Theme.GlyphWarning, "Couldn't copy or save the screenshot");
        }

        // "Screenshot 2026-11-04 142233.png", numbered when several land in the same second.
        static void SavePng(MemoryStream png)
        {
            string folder = Settings.ScreenshotFolder;
            Directory.CreateDirectory(folder);
            string stem = "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, stem + ".png");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, stem + " (" + n + ").png");
            using (var file = new FileStream(path, FileMode.CreateNew))
                png.WriteTo(file);
        }

        void OpenScreenshotFolder()
        {
            if (!FolderPicker.Open(Settings.ScreenshotFolder))
                osd.ShowText(Theme.GlyphWarning, "Couldn't open the screenshot folder");
        }

        // ---- Windows ----

        void ShowSetup(bool firstRun)
        {
            Settings.SetupOffered = true;
            if (setup != null)
            {
                setup.Activate();
                return;
            }
            popup.HidePopup();
            RefreshState();
            setup = new SetupWindow(display, Settings.Gamma, SuggestedPeak(), firstRun);
            setup.ProfilesCreated += OnProfilesCreated;
            setup.FormClosed += delegate { setup = null; };
            setup.Show();
            setup.Activate();
        }

        // The peak the current profiles already declare, so a re-run starts from it.
        int SuggestedPeak()
        {
            foreach (ColorProfile profile in new[] { ProfileLibrary.Get(activeProfile), ProfileLibrary.HdrFor(Model) })
                if (profile != null && profile.PeakNits >= 100 && profile.PeakNits <= 10000)
                    return (int)Math.Round(profile.PeakNits);
            return 1000;
        }

        void OnProfilesCreated(string gamma)
        {
            Settings.Gamma = gamma;
            RefreshState();
            Commit(CurrentValue, hdrWanted, true);
            // After the setup window has closed, so closing it doesn't dismiss the slider.
            popup.BeginInvoke((Action)ShowPopupAtTray);
        }

        void ShowShortcuts()
        {
            if (shortcuts != null)
            {
                shortcuts.Activate();
                return;
            }
            popup.HidePopup();
            FlushHotkey();
            shortcuts = new ShortcutsWindow(hotkeys);
            shortcuts.FormClosed += delegate { shortcuts = null; };
            shortcuts.Show();
            shortcuts.Activate();
        }

        // Re-reads displays, brightness and the active profile from Windows.
        void RefreshState()
        {
            displays = HdrDisplay.GetAll();
            string saved = Settings.Display;
            display = displays.FirstOrDefault(d => d.DevicePath == saved)
                ?? displays.FirstOrDefault(d => d.HdrOn)
                ?? displays.FirstOrDefault(d => d.HdrSupported)
                ?? displays.FirstOrDefault();

            rawValue = -1;
            activeProfile = null;
            lastError = null;
            if (display != null)
            {
                int? level = display.GetSdrWhiteLevel();
                if (level.HasValue)
                    rawValue = Math.Max(0, Math.Min(100, HdrDisplay.SliderFromLevel(level.Value)));
                activeProfile = display.GetDefaultProfile();
            }
            hdrWanted = ProfileLibrary.IsHdr(activeProfile);
            if (!popup.Interacting)
                PushToPopup();
            UpdateTooltip();
        }

        bool[] GammaAvailability()
        {
            var available = new bool[ProfileGenerator.Gammas.Length];
            for (int i = 0; i < available.Length; i++)
                available[i] = display != null && ProfileLibrary.HasGamma(Model, ProfileGenerator.Gammas[i]);
            return available;
        }

        void PushToPopup()
        {
            ColorProfile hdrProfile = ProfileLibrary.HdrFor(Model);
            bool ready = display != null && display.HdrOn;
            bool hdrActive = ProfileLibrary.IsHdr(activeProfile);
            string gamma = Settings.Gamma;
            string hint = hdrProfile == null ? "No HDR profile yet · setup creates one"
                : hdrProfile.Model != null ? "No gamma curve · 0–" + Math.Round(hdrProfile.PeakNits) + " nits"
                : "Applies " + hdrProfile.FileName + " · no gamma curve";
            string name = displays.Count(d => d.HdrSupported) > 1 && display != null ? display.Name : null;
            popup.SetState(rawValue >= 0 ? rawValue : popup.Value, ready, hdrActive, ready && hdrProfile != null, hint, name);
            popup.SetGamma(gamma, GammaAvailability(), Settings.ShowGammaToggle);

            if (display == null)
                popup.SetStatus("No active display found", StatusKind.Error, false);
            else if (!display.HdrOn)
                popup.SetStatus("HDR is off on " + display.Name + " · turn it on in Display settings", StatusKind.Info, false);
            else if (lastError != null)
                popup.SetStatus(lastError + " · click to retry", StatusKind.Error, true);
            else if (hdrActive)
                popup.SetStatus("HDR profile active · the slider only changes brightness", StatusKind.Info, false);
            else if (rawValue >= 0 && rawValue % BrightnessPopup.Step != 0)
                popup.SetStatus("Windows is set to " + rawValue + " · click to snap to " + popup.Value, StatusKind.Warning, true);
            else
            {
                ColorProfile expected = ProfileLibrary.Find(Model, gamma, popup.Value);
                if (expected == null)
                    popup.SetStatus("No profile for " + popup.Value + " · brightness only", StatusKind.Warning, false);
                else if (string.Equals(activeProfile, expected.FileName, StringComparison.OrdinalIgnoreCase))
                    popup.SetStatus(expected.Label, StatusKind.Success, false);
                else
                    popup.SetStatus("Active profile: " + ProfileLibrary.Describe(activeProfile) + " · click to fix",
                        StatusKind.Warning, true);
            }
        }

        void BuildMenu()
        {
            menu.MenuItems.Clear();
            bool ready = display != null && display.HdrOn;
            bool hdrActive = ProfileLibrary.IsHdr(activeProfile);
            string gamma = Settings.Gamma;

            menu.MenuItems.Add(new MenuItem("Open slider", delegate { ShowPopup(Cursor.Position); }) { DefaultItem = true });

            var levels = new MenuItem("SDR brightness" + (rawValue >= 0 ? "  (" + rawValue + ")" : "")) { Enabled = ready };
            for (int v = BrightnessPopup.Min; v <= BrightnessPopup.Max; v += BrightnessPopup.Step)
            {
                int value = v;
                string text = v + "  ·  " + HdrDisplay.NitsFromSlider(v) + " nits"
                    + (ProfileLibrary.Find(Model, gamma, v) == null ? "   (no profile)" : "");
                levels.MenuItems.Add(new MenuItem(text, delegate { Commit(value, hdrWanted); })
                {
                    RadioCheck = true,
                    Checked = v == rawValue,
                });
            }
            menu.MenuItems.Add(levels);

            bool[] available = GammaAvailability();
            var gammas = new MenuItem("Gamma  (" + gamma + ")");
            gammas.MenuItems.Add(new MenuItem("Show toggle in slider", delegate
            {
                Settings.ShowGammaToggle = !Settings.ShowGammaToggle;
                PushToPopup();
            })
            {
                Checked = Settings.ShowGammaToggle,
            });
            gammas.MenuItems.Add("-");
            for (int i = 0; i < available.Length; i++)
            {
                string choice = ProfileGenerator.Gammas[i];
                gammas.MenuItems.Add(new MenuItem(choice + (available[i] ? "" : "   (run setup)"), delegate
                {
                    Settings.Gamma = choice;
                    Commit(CurrentValue, hdrWanted);
                })
                {
                    RadioCheck = true,
                    Checked = choice == gamma,
                    Enabled = available[i],
                });
            }
            menu.MenuItems.Add(gammas);

            ColorProfile hdrProfile = ProfileLibrary.HdrFor(Model);
            string hdrKey = Settings.GetHotkey(HotkeyAction.ToggleHdr).IsEmpty ? "" : "\t" + Settings.GetHotkey(HotkeyAction.ToggleHdr);
            menu.MenuItems.Add(new MenuItem("HDR content profile" + (hdrProfile != null && hdrProfile.Model == null ? "  (" + hdrProfile.FileName + ")" : "") + hdrKey,
                delegate { Commit(CurrentValue, !hdrActive); })
            {
                Checked = hdrActive,
                Enabled = ready && hdrProfile != null,
            });

            if (displays.Count > 1)
            {
                var choose = new MenuItem("Display");
                foreach (HdrDisplay d in displays)
                {
                    HdrDisplay target = d;
                    string state = d.HdrOn ? "" : d.HdrSupported ? "  (HDR off)" : "  (no HDR)";
                    choose.MenuItems.Add(new MenuItem(d.Name + state, delegate
                    {
                        Settings.Display = target.DevicePath;
                        RefreshState();
                    })
                    {
                        RadioCheck = true,
                        Checked = display != null && d.DevicePath == display.DevicePath,
                    });
                }
                menu.MenuItems.Add(choose);
            }

            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Set up gamma profiles…", delegate { ShowSetup(false); });
            menu.MenuItems.Add("Keyboard shortcuts…" + (hotkeys.Failed.Count > 0 ? "  (" + hotkeys.Failed.Count + " unavailable)" : ""),
                delegate { ShowShortcuts(); });
            menu.MenuItems.Add("Open screenshot folder", delegate { OpenScreenshotFolder(); });
            menu.MenuItems.Add("Display settings…", delegate { Process.Start("ms-settings:display"); });
            menu.MenuItems.Add(new MenuItem("Start with Windows", delegate { Settings.StartWithWindows = !Settings.StartWithWindows; })
            {
                Checked = Settings.StartWithWindows,
            });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Exit", delegate { ExitThread(); });
        }

        void UpdateTooltip()
        {
            string text;
            if (display == null)
                text = "SDR brightness · no display";
            else if (!display.HdrOn)
                text = "SDR brightness · HDR is off";
            else
            {
                text = "SDR brightness " + (rawValue >= 0 ? rawValue + " (" + HdrDisplay.NitsFromSlider(rawValue) + " nits)" : "?");
                ColorProfile profile = ProfileLibrary.Get(activeProfile);
                if (profile != null && profile.Gamma != null)
                    text += " · gamma " + profile.Gamma;
                else if (profile != null && profile.IsHdr)
                    text += " · HDR profile";
                else if (activeProfile != null)
                    text += " · " + activeProfile;
            }
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void UpdateTrayIcon()
        {
            int size = SystemInformation.SmallIconSize.Width;
            Color color = Theme.TaskbarIsLight() ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
            using (Bitmap bitmap = Theme.RenderGlyph(Theme.GlyphBrightness, size, color, 1f))
            {
                IntPtr handle = bitmap.GetHicon();
                Icon old = tray.Icon;
                tray.Icon = Icon.FromHandle(handle);
                if (old != null)
                    old.Dispose();
                if (iconHandle != IntPtr.Zero)
                    Native.DestroyIcon(iconHandle);
                iconHandle = handle;
            }
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                popup.BeginInvoke((Action)UpdateTrayIcon);
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            FlushHotkey();
            hotkeys.Dispose();
            hotkeyCommit.Dispose();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            popup.Dispose();
            osd.Dispose();
            if (iconHandle != IntPtr.Zero)
                Native.DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\SdrBrightnessTray", out first))
            using (var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SdrBrightnessTray.Show"))
            {
                if (!first)
                {
                    // Already running: have that instance open its slider instead.
                    Native.AllowSetForegroundWindow(-1);
                    showRequest.Set();
                    return;
                }

                Native.AllowDarkMenus();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var app = new TrayApp();
                RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(showRequest,
                    delegate { app.ShowFromSignal(); }, null, -1, false);
                Application.Run(app);
                wait.Unregister(null);
            }
        }
    }
}
