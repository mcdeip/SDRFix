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
    // What SDRFix knows about one display: where Windows has its slider and profile, and changes on their way.
    sealed class DisplayState
    {
        public HdrDisplay Display;
        public int RawValue = -1;     // Windows' slider position; can be off-step if moved in Settings
        public string ActiveProfile;  // file name of the default HDR profile
        public string LastError;
        public bool HdrWanted;        // the HDR content profile was last asked for, even if Windows hasn't caught up yet
        public int Seq;
        public int HotkeyValue = -1;  // where shortcut presses have moved the slider before they commit

        public string Model
        {
            get { return Display.ModelId; }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenu menu = new ContextMenu();
        readonly BrightnessPopup popup = new BrightnessPopup();
        readonly Osd osd = new Osd();
        readonly HotkeyManager hotkeys = new HotkeyManager();
        readonly System.Windows.Forms.Timer hotkeyCommit = new System.Windows.Forms.Timer { Interval = 400 };
        readonly System.Windows.Forms.Timer displayChange = new System.Windows.Forms.Timer { Interval = 500 };
        readonly Applier applier;

        List<DisplayState> states = new List<DisplayState>(); // every active display, Windows' main display first
        List<DisplayState> shown = new List<DisplayState>();  // the popup's entries, in order
        IntPtr iconHandle;
        SetupWindow setup;
        ShortcutsWindow shortcuts;
        RegionPicker picker;

        public TrayApp()
        {
            IntPtr unused = popup.Handle; // gives the applier a thread to report back on
            applier = new Applier(popup);
            applier.Applied += OnApplied;

            popup.Previewed += (i, value) =>
            {
                DisplayState s = Shown(i);
                if (s != null && s.Display.HdrOn)
                    applier.Submit(new ApplyRequest { Display = s.Display, Value = value });
            };
            popup.Committed += (i, value) => Commit(Shown(i), value, popup.HdrOnOf(i));
            popup.StatusClicked += i => Commit(Shown(i), popup.ValueOf(i), popup.HdrOnOf(i));
            popup.HdrToggled += (i, on) => Commit(Shown(i), popup.ValueOf(i), on);
            popup.GammaChanged += (i, gamma) =>
            {
                DisplayState s = Shown(i);
                if (s == null)
                    return;
                Settings.SetGamma(s.Model, gamma);
                Commit(s, popup.ValueOf(i), popup.HdrOnOf(i));
            };
            popup.SetupClicked += i => ShowSetup(Shown(i), false);
            popup.LinkToggled += on =>
            {
                Settings.LinkDisplays = on;
                PushToPopup();
            };
            hotkeys.Pressed += OnHotkey;
            hotkeyCommit.Tick += delegate { FlushHotkey(); };
            // Displays come and go, or become the main one, in several steps; look once they've settled.
            displayChange.Tick += delegate
            {
                displayChange.Stop();
                RefreshState();
            };

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
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            RefreshState();
            UpdateTrayIcon();
            tray.Visible = true;
            hotkeys.Apply();

            if (!Settings.SetupOffered)
                popup.BeginInvoke((Action)delegate { ShowSetup(Primary, true); });
        }

        // ---- Displays ----

        DisplayState Find(string devicePath)
        {
            return states.FirstOrDefault(s => s.Display.DevicePath == devicePath);
        }

        DisplayState Shown(int index)
        {
            return index >= 0 && index < shown.Count ? shown[index] : null;
        }

        List<DisplayState> HdrStates
        {
            get { return states.Where(s => s.Display.HdrOn).ToList(); }
        }

        // The display to act on when there's no better choice: the main one if HDR is on there.
        DisplayState Primary
        {
            get
            {
                return states.FirstOrDefault(s => s.Display.HdrOn)
                    ?? states.FirstOrDefault(s => s.Display.HdrSupported)
                    ?? states.FirstOrDefault();
            }
        }

        // The display with the active window (the pointer's, when no window has focus).
        DisplayState Focused()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            string name = foreground != IntPtr.Zero ? Native.DisplayNameOf(foreground) : Native.DisplayNameAt(Cursor.Position);
            return states.FirstOrDefault(s => string.Equals(s.Display.GdiName, name, StringComparison.OrdinalIgnoreCase)) ?? Primary;
        }

        int CurrentValue(DisplayState s)
        {
            int i = shown.IndexOf(s);
            return BrightnessPopup.Snap(s.RawValue >= 0 ? s.RawValue : i >= 0 ? popup.ValueOf(i) : 0);
        }

        // The set a display uses: its own choice, or setup's other gamma when only that one exists for it.
        static string GammaFor(DisplayState s)
        {
            string model = s != null ? s.Model : null;
            string gamma = Settings.GetGamma(model);
            if (s != null && !ProfileLibrary.HasGamma(model, gamma))
                foreach (string other in ProfileGenerator.Gammas)
                    if (ProfileLibrary.HasGamma(model, other))
                        return other;
            return gamma;
        }

        static bool[] GammaAvailability(DisplayState s)
        {
            var available = new bool[ProfileGenerator.Gammas.Length];
            for (int i = 0; i < available.Length; i++)
                available[i] = s != null && ProfileLibrary.HasGamma(s.Model, ProfileGenerator.Gammas[i]);
            return available;
        }

        // Re-reads displays, brightness and the active profiles from Windows.
        void RefreshState()
        {
            var next = new List<DisplayState>();
            foreach (HdrDisplay d in HdrDisplay.GetAll().OrderBy(d => d.IsMain ? 0 : 1))
            {
                DisplayState s = Find(d.DevicePath) ?? new DisplayState();
                s.Display = d;
                s.RawValue = -1;
                s.LastError = null;
                int? level = d.GetSdrWhiteLevel();
                if (level.HasValue)
                    s.RawValue = Math.Max(0, Math.Min(100, HdrDisplay.SliderFromLevel(level.Value)));
                s.ActiveProfile = d.GetDefaultProfile();
                s.HdrWanted = ProfileLibrary.IsHdr(s.ActiveProfile);
                next.Add(s);
            }
            states = next;
            if (!popup.Interacting)
                PushToPopup();
            UpdateTooltip();
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            popup.BeginInvoke((Action)delegate
            {
                displayChange.Stop();
                displayChange.Start();
            });
        }

        // ---- Popup ----

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

        // One entry per display with HDR on; with none, the display that would be used, so it can say why.
        void PushToPopup()
        {
            shown = HdrStates;
            if (shown.Count == 0 && Primary != null)
                shown.Add(Primary);
            var list = new List<PopupDisplay>();
            foreach (DisplayState s in shown)
                list.Add(Describe(s));
            if (list.Count == 0)
                list.Add(new PopupDisplay { Enabled = false, Status = "No active display found", StatusKind = StatusKind.Error });
            popup.SetDisplays(list, states.Count(s => s.Display.HdrSupported) > 1, Settings.ShowGammaToggle, Settings.LinkDisplays);
        }

        PopupDisplay Describe(DisplayState s)
        {
            ColorProfile hdrProfile = ProfileLibrary.HdrFor(s.Model);
            bool ready = s.Display.HdrOn;
            bool hdrActive = ProfileLibrary.IsHdr(s.ActiveProfile);
            int value = CurrentValue(s);
            var d = new PopupDisplay
            {
                Name = s.Display.Name,
                Value = value,
                Enabled = ready,
                HdrOn = hdrActive,
                HdrAvailable = ready && hdrProfile != null,
                HdrHint = hdrProfile == null ? "No HDR profile yet · setup creates one"
                    : hdrProfile.Model != null ? "No gamma curve · 0–" + Math.Round(hdrProfile.PeakNits) + " nits"
                    : "Applies " + hdrProfile.FileName + " · no gamma curve",
                Gamma = GammaFor(s),
                GammaAvailable = GammaAvailability(s),
            };

            if (!ready)
                SetStatus(d, "HDR is off on " + s.Display.Name + " · turn it on in Display settings", StatusKind.Info, false);
            else if (s.LastError != null)
                SetStatus(d, s.LastError + " · click to retry", StatusKind.Error, true);
            else if (hdrActive)
                SetStatus(d, "HDR profile active · the slider only changes brightness", StatusKind.Info, false);
            else if (s.RawValue >= 0 && s.RawValue % BrightnessPopup.Step != 0)
                SetStatus(d, "Windows is set to " + s.RawValue + " · click to snap to " + value, StatusKind.Warning, true);
            else
            {
                ColorProfile expected = ProfileLibrary.Find(s.Model, d.Gamma, value);
                if (expected == null)
                    SetStatus(d, "No profile for " + value + " · brightness only", StatusKind.Warning, false);
                else if (string.Equals(s.ActiveProfile, expected.FileName, StringComparison.OrdinalIgnoreCase))
                    SetStatus(d, expected.Label, StatusKind.Success, false);
                else
                    SetStatus(d, "Active profile: " + ProfileLibrary.Describe(s.ActiveProfile) + " · click to fix", StatusKind.Warning, true);
            }
            return d;
        }

        static void SetStatus(PopupDisplay d, string text, StatusKind kind, bool clickable)
        {
            d.Status = text;
            d.StatusKind = kind;
            d.StatusClickable = clickable;
        }

        void Commit(DisplayState s, int value, bool hdr, bool reload = false)
        {
            if (s == null || !s.Display.HdrOn)
                return;
            s.HdrWanted = hdr;
            popup.SetStatus(shown.IndexOf(s), "Applying…", StatusKind.Busy, false);
            applier.Submit(new ApplyRequest
            {
                Display = s.Display,
                Value = value,
                Final = true,
                Hdr = hdr,
                Gamma = GammaFor(s),
                Reload = reload,
                Seq = ++s.Seq,
            });
        }

        void OnApplied(ApplyResult result)
        {
            DisplayState s = Find(result.DevicePath);
            if (s == null)
                return; // the display has gone since
            s.RawValue = result.Value;
            s.ActiveProfile = result.ActiveProfile;
            s.LastError = result.Error;
            if (result.Seq == s.Seq)
                s.HdrWanted = ProfileLibrary.IsHdr(s.ActiveProfile);
            UpdateTooltip();
            int i = shown.IndexOf(s);
            if (!popup.Interacting && (i < 0 || popup.ValueOf(i) == result.Value))
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
            if (HdrStates.Count == 0)
                RefreshState();
            List<DisplayState> hdr = HdrStates;
            if (hdr.Count == 0)
            {
                DisplayState p = Primary;
                osd.ShowText(Theme.GlyphWarning, p == null ? "No display found" : "HDR is off on " + p.Display.Name);
                return;
            }
            bool several = hdr.Count > 1;
            DisplayState focused = Focused();
            if (several && !focused.Display.HdrOn && (action == HotkeyAction.ToggleHdr || !Settings.LinkDisplays))
            {
                osd.ShowText(Theme.GlyphWarning, "HDR is off on " + focused.Display.Name, focused.Display.GdiName);
                return;
            }
            if (!several)
                focused = hdr[0];

            if (action == HotkeyAction.ToggleHdr)
            {
                // Always the active window's display: it's about what's showing there.
                FlushHotkey();
                string on = several ? " · " + focused.Display.Name : "";
                if (ProfileLibrary.HdrFor(focused.Model) == null)
                {
                    osd.ShowText(Theme.GlyphWarning, "No HDR profile" + on + " · run setup from the tray menu", focused.Display.GdiName);
                    return;
                }
                bool hdrOn = !focused.HdrWanted;
                Commit(focused, CurrentValue(focused), hdrOn);
                osd.ShowText(Theme.GlyphBrightness,
                    (hdrOn ? "HDR content profile on" : "HDR content profile off · gamma " + GammaFor(focused)) + on, focused.Display.GdiName);
                if (popup.Visible && !popup.Interacting)
                    PushToPopup();
                return;
            }

            // Linked displays all move; otherwise just the active window's.
            List<DisplayState> targets = several && Settings.LinkDisplays ? hdr : new List<DisplayState> { focused };
            if (!targets.Contains(focused))
                focused = targets[0];
            int step = Settings.HotkeyStep * (action == HotkeyAction.BrightnessUp ? 1 : -1);
            foreach (DisplayState s in targets)
            {
                s.HotkeyValue = BrightnessPopup.Snap((s.HotkeyValue >= 0 ? s.HotkeyValue : CurrentValue(s)) + step);
                applier.Submit(new ApplyRequest { Display = s.Display, Value = s.HotkeyValue }); // brightness now, profile once presses stop
            }
            hotkeyCommit.Stop();
            hotkeyCommit.Start();
            if (popup.Visible)
            {
                if (!popup.Interacting)
                {
                    foreach (DisplayState s in targets)
                        s.RawValue = s.HotkeyValue;
                    PushToPopup();
                }
            }
            else
                osd.ShowLevel(focused.HotkeyValue, focused.Display.GdiName);
        }

        void FlushHotkey()
        {
            hotkeyCommit.Stop();
            foreach (DisplayState s in states)
            {
                if (s.HotkeyValue < 0)
                    continue;
                int value = s.HotkeyValue;
                s.HotkeyValue = -1;
                Commit(s, value, s.HdrWanted);
            }
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

        void ShowSetup(DisplayState target, bool firstRun)
        {
            Settings.SetupOffered = true;
            if (setup != null)
            {
                setup.Activate();
                return;
            }
            popup.HidePopup();
            string path = target != null ? target.Display.DevicePath : null;
            RefreshState();
            DisplayState s = (path != null ? Find(path) : null) ?? Primary;
            path = s != null ? s.Display.DevicePath : null;
            setup = new SetupWindow(s != null ? s.Display : null, GammaFor(s), SuggestedPeak(s), firstRun);
            setup.ProfilesCreated += gamma => OnProfilesCreated(path, gamma);
            setup.FormClosed += delegate { setup = null; };
            setup.Show();
            setup.Activate();
        }

        // The peak the display's current profiles already declare, so a re-run starts from it.
        static int SuggestedPeak(DisplayState s)
        {
            if (s != null)
                foreach (ColorProfile profile in new[] { ProfileLibrary.Get(s.ActiveProfile), ProfileLibrary.HdrFor(s.Model) })
                    if (profile != null && profile.PeakNits >= 100 && profile.PeakNits <= 10000)
                        return (int)Math.Round(profile.PeakNits);
            return 1000;
        }

        void OnProfilesCreated(string path, string gamma)
        {
            DisplayState s = Find(path);
            Settings.SetGamma(s != null ? s.Model : null, gamma);
            RefreshState();
            s = Find(path);
            if (s != null)
                Commit(s, CurrentValue(s), s.HdrWanted, true);
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

        void BuildMenu()
        {
            menu.MenuItems.Clear();
            menu.MenuItems.Add(new MenuItem("Open slider", delegate { ShowPopup(Cursor.Position); }) { DefaultItem = true });

            // One HDR display: its items right here. Several: a submenu each, plus linking.
            List<DisplayState> supported = states.Where(s => s.Display.HdrSupported).ToList();
            if (supported.Count <= 1)
                menu.MenuItems.AddRange(DisplayItems(Primary));
            else
            {
                foreach (DisplayState s in supported)
                {
                    var sub = new MenuItem(s.Display.Name + (s.Display.HdrOn ? "" : "  (HDR off)"));
                    sub.MenuItems.AddRange(DisplayItems(s));
                    sub.MenuItems.Add("-");
                    sub.MenuItems.Add("Set up gamma profiles…", delegate { ShowSetup(s, false); });
                    menu.MenuItems.Add(sub);
                }
                menu.MenuItems.Add(new MenuItem("Link displays", delegate
                {
                    Settings.LinkDisplays = !Settings.LinkDisplays;
                    PushToPopup();
                })
                {
                    Checked = Settings.LinkDisplays,
                });
            }

            menu.MenuItems.Add("-");
            if (supported.Count <= 1)
                menu.MenuItems.Add("Set up gamma profiles…", delegate { ShowSetup(Primary, false); });
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

        // SDR brightness steps, the gamma choice and the HDR content profile for one display.
        MenuItem[] DisplayItems(DisplayState s)
        {
            bool ready = s != null && s.Display.HdrOn;
            bool hdrActive = s != null && ProfileLibrary.IsHdr(s.ActiveProfile);
            string model = s != null ? s.Model : null;
            string gamma = GammaFor(s);

            var levels = new MenuItem("SDR brightness" + (s != null && s.RawValue >= 0 ? "  (" + s.RawValue + ")" : "")) { Enabled = ready };
            for (int v = BrightnessPopup.Min; v <= BrightnessPopup.Max; v += BrightnessPopup.Step)
            {
                int value = v;
                string text = v + "  ·  " + HdrDisplay.NitsFromSlider(v) + " nits"
                    + (ProfileLibrary.Find(model, gamma, v) == null ? "   (no profile)" : "");
                levels.MenuItems.Add(new MenuItem(text, delegate { Commit(s, value, s.HdrWanted); })
                {
                    RadioCheck = true,
                    Checked = s != null && v == s.RawValue,
                });
            }

            bool[] available = GammaAvailability(s);
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
                    Settings.SetGamma(model, choice);
                    Commit(s, CurrentValue(s), s.HdrWanted);
                })
                {
                    RadioCheck = true,
                    Checked = choice == gamma,
                    Enabled = available[i],
                });
            }

            ColorProfile hdrProfile = ProfileLibrary.HdrFor(model);
            string hdrKey = Settings.GetHotkey(HotkeyAction.ToggleHdr).IsEmpty ? "" : "\t" + Settings.GetHotkey(HotkeyAction.ToggleHdr);
            var hdr = new MenuItem("HDR content profile" + (hdrProfile != null && hdrProfile.Model == null ? "  (" + hdrProfile.FileName + ")" : "") + hdrKey,
                delegate { Commit(s, CurrentValue(s), !hdrActive); })
            {
                Checked = hdrActive,
                Enabled = ready && hdrProfile != null,
            };
            return new[] { levels, gammas, hdr };
        }

        void UpdateTooltip()
        {
            List<DisplayState> hdr = HdrStates;
            string text;
            if (states.Count == 0)
                text = "SDR brightness · no display";
            else if (hdr.Count == 0)
                text = "SDR brightness · HDR is off";
            else if (hdr.Count > 1)
                text = "SDR brightness · " + string.Join(" · ", hdr.Select(s => s.Display.Name + " " + (s.RawValue >= 0 ? s.RawValue.ToString() : "?")).ToArray());
            else
            {
                DisplayState s = hdr[0];
                text = "SDR brightness " + (s.RawValue >= 0 ? s.RawValue + " (" + HdrDisplay.NitsFromSlider(s.RawValue) + " nits)" : "?");
                ColorProfile profile = ProfileLibrary.Get(s.ActiveProfile);
                if (profile != null && profile.Gamma != null)
                    text += " · gamma " + profile.Gamma;
                else if (profile != null && profile.IsHdr)
                    text += " · HDR profile";
                else if (s.ActiveProfile != null)
                    text += " · " + s.ActiveProfile;
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
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            FlushHotkey();
            hotkeys.Dispose();
            hotkeyCommit.Dispose();
            displayChange.Dispose();
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
