using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace SdrBrightness
{
    enum HotkeyAction { BrightnessDown = 1, BrightnessUp = 2, ToggleHdr = 3 }

    // A global shortcut: RegisterHotKey's MOD_* flags plus a virtual-key code.
    struct Hotkey
    {
        public const int Alt = 1, Control = 2, Shift = 4, Win = 8;

        public readonly int Modifiers;
        public readonly int Key;

        public Hotkey(int modifiers, Keys key)
        {
            Modifiers = modifiers;
            Key = (int)key;
        }

        public bool IsEmpty
        {
            get { return Key == 0; }
        }

        // Stored in the registry as (modifiers << 16) | key; 0 means no shortcut.
        public int Packed
        {
            get { return Modifiers << 16 | Key; }
        }

        public static Hotkey FromPacked(int packed)
        {
            return new Hotkey((packed >> 16) & 0xF, (Keys)(packed & 0xFF));
        }

        public static Hotkey Default(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.BrightnessDown:
                    return new Hotkey(Alt | Shift, Keys.F1);
                case HotkeyAction.BrightnessUp:
                    return new Hotkey(Alt | Shift, Keys.F2);
                default:
                    return new Hotkey(Alt | Shift, Keys.F3);
            }
        }

        public override bool Equals(object obj)
        {
            return obj is Hotkey && ((Hotkey)obj).Packed == Packed;
        }

        public override int GetHashCode()
        {
            return Packed;
        }

        public override string ToString()
        {
            return IsEmpty ? "None" : ModifierText(Modifiers) + KeyName((Keys)Key);
        }

        public static string ModifierText(int modifiers)
        {
            var sb = new StringBuilder();
            if ((modifiers & Control) != 0)
                sb.Append("Ctrl + ");
            if ((modifiers & Alt) != 0)
                sb.Append("Alt + ");
            if ((modifiers & Shift) != 0)
                sb.Append("Shift + ");
            if ((modifiers & Win) != 0)
                sb.Append("Win + ");
            return sb.ToString();
        }

        static string KeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9)
                return ((char)('0' + (key - Keys.D0))).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
                return "Num " + (key - Keys.NumPad0);
            switch (key)
            {
                case Keys.Prior: return "Page Up";
                case Keys.Next: return "Page Down";
                case Keys.Return: return "Enter";
                case Keys.Back: return "Backspace";
                case Keys.Capital: return "Caps Lock";
                case Keys.Scroll: return "Scroll Lock";
                case Keys.Snapshot: return "Print Screen";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Multiply: return "Num *";
                case Keys.Divide: return "Num /";
                case Keys.Decimal: return "Num .";
                case Keys.VolumeUp: return "Volume Up";
                case Keys.VolumeDown: return "Volume Down";
                case Keys.VolumeMute: return "Mute";
                case Keys.MediaPlayPause: return "Play/Pause";
                case Keys.MediaNextTrack: return "Next Track";
                case Keys.MediaPreviousTrack: return "Previous Track";
            }
            // Punctuation keys: ask the keyboard layout which character they type.
            uint ch = Native.MapVirtualKey((uint)key, Native.MAPVK_VK_TO_CHAR) & 0xFFFF;
            if (ch > 32 && key >= Keys.OemSemicolon)
                return ((char)ch).ToString();
            return key.ToString();
        }
    }

    // Registers the app's global shortcuts on a message-only window and reports presses.
    sealed class HotkeyManager : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const uint MOD_NOREPEAT = 0x4000;

        public static readonly HotkeyAction[] Actions = { HotkeyAction.BrightnessDown, HotkeyAction.BrightnessUp, HotkeyAction.ToggleHdr };

        readonly List<HotkeyAction> registered = new List<HotkeyAction>();
        readonly List<HotkeyAction> failed = new List<HotkeyAction>();
        bool paused;

        public event Action<HotkeyAction> Pressed;

        public HotkeyManager()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        // Shortcuts Windows refused at the last Apply, usually because another app owns them.
        public IList<HotkeyAction> Failed
        {
            get { return failed.AsReadOnly(); }
        }

        // (Re)registers the shortcuts saved in Settings.
        public void Apply()
        {
            UnregisterAll();
            failed.Clear();
            if (paused)
                return;
            foreach (HotkeyAction action in Actions)
            {
                Hotkey hotkey = Settings.GetHotkey(action);
                if (hotkey.IsEmpty)
                    continue;
                // Holding a brightness shortcut repeats it; the HDR toggle fires once per press.
                uint modifiers = (uint)hotkey.Modifiers | (action == HotkeyAction.ToggleHdr ? MOD_NOREPEAT : 0);
                if (Native.RegisterHotKey(Handle, (int)action, modifiers, (uint)hotkey.Key))
                    registered.Add(action);
                else
                    failed.Add(action);
            }
        }

        // While paused (the shortcuts window is capturing keys) nothing is registered.
        public void Pause(bool pause)
        {
            paused = pause;
            Apply();
        }

        void UnregisterAll()
        {
            foreach (HotkeyAction action in registered)
                Native.UnregisterHotKey(Handle, (int)action);
            registered.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                var handler = Pressed;
                if (handler != null)
                    handler((HotkeyAction)m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }
}
