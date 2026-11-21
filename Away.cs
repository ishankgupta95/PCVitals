using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>
/// Whether nobody can see the desktop: the Windows session is locked, or the display is off. Pure logic (no
/// Windows calls) so it can be tested; <see cref="AwayWatcher"/> feeds it from the real notifications.
/// Dimmed counts as visible, since the panel can still be read.
/// </summary>
sealed class AwayState
{
    bool _locked, _displayOff;

    public bool IsAway => _locked || _displayOff;
    /// <summary>Raised (on the thread that reports the change) only when <see cref="IsAway"/> flips.</summary>
    public event Action? Changed;

    public void OnLock() => Set(ref _locked, true);
    public void OnUnlock() => Set(ref _locked, false);

    /// <summary>GUID_CONSOLE_DISPLAY_STATE value: 0 off, 1 on, 2 dimmed.</summary>
    public void OnDisplay(int state) => Set(ref _displayOff, state == 0);

    void Set(ref bool field, bool value)
    {
        bool before = IsAway;
        field = value;
        if (IsAway != before) Changed?.Invoke();
    }
}

/// <summary>
/// Listens for session lock/unlock and the display turning off or on, on a message-only window that lives as long
/// as the app, and reports them to an <see cref="AwayState"/>. Create it on the UI thread.
/// </summary>
sealed class AwayWatcher : NativeWindow, IDisposable
{
    const int WM_WTSSESSION_CHANGE = 0x02B1, WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    const int WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    readonly AwayState _state;
    IntPtr _power;
    bool _wts;

    public AwayWatcher(AwayState state)
    {
        _state = state;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        var setting = ConsoleDisplayState;
        _wts = WTSRegisterSessionNotification(Handle, 0 /* NOTIFY_FOR_THIS_SESSION */);
        _power = RegisterPowerSettingNotification(Handle, ref setting, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        // Either call can fail (remote sessions, odd configurations); the panel then simply never pauses.
        if (!_wts || _power == IntPtr.Zero)
            Program.Log("Pause when away: notifications unavailable (session " + _wts + ", display " + (_power != IntPtr.Zero) + ")");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WTSSESSION_CHANGE)
        {
            int what = (int)m.WParam;
            if (what == WTS_SESSION_LOCK) _state.OnLock();
            else if (what == WTS_SESSION_UNLOCK) _state.OnUnlock();
        }
        else if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID, DWORD length, then the value.
            var guid = Marshal.PtrToStructure<Guid>(m.LParam);
            if (guid == ConsoleDisplayState) _state.OnDisplay(Marshal.ReadInt32(m.LParam, 20));
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        if (_power != IntPtr.Zero) UnregisterPowerSettingNotification(_power);
        if (_wts) WTSUnRegisterSessionNotification(Handle);
        _power = IntPtr.Zero;
        _wts = false;
        DestroyHandle();
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    [DllImport("user32.dll")]
    static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
