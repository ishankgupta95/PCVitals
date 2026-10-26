using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>A system-wide keyboard shortcut (RegisterHotKey on a message-only window).</summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    public static readonly string[] Choices = { "Off", "Ctrl+Shift+Alt+V", "Ctrl+Alt+V", "Win+Alt+V", "Ctrl+Shift+F12" };

    const int WM_HOTKEY = 0x0312, Id = 0x5643;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    bool _registered;

    public event Action? Pressed;

    public Hotkeys() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

    /// <summary>Registers <paramref name="combo"/> (one of <see cref="Choices"/>); false if another app owns it.</summary>
    public bool Set(string combo)
    {
        Unregister();
        if (combo == "Off" || string.IsNullOrEmpty(combo)) return true;
        uint mods = MOD_NOREPEAT;
        Keys key = Keys.None;
        foreach (var part in combo.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": mods |= MOD_CONTROL; break;
                case "Shift": mods |= MOD_SHIFT; break;
                case "Alt": mods |= MOD_ALT; break;
                case "Win": mods |= MOD_WIN; break;
                default: Enum.TryParse(part, out key); break;
            }
        }
        if (key == Keys.None) return false;
        _registered = RegisterHotKey(Handle, Id, mods, (uint)key);
        return _registered;
    }

    void Unregister()
    {
        if (_registered) UnregisterHotKey(Handle, Id);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
