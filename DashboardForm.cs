using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>Borderless flyout that opens above the tray.</summary>
sealed class DashboardForm : Form
{
    readonly DashboardView _view;

    public DateTime LastAutoHidden { get; private set; }
    public bool Pinned { get; private set; }
    public event Action? RestartAsAdminRequested;
    public event Action? SettingsRequested;

    public DashboardForm(AppSettings settings, bool isAdmin)
    {
        Text = "PC Vitals";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;

        _view = new DashboardView(settings, isAdmin) { Dock = DockStyle.Fill };
        _view.CloseRequested += Hide;
        _view.PinToggled += () =>
        {
            Pinned = !Pinned;
            _view.Pinned = Pinned;
        };
        _view.RestartAsAdminRequested += () => RestartAsAdminRequested?.Invoke();
        _view.SettingsRequested += () => SettingsRequested?.Invoke();
        Controls.Add(_view);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            const int WS_EX_TOOLWINDOW = 0x80; // keep it out of Alt+Tab
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Pinned || !Visible) return;
        Hide();
        LastAutoHidden = DateTime.Now;
    }

    /// <summary>Set by the app on exit; any other user close (e.g. Alt+F4) just hides the flyout.</summary>
    public bool AllowClose { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        base.OnKeyDown(e);
    }

    public void SetSnapshot(Snapshot snap) => _view.SetSnapshot(snap);

    public Func<string, int, float[]>? HistoryProvider
    {
        get => _view.HistoryProvider;
        set => _view.HistoryProvider = value;
    }

    /// <summary>Re-reads theme colours after the palette changed.</summary>
    public void ApplyTheme()
    {
        BackColor = Theme.Bg;
        _view.BackColor = Theme.Bg;
        if (IsHandleCreated) Native.ApplyRoundedDarkFrame(Handle);
        _view.Invalidate();
    }

    /// <summary>Switches the open dashboard to an item's page; false if it was already showing it.</summary>
    public bool SwitchTo(WidgetKind page) => _view.ShowPage(page);

    /// <summary>Shows the dashboard near <paramref name="anchor"/>, on the page for <paramref name="page"/> if given.</summary>
    public void ShowNear(Point anchor, WidgetKind? page = null)
    {
        _view.ShowPage(page);
        Bounds = ComputeBounds(anchor);
        Show();
        // The window may have picked up a new DPI when it landed on another monitor.
        Bounds = ComputeBounds(anchor);
        Activate();
        SetForegroundWindow(Handle);
        _view.Focus();
    }

    Rectangle ComputeBounds(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var wa = Native.UsableArea(screen);
        var full = screen.Bounds;
        float scale = MonitorDpi(anchor) / 96f;
        int margin = (int)(12 * scale);
        int w = Math.Min(wa.Width - margin * 2, (int)(920 * scale));
        int h = Math.Min(wa.Height - margin * 2, (int)(740 * scale));

        int x, y;
        if (wa.Left > full.Left || wa.Right < full.Right) // taskbar on the left or right edge
        {
            x = wa.Left > full.Left ? wa.Left + margin : wa.Right - w - margin;
            y = Math.Clamp(anchor.Y - h / 2, wa.Top + margin, wa.Bottom - h - margin);
        }
        else
        {
            x = Math.Clamp(anchor.X - w / 2, wa.Left + margin, wa.Right - w - margin);
            y = wa.Top > full.Top ? wa.Top + margin : wa.Bottom - h - margin;
        }
        return new Rectangle(x, y, w, h);
    }

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
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
