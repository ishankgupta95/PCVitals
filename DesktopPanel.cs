namespace PCVitals;

/// <summary>
/// A floating panel of readings on the desktop (<see cref="PanelLayout"/>). Like the taskbar strip it is
/// a per-pixel-alpha layered window that never takes focus, and it is redrawn only when what it shows
/// changes. It can sit on top of other windows, among them, or behind them like part of the wallpaper.
/// </summary>
sealed class DesktopPanel : Form
{
    readonly AppSettings _settings;
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    readonly Native.WinEventProc _foregroundProc;
    IntPtr _foregroundHook;
    PanelRenderer _renderer;
    Native.LayeredSurface? _surface;
    Snapshot? _snap;
    string? _signature;
    Point _pos, _origin;
    float _scale = 1;
    bool _desktopRaised;

    bool _mouseDown, _dragging;
    Point _downScreen, _downPos;

    public PanelLayout Layout { get; private set; }
    /// <summary>A tile was clicked: the dashboard page for its device.</summary>
    public event Action<string?>? TileClicked;
    public event Action<Point>? MenuRequested;

    readonly Func<string, int, float[]>? _history;

    /// <param name="history">Longer graph history than snapshots carry (key, sample count).</param>
    public DesktopPanel(AppSettings settings, PanelLayout layout, Func<string, int, float[]>? history)
    {
        _settings = settings;
        _history = history;
        Layout = layout;
        _renderer = NewRenderer(layout);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "PC Vitals panel";
        _clock.Tick += (_, _) => Redraw();
        _foregroundProc = (_, _, _, _, _, _, _) => OnForegroundChanged();
    }

    PanelRenderer NewRenderer(PanelLayout layout) => new(layout, _settings) { HistoryProvider = _history, IntervalMs = _settings.IntervalMs };

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            // Form's own constructor reads this before ours has set the layout.
            if (Layout is not { } layout) return cp;
            if (layout.Locked) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            if (layout.Mode == PanelMode.AlwaysOnTop) cp.ExStyle |= Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _foregroundHook = Native.HookForeground(_foregroundProc);
    }

    /// <summary>The layout or settings changed: rebuild the drawing and re-apply the window options.</summary>
    public void Apply(PanelLayout layout)
    {
        Layout = layout;
        _renderer.Dispose();
        _renderer = NewRenderer(layout);
        if (IsHandleCreated)
        {
            Native.SetExStyle(Handle, Native.WS_EX_TRANSPARENT, layout.Locked);
            ApplyZOrder();
        }
        Redraw(force: true);
    }

    public void SetSnapshot(Snapshot snap)
    {
        _snap = snap;
        Redraw();
    }

    /// <summary>Redraws if anything it shows changed (or always, with <paramref name="force"/>).</summary>
    public void Redraw(bool force = false)
    {
        var screen = FindScreen();
        if (!Layout.Enabled || _snap == null || screen == null || (Layout.HideInFullscreen && Native.FullscreenAppOn(screen)))
        {
            HidePanel();
            return;
        }

        var (size, scale, origin) = Measure(screen);
        var frame = _renderer.Prepare(_snap, DateTime.Now);
        var pos = _dragging ? _pos : PlaceOn(screen, size);
        var sig = $"{frame.Signature}|{size}|{scale}|{pos}|{Layout.Locked}";
        if (!force && sig == _signature && Visible) return;
        _signature = sig;
        _scale = scale;
        _origin = origin;
        _pos = pos;

        if (_surface == null || _surface.Size != size)
        {
            _surface?.Dispose();
            _surface = new Native.LayeredSurface(size);
        }
        using (var g = Graphics.FromImage(_surface.Bitmap))
        {
            // Alpha 1 is invisible but catches the mouse, so the whole panel can be dragged; locked, clicks go through anyway.
            g.Clear(Layout.Locked ? Color.Transparent : Color.FromArgb(1, 0, 0, 0));
            _renderer.Draw(g, frame, size, scale, origin, Layout.FillDisplay);
        }
        _surface.Present(Handle, _pos);
        if (!Visible)
        {
            Show();
            ApplyZOrder();
        }
        _clock.Enabled = _renderer.HasClock;
    }

    void HidePanel()
    {
        _clock.Enabled = false;
        _signature = null;
        if (Visible) Hide();
        // A hidden panel's pixels would only take memory.
        _surface?.Dispose();
        _surface = null;
    }

    /// <summary>The panel's size, the grid's scale and where the grid starts inside it.</summary>
    (Size Size, float Scale, Point Origin) Measure(Screen screen)
    {
        if (Layout.FillDisplay)
        {
            var natural = PanelGrid.PanelSize(Layout, 1);
            var b = screen.Bounds;
            float fit = Math.Min(b.Width / (float)Math.Max(1, natural.Width), b.Height / (float)Math.Max(1, natural.Height));
            var grid = PanelGrid.PanelSize(Layout, fit);
            return (b.Size, fit, new Point((b.Width - grid.Width) / 2, (b.Height - grid.Height) / 2));
        }
        float scale = DashboardForm.MonitorDpi(Center(screen.Bounds)) / 96f * Math.Clamp(Layout.Scale, 50, 300) / 100f;
        return (PanelGrid.PanelSize(Layout, scale), scale, Point.Empty);
    }

    static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>
    /// The monitor the panel belongs to: the saved one by device name (if it is still there), else one of the same
    /// size, else the main display.
    /// </summary>
    Screen? FindScreen()
    {
        var screens = Screen.AllScreens;
        return screens.FirstOrDefault(s => s.DeviceName == Layout.Monitor)
               ?? screens.FirstOrDefault(s => !Layout.MonitorSize.IsEmpty && s.Bounds.Size == Layout.MonitorSize)
               ?? Screen.PrimaryScreen;
    }

    /// <summary>Where the panel goes: its saved spot on the monitor, kept fully on screen; top right by default.</summary>
    Point PlaceOn(Screen screen, Size size)
    {
        if (Layout.FillDisplay) return screen.Bounds.Location;
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        var p = Layout.Position is { } saved
            ? new Point(b.Left + saved.X, b.Top + saved.Y)
            : new Point(wa.Right - size.Width - (int)(24 * _scale), wa.Top + (int)(24 * _scale));
        return new Point(Math.Clamp(p.X, b.Left, Math.Max(b.Left, b.Right - size.Width)),
                         Math.Clamp(p.Y, b.Top, Math.Max(b.Top, b.Bottom - size.Height)));
    }

    // ---- Z-order ------------------------------------------------------------------------------

    void ApplyZOrder()
    {
        if (!IsHandleCreated) return;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        switch (Layout.Mode)
        {
            case PanelMode.AlwaysOnTop:
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
                break;
            case PanelMode.Desktop:
                // "Show desktop" puts the desktop in front of every normal window, the panel too; while it
                // is in front the panel rises above it, and goes back to the bottom when anything else is used.
                _desktopRaised = Native.DesktopInFront();
                if (_desktopRaised) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
                else
                {
                    Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                    Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, flags);
                }
                break;
            default:
                Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                break;
        }
    }

    void OnForegroundChanged()
    {
        if (IsDisposed || !Layout.Enabled) return;
        // A game going fullscreen (or leaving it) shows or hides the panel.
        if (Layout.HideInFullscreen) Redraw();
        if (Visible && Layout.Mode == PanelMode.Desktop && Native.DesktopInFront() != _desktopRaised) ApplyZOrder();
    }

    /// <summary>Cheap periodic check for monitor and DPI changes the panel doesn't hear about otherwise.</summary>
    public void CheckPlacement()
    {
        if (!Layout.Enabled || _dragging) return;
        if (FindScreen() is { } screen && Visible && PlaceOn(screen, Measure(screen).Size) != _pos) Redraw(force: true);
        else if (!Visible && _snap != null) Redraw();
    }

    // ---- Mouse ----------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _mouseDown = true;
            _dragging = false;
            _downScreen = Cursor.Position;
            _downPos = _pos;
        }
        else if (e.Button == MouseButtons.Right)
        {
            MenuRequested?.Invoke(Cursor.Position);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_mouseDown || Layout.FillDisplay) return;
        var c = Cursor.Position;
        int dx = c.X - _downScreen.X, dy = c.Y - _downScreen.Y;
        float dpi = DeviceDpi / 96f;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 4 * dpi)
        {
            _dragging = true;
            Cursor = Cursors.SizeAll;
        }
        if (!_dragging) return;
        var size = _surface?.Size ?? Size;
        var p = new Point(_downPos.X + dx, _downPos.Y + dy);
        // Snap to the edges of the work area when close.
        var wa = Screen.FromPoint(c).WorkingArea;
        int snap = (int)(12 * dpi);
        if (Math.Abs(p.X - wa.Left) < snap) p.X = wa.Left;
        if (Math.Abs(p.Y - wa.Top) < snap) p.Y = wa.Top;
        if (Math.Abs(wa.Right - (p.X + size.Width)) < snap) p.X = wa.Right - size.Width;
        if (Math.Abs(wa.Bottom - (p.Y + size.Height)) < snap) p.Y = wa.Bottom - size.Height;
        _pos = p;
        Native.SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x0004 /* NOZORDER */);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_mouseDown) return;
        _mouseDown = false;
        Cursor = Cursors.Default;
        if (_dragging)
        {
            _dragging = false;
            SavePosition();
            Redraw(force: true); // the monitor (and its scaling) may have changed
            return;
        }
        var p = new Point(e.X - _origin.X, e.Y - _origin.Y);
        int hit = PanelGrid.HitTest(Layout, p, _scale);
        if (hit >= 0 && _snap != null && Layout.Tiles[hit].Kind == PanelTileKind.Value)
            TileClicked?.Invoke(PanelSources.Read(Layout.Tiles[hit], _snap, _settings)?.Page);
    }

    void SavePosition()
    {
        var size = _surface?.Size ?? Size;
        var screen = Screen.FromPoint(Center(new Rectangle(_pos, size)));
        Layout.Monitor = screen.DeviceName;
        Layout.MonitorSize = screen.Bounds.Size;
        Layout.Position = new Point(_pos.X - screen.Bounds.Left, _pos.Y - screen.Bounds.Top);
        _settings.Save();
    }

    /// <summary>Back to the default spot (top right of its monitor).</summary>
    public void ResetPosition()
    {
        Layout.Position = null;
        Redraw(force: true);
    }

    // ---- Lifetime -----------------------------------------------------------------------------

    /// <summary>Set by the app on exit; stray WM_CLOSE messages are otherwise ignored.</summary>
    public bool AllowClose { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
            _clock.Dispose();
            _renderer.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
