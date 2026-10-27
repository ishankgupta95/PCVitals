using System.Globalization;
using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>
/// The dashboard window: a normal, resizable app window (title bar, minimise / maximise, taskbar
/// button, Alt+Tab) that remembers its size and position. Closing it hides it; PC Vitals keeps
/// running in the tray.
/// </summary>
sealed class DashboardForm : Form
{
    readonly DashboardView _view;
    readonly AppSettings _settings;
    bool _placed;

    public event Action? RestartAsAdminRequested;
    public event Action? SettingsRequested;
    /// <summary>The user changed a sensor (favourite, rename, hide, pin) from the dashboard.</summary>
    public event Action? SensorsChanged;
    public event Action<string>? ResetMinMaxRequested;
    public event Action<SensorKey>? AddAlertRequested;

    public DashboardForm(AppSettings settings, bool isAdmin)
    {
        _settings = settings;
        Text = "PC Vitals";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // the view scales itself to the window's DPI
        BackColor = Theme.Bg;
        Icon = TrayIconRenderer.AppIcon;

        _view = new DashboardView(settings, isAdmin) { Dock = DockStyle.Fill };
        _view.RestartAsAdminRequested += () => RestartAsAdminRequested?.Invoke();
        _view.SettingsRequested += () => SettingsRequested?.Invoke();
        _view.SensorsChanged += () => SensorsChanged?.Invoke();
        _view.ResetMinMaxRequested += id => ResetMinMaxRequested?.Invoke(id);
        _view.AddAlertRequested += key => AddAlertRequested?.Invoke(key);
        Controls.Add(_view);
    }

    /// <summary>True while the dashboard is on screen (shown and not minimised).</summary>
    public bool OnScreen => Visible && WindowState != FormWindowState.Minimized;

    /// <summary>True when the dashboard is the window the user is working in.</summary>
    public bool IsForeground => OnScreen && GetForegroundWindow() == Handle;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        MinimumSize = MinimumFor(e.DeviceDpiNew);
    }

    /// <summary>Set by the app on exit; closing the window otherwise just hides it (the app stays in the tray).</summary>
    public bool AllowClose { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SavePlacement();
    }

    public void SetSnapshot(Snapshot snap) => _view.SetSnapshot(snap);

    public Func<string, int, float[]>? HistoryProvider
    {
        get => _view.HistoryProvider;
        set => _view.HistoryProvider = value;
    }

    public Func<string, int, float[]>? MinuteHistoryProvider
    {
        get => _view.MinuteHistoryProvider;
        set => _view.MinuteHistoryProvider = value;
    }

    public Func<int, HistoryRange?>? LongHistoryProvider
    {
        get => _view.LongHistoryProvider;
        set => _view.LongHistoryProvider = value;
    }

    /// <summary>Repaints with data that arrived in the background (e.g. saved history).</summary>
    public void RefreshView() => _view.Invalidate();

    /// <summary>Re-reads theme colours after the palette changed.</summary>
    public void ApplyTheme()
    {
        BackColor = Theme.Bg;
        _view.BackColor = Theme.Bg;
        if (IsHandleCreated) Native.ApplyRoundedDarkFrame(Handle);
        _view.Invalidate();
    }

    /// <summary>Switches the open dashboard to an item's page; false if it was already showing it.</summary>
    public bool SwitchTo(WidgetKind page, string? pageKey = null) => _view.ShowPage(page, pageKey);

    /// <summary>
    /// Shows the dashboard (restoring it if minimised) and brings it to the front, on the page for
    /// <paramref name="page"/> if given. The first time, it opens where it was last closed, or centred
    /// on the display containing <paramref name="anchor"/>.
    /// </summary>
    public void ShowAndActivate(Point anchor, WidgetKind? page = null, string? pageKey = null)
    {
        _view.ShowPage(page, pageKey);
        if (!_placed)
        {
            Place(anchor);
            _placed = true;
        }
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = _settings.DashboardMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        SetForegroundWindow(Handle);
        _view.Focus();
    }

    void Place(Point anchor)
    {
        MinimumSize = MinimumFor((int)MonitorDpi(anchor));
        if (ParseBounds(_settings.DashboardBounds) is { } saved && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            Bounds = saved;
        }
        else
        {
            // First run: a comfortable size, centred on the display the user opened it from.
            var wa = Screen.FromPoint(anchor).WorkingArea;
            float scale = MonitorDpi(anchor) / 96f;
            int w = Math.Min(wa.Width - 40, (int)(1100 * scale)), h = Math.Min(wa.Height - 40, (int)(800 * scale));
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        }
        if (_settings.DashboardMaximized) WindowState = FormWindowState.Maximized;
    }

    void SavePlacement()
    {
        if (!_placed || WindowState == FormWindowState.Minimized && !Visible) return;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var text = string.Join(",", new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        bool maximized = WindowState == FormWindowState.Maximized;
        if (text == _settings.DashboardBounds && maximized == _settings.DashboardMaximized) return;
        _settings.DashboardBounds = text;
        _settings.DashboardMaximized = maximized;
        _settings.Save();
    }

    static Rectangle? ParseBounds(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 4 }) return null;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return null;
        return v[2] > 100 && v[3] > 100 ? new Rectangle(v[0], v[1], v[2], v[3]) : null;
    }

    static Size MinimumFor(int dpi) => new((int)(720 * dpi / 96f), (int)(480 * dpi / 96f));

    static uint MonitorDpi(Point p)
    {
        try
        {
            var mon = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0) return dx;
        }
        catch
        {
        }
        return 96;
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
