using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PCVitals;

/// <summary>
/// Strip of labelled vitals drawn directly on the taskbar, next to the tray.
/// It is a per-pixel-alpha layered window owned by the taskbar, so it stays above it in z-order
/// without stealing focus, and is redrawn only when its content actually changes.
/// </summary>
sealed class TaskbarWidget : Form
{
    readonly AppSettings _settings;
    readonly ContextMenuStrip _menu;
    readonly HoverCard _card = new();
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 350 };

    Snapshot? _snap;
    List<ItemView> _views = new();
    List<Rectangle> _rects = new();
    TaskbarInfo? _taskbar;
    IntPtr _owner;
    Point _pos;
    int _hover = -1;
    bool _cardWanted;
    string? _signature;

    bool _mouseDown, _dragging;
    Point _downScreen;
    int _downOffset;

    public bool LightTaskbar { get; set; }
    public event Action<ItemView>? ItemClicked;

    public TaskbarWidget(AppSettings settings, ContextMenuStrip menu)
    {
        _settings = settings;
        _menu = menu;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "PC Vitals taskbar";
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            _cardWanted = true;
            UpdateCard();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    public void SetSnapshot(Snapshot snap)
    {
        _snap = snap;
        Redraw();
    }

    /// <summary>Re-evaluates placement and content; cheap when nothing changed.</summary>
    public void Redraw(bool force = false)
    {
        if (!_settings.WidgetEnabled || _snap == null)
        {
            HideAll();
            return;
        }
        var tb = Native.FindTaskbar();
        if (tb == null || !tb.Horizontal || Native.FullscreenAppActive(tb.Handle))
        {
            HideAll();
            return;
        }
        bool floating = IsAutoHidden(tb);
        if (floating && !_settings.FloatWhenTaskbarHidden)
        {
            HideAll();
            return;
        }

        if (_owner != tb.Handle)
        {
            // Owned windows always sit above their owner, so the taskbar can't cover us when clicked.
            Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, tb.Handle);
            _owner = tb.Handle;
        }
        _taskbar = tb;

        var views = _settings.WidgetItems.Where(i => i.Enabled)
            .Select(i => Vitals.Build(i.Kind, i.Label, _snap))
            .Where(v => v != null).Select(v => v!).ToList();
        float dpiScale = tb.Dpi / 96f;
        var (scale, graphs, shown) = Fit(views, tb, dpiScale);
        views = views.Take(shown).ToList();

        var sig = Signature(views, tb, scale) + floating + graphs;
        if (!force && sig == _signature && Visible)
        {
            KeepOnTop();
            return;
        }
        _signature = sig;
        _views = views;

        // Floating mode has no taskbar behind it, so it draws its own dark backdrop.
        bool light = !floating && LightTaskbar;
        using var bmp = RenderStrip(views, tb.Bounds.Height, scale, light, graphs, _hover, out _rects, floating);
        _pos = Place(tb, bmp.Width, dpiScale);
        if (floating)
        {
            var screen = Screen.FromHandle(tb.Handle).Bounds;
            _pos = new Point(_pos.X, screen.Bottom - bmp.Height - (int)(6 * scale));
        }
        Native.SetLayeredBitmap(Handle, bmp, _pos);
        if (!Visible) Show();
        KeepOnTop();
        if (_card.Visible) UpdateCard();
    }

    string Signature(List<ItemView> views, TaskbarInfo tb, float scale)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append(tb.Bounds).Append(tb.Tray.Left).Append(tb.Icons).Append(scale).Append(LightTaskbar).Append(_hover)
          .Append(_settings.WidgetGraphs).Append(_settings.WidgetOffset).Append(_settings.WidgetPosition);
        foreach (var v in views)
        {
            sb.Append('|').Append(v.Label).Append(v.Primary).Append(v.Line1).Append(v.Line2);
            // The bars scroll every sample, so include the newest value when graphs are on.
            if (_settings.WidgetGraphs && v.History is { Length: > 0 } h) sb.Append(h.Length).Append(h[^1]);
        }
        return sb.ToString();
    }

    /// <summary>What auto-fit had to do on the last draw (for Settings), or null if everything fit.</summary>
    public string? FitNote { get; private set; }

    /// <summary>
    /// The free stretch of taskbar the strip may use: between the app icons and the tray, or, for
    /// "left side", between the left edge and the icons (when there's room there).
    /// </summary>
    (int Left, int Right, bool AnchorLeft) FreeRegion(TaskbarInfo tb, float s)
    {
        int gap = (int)(8 * s);
        int trayLeft = tb.Tray.Width > 0 ? tb.Tray.Left : tb.Bounds.Right - (int)(240 * s);
        var icons = tb.Icons;
        if (_settings.WidgetPosition == WidgetPosition.LeftSide && !icons.IsEmpty && icons.Left - tb.Bounds.Left > 160 * s)
            return (tb.Bounds.Left + gap, icons.Left - gap, true);
        int left = !icons.IsEmpty && icons.Right < trayLeft ? icons.Right + gap : tb.Bounds.Left + gap;
        return (left, trayLeft - gap, _settings.WidgetPosition == WidgetPosition.LeftSide);
    }

    /// <summary>
    /// Shrinks the strip until it fits its free region, so it never covers Start or app icons:
    /// first hide graphs, then scale text down to 70%, then drop items from the end of the list.
    /// </summary>
    (float Scale, bool Graphs, int Shown) Fit(List<ItemView> views, TaskbarInfo tb, float dpiScale)
    {
        float scale = dpiScale * Math.Clamp(_settings.WidgetScale, 60, 150) / 100f;
        bool graphs = _settings.WidgetGraphs;
        FitNote = null;
        if (views.Count == 0) return (scale, graphs, 0);

        var (left, right, _) = FreeRegion(tb, dpiScale);
        int available = right - left;
        if (MeasureStrip(views, scale, graphs) <= available) return (scale, graphs, views.Count);

        if (graphs && MeasureStrip(views, scale, false) <= available)
        {
            FitNote = "Graphs are hidden so the stats fit between the app icons and the tray.";
            return (scale, false, views.Count);
        }
        graphs = false;
        float min = dpiScale * 0.7f, step = dpiScale * 0.05f;
        while (scale - step >= min && MeasureStrip(views, scale, false) > available) scale -= step;
        int shown = views.Count;
        while (shown > 1 && MeasureStrip(views.Take(shown).ToList(), scale, false) > available) shown--;
        FitNote = shown < views.Count
            ? $"{views.Count - shown} item{(views.Count - shown == 1 ? " is" : "s are")} hidden: there isn't room between the app icons and the tray. " +
              "Turn some items off, make the strip smaller, or move it to the left side."
            : "The stats were made smaller and graphs hidden so they fit between the app icons and the tray.";
        return (scale, graphs, shown);
    }

    Point Place(TaskbarInfo tb, int width, float s)
    {
        var (left, right, anchorLeft) = FreeRegion(tb, s);
        int maxOffset = (int)(Math.Max(0, right - left - width) / s);
        _settings.WidgetOffset = Math.Clamp(_settings.WidgetOffset, 0, maxOffset);
        int offset = (int)(_settings.WidgetOffset * s);
        int x = anchorLeft ? left + offset : right - width - offset;
        return new Point(Math.Clamp(x, tb.Bounds.Left, Math.Max(tb.Bounds.Left, tb.Bounds.Right - width)), tb.Bounds.Top);
    }

    /// <summary>An auto-hiding taskbar parks itself almost entirely off screen.</summary>
    static bool IsAutoHidden(TaskbarInfo tb)
    {
        var visible = Rectangle.Intersect(tb.Bounds, Screen.FromHandle(tb.Handle).Bounds);
        return visible.Height < tb.Bounds.Height * 0.9;
    }

    string? _placementKey;

    /// <summary>
    /// Cheap check (a few window-rect queries) for taskbar moves, auto-hide slides and fullscreen
    /// apps; redraws only when something changed. Returns true while the taskbar auto-hides, so the
    /// caller can poll faster just for that case.
    /// </summary>
    public bool CheckPlacement()
    {
        var tb = Native.FindTaskbar();
        bool autoHide = tb != null && IsAutoHidden(tb);
        var key = tb == null ? "none" : $"{tb.Bounds}|{tb.Tray.Left}|{tb.Icons}|{autoHide}|{Native.FullscreenAppActive(tb.Handle)}";
        if (key != _placementKey)
        {
            _placementKey = key;
            Redraw(force: true);
        }
        else if (Visible)
        {
            KeepOnTop();
        }
        return Native.TaskbarAutoHides();
    }

    void KeepOnTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    void HideAll()
    {
        HideCard();
        if (Visible) Hide();
        _signature = null;
    }

    // ---- Rendering --------------------------------------------------------------------------

    sealed class StripFonts
    {
        // Keyed by scale rounded to 1%: auto-fit only ever tries a handful of sizes.
        static readonly Dictionary<int, StripFonts> Cache = new();
        public required Font Label, Primary, Line;

        public static StripFonts For(float scale)
        {
            int key = (int)Math.Round(scale * 100);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            scale = key / 100f;
            Font Px(string family, float px, FontStyle style = FontStyle.Regular) => new(family, px * scale, style, GraphicsUnit.Pixel);
            return Cache[key] = new StripFonts
            {
                Label = Px("Segoe UI", 10f, FontStyle.Bold),
                Primary = Px("Segoe UI Semibold", 14.5f),
                Line = Px("Segoe UI Semibold", 11.5f),
            };
        }
    }

    static readonly StringFormat Typo = CreateFormat();

    static StringFormat CreateFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
        return f;
    }

    /// <summary>
    /// Column widths per item. They come from templates ("100%", "100°C") rather than the current
    /// text, so the strip doesn't jiggle as values change.
    /// </summary>
    static List<(int A, int B, int G, int Width)> LayoutItems(IReadOnlyList<ItemView> views, float scale, bool graphs)
    {
        int S(float v) => (int)Math.Round(v * scale);
        var fonts = StripFonts.For(scale);
        int padX = S(9), gap = S(7), graphW = S(34);
        using var probe = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(probe);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float Wd(string? s, Font f) => string.IsNullOrEmpty(s) ? 0 : mg.MeasureString(s, f, int.MaxValue, Typo).Width;

        var layout = new List<(int A, int B, int G, int Width)>();
        foreach (var v in views)
        {
            float a = Wd(v.Label?.ToUpperInvariant(), fonts.Label);
            foreach (var t in v.PrimaryTemplates) a = Math.Max(a, Wd(t, fonts.Primary));
            a = Math.Max(a, Wd(v.Primary, fonts.Primary));
            float b = 0;
            if (v.Line1 != null || v.Line2 != null)
            {
                foreach (var t in v.LineTemplates) b = Math.Max(b, Wd(t, fonts.Line));
                b = Math.Max(b, Math.Max(Wd(v.Line1, fonts.Line), Wd(v.Line2, fonts.Line)));
            }
            int ia = (int)Math.Ceiling(a), ib = (int)Math.Ceiling(b);
            int g = graphs && v.History != null ? graphW : 0;
            int width = padX * 2 + ia + (ia > 0 && ib > 0 ? gap : 0) + ib + (g > 0 ? gap + g : 0);
            layout.Add((ia, ib, g, width));
        }
        return layout;
    }

    static int StripWidth(List<(int A, int B, int G, int Width)> layout, float scale) =>
        Math.Max(1, layout.Sum(l => l.Width) + (int)Math.Round(2 * scale) * Math.Max(0, layout.Count - 1));

    /// <summary>Width the strip would need, without drawing it.</summary>
    internal static int MeasureStrip(IReadOnlyList<ItemView> views, float scale, bool graphs) =>
        StripWidth(LayoutItems(views, scale, graphs), scale);

    /// <summary>Lays out and draws the strip; also used by the settings and README previews.</summary>
    internal static Bitmap RenderStrip(IReadOnlyList<ItemView> views, int height, float scale, bool light, bool graphs, int hover,
                                       out List<Rectangle> rects, bool backdrop = false)
    {
        int S(float v) => (int)Math.Round(v * scale);
        var fonts = StripFonts.For(scale);
        var text = light ? Color.FromArgb(26, 26, 30) : Color.White;
        var dim = light ? Color.FromArgb(88, 90, 100) : Color.FromArgb(196, 200, 210);
        int padX = S(9), gap = S(7);

        var layout = LayoutItems(views, scale, graphs);
        int total = StripWidth(layout, scale);
        var bmp = new Bitmap(total, Math.Max(1, height), PixelFormat.Format32bppArgb);
        rects = new List<Rectangle>();
        using var g2 = Graphics.FromImage(bmp);
        // Alpha 1 is invisible but keeps the whole strip clickable (fully transparent pixels pass clicks through).
        g2.Clear(Color.FromArgb(1, 0, 0, 0));
        g2.SmoothingMode = SmoothingMode.AntiAlias;
        g2.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (backdrop)
        {
            var pill = new Rectangle(0, S(4), total - 1, height - S(8) - 1);
            Gfx.FillRounded(g2, pill, S(10), Color.FromArgb(235, 24, 26, 32));
            using var path = Gfx.RoundedPath(pill, S(10));
            using var pen = new Pen(Color.FromArgb(90, 255, 255, 255));
            g2.DrawPath(pen, path);
        }

        void Draw(string s, Font f, Color c, int x, float cy)
        {
            var h = g2.MeasureString(s, f, int.MaxValue, Typo).Height;
            using var brush = new SolidBrush(c);
            g2.DrawString(s, f, brush, x, cy - h / 2, Typo);
        }

        int ix = 0, cyMid = height / 2;
        for (int i = 0; i < views.Count; i++)
        {
            var v = views[i];
            var (a, b, gw, width) = layout[i];
            var rect = new Rectangle(ix, 0, width, height);
            rects.Add(rect);
            if (i == hover)
                Gfx.FillRounded(g2, Rectangle.Inflate(rect, 0, -S(5)), S(6), light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255));

            var accent = Theme.TaskbarAccent(v, light);
            int cx = ix + padX;
            bool hasLabel = !string.IsNullOrWhiteSpace(v.Label);
            if (a > 0)
            {
                var label = v.Label?.ToUpperInvariant() ?? "";
                var primaryColor = v.PrimaryTemp != null ? Theme.TaskbarTemp(v.PrimaryTemp, light, text) : text;
                if (hasLabel && v.Primary != null)
                {
                    Draw(label, fonts.Label, accent, cx, cyMid - S(8.5f));
                    Draw(v.Primary, fonts.Primary, primaryColor, cx, cyMid + S(6.5f));
                }
                else if (hasLabel) Draw(label, fonts.Label, accent, cx, cyMid);
                else if (v.Primary != null) Draw(v.Primary, fonts.Primary, primaryColor, cx, cyMid);
                cx += a + (b > 0 || gw > 0 ? gap : 0);
            }
            if (b > 0)
            {
                var c1 = v.Line1Temp != null ? Theme.TaskbarTemp(v.Line1Temp, light, text) : text;
                var c2 = v.Kind == WidgetKind.Network ? text : dim;
                if (v.Line1 != null && v.Line2 != null)
                {
                    Draw(v.Line1, fonts.Line, c1, cx, cyMid - S(7.5f));
                    Draw(v.Line2, fonts.Line, c2, cx, cyMid + S(7.5f));
                }
                else Draw(v.Line1 ?? v.Line2!, fonts.Line, c1, cx, cyMid);
                cx += b + gap;
            }
            if (gw > 0)
            {
                var track = light ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(34, 255, 255, 255);
                Gfx.Bars(g2, new Rectangle(cx, cyMid - S(13), gw, S(26)), v.History, v.HistoryMax, Theme.WithAlpha(accent, 235), track, scale);
            }
            ix += width + S(2);
        }
        return bmp;
    }

    // ---- Interaction ------------------------------------------------------------------------

    int HitIndex(Point p) => _rects.FindIndex(r => r.Contains(p));

    void UpdateCard()
    {
        if (!_cardWanted || _taskbar == null || _hover < 0 || _hover >= _views.Count || _hover >= _rects.Count) return;
        var r = _rects[_hover];
        _card.ShowFor(_views[_hover], new Rectangle(_pos.X + r.X, _pos.Y + r.Y, r.Width, r.Height), _taskbar);
    }

    void HideCard()
    {
        _hoverTimer.Stop();
        _cardWanted = false;
        if (_card.Visible) _card.Hide();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseDown)
        {
            int dx = Cursor.Position.X - _downScreen.X;
            float scale = (_taskbar?.Dpi ?? 96) / 96f;
            if (!_dragging && Math.Abs(dx) > 4 * scale)
            {
                _dragging = true;
                HideCard();
                Cursor = Cursors.SizeWE;
            }
            if (_dragging)
            {
                int d = (int)(dx / scale);
                _settings.WidgetOffset = Math.Max(0, _settings.WidgetPosition == WidgetPosition.NextToTray ? _downOffset - d : _downOffset + d);
                Redraw(force: true);
            }
            return;
        }

        int idx = HitIndex(e.Location);
        if (idx == _hover) return;
        _hover = idx;
        Redraw(force: true);
        if (idx < 0) HideCard();
        else if (_card.Visible) UpdateCard();
        else
        {
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_mouseDown) return;
        _hover = -1;
        HideCard();
        Redraw(force: true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _mouseDown = true;
            _dragging = false;
            _downScreen = Cursor.Position;
            _downOffset = _settings.WidgetOffset;
        }
        else if (e.Button == MouseButtons.Right)
        {
            HideCard();
            _menu.Show(Cursor.Position);
        }
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
            _settings.Save();
            return;
        }
        int idx = HitIndex(e.Location);
        HideCard();
        if (idx >= 0 && idx < _views.Count) ItemClicked?.Invoke(_views[idx]);
    }

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
            _hoverTimer.Dispose();
            _card.Dispose();
        }
        base.Dispose(disposing);
    }
}
