using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PCVitals;

/// <summary>
/// The dashboard: a rail of devices with live mini
/// graphs on the left, and the selected device on the right (big history graph, stat tiles, top
/// processes, then every sensor). Fully owner-drawn so a few hundred live values repaint without
/// flicker or a control per row.
/// </summary>
sealed class DashboardView : Control
{
    static readonly SensorType[] GroupOrder =
    {
        SensorType.Temperature, SensorType.Load, SensorType.Power, SensorType.Clock, SensorType.Fan,
        SensorType.Control, SensorType.Voltage, SensorType.Current, SensorType.SmallData, SensorType.Data,
        SensorType.Throughput, SensorType.Level, SensorType.Energy, SensorType.Factor, SensorType.TimeSpan,
    };

    // Up to 15 min uses per-refresh samples; 1 h and 24 h use one-minute averages.
    static readonly int[] Ranges = { 60, 300, 900, 3600, 86400 };
    static readonly Color TempLine = Color.FromArgb(255, 150, 90);

    enum ProcessSort { None, Cpu, Memory, Gpu }

    /// <summary>Pages with a process list; only these pay for process (and per-process GPU) sampling.</summary>
    static bool ShowsProcesses(string page) => page is "overview" or "cpu" or "memory" || page.StartsWith("gpu:", StringComparison.Ordinal);

    sealed class Page
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required string Glyph { get; init; }
        public required Color Accent { get; init; }
        public ItemView? View { get; init; }
        public string? Sub { get; init; }
        public string? HeaderSub { get; init; }
        public string? RailValue { get; init; }
        public List<HardwareReading> Readings { get; } = new();
        public string? MainSeries { get; init; }
        public string MainLabel { get; init; } = "Usage";
        public float? MainMax { get; init; }
        public Func<float, string> MainFormat { get; init; } = v => $"{v:0}%";
        public string? SecondSeries { get; init; }
        public string? SecondLabel { get; init; }
        public bool SecondIsTemp { get; init; }
        public ProcessSort Processes { get; init; }
    }

    readonly AppSettings _settings;
    readonly bool _isAdmin;
    readonly ProcessStats _procs = new();
    readonly List<(Rectangle Rect, Action Action, string Name)> _hits = new();

    Snapshot? _snap;
    float _scroll;
    int _contentHeight;
    Rectangle _viewport, _thumb, _plot;
    bool _draggingThumb;
    int _dragStartY;
    float _dragStartScroll;
    Point _mouse = new(-1, -1);
    bool _pinned;
    UiFonts _f = null!;
    static string? _osName;

    public event Action? CloseRequested;
    public event Action? PinToggled;
    public event Action? RestartAsAdminRequested;
    public event Action? SettingsRequested;

    /// <summary>Supplies long history for the big graph: (series key, sample count) → samples.</summary>
    public Func<string, int, float[]>? HistoryProvider { get; set; }
    /// <summary>Supplies one-minute averages for the 1 h / 24 h ranges: (series key, minutes) → samples.</summary>
    public Func<string, int, float[]>? MinuteHistoryProvider { get; set; }
    /// <summary>Overrides the machine name in the header (used for README screenshots).</summary>
    public string? MachineLabel { get; set; }
    public int ContentHeight => _contentHeight;

    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            Invalidate();
        }
    }

    public DashboardView(AppSettings settings, bool isAdmin)
    {
        _settings = settings;
        _isAdmin = isAdmin;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        TabStop = true;
    }

    public void SetSnapshot(Snapshot snap)
    {
        _snap = snap;
        // Process sampling costs a syscall per refresh, so only while a page shows processes.
        if (Visible && ShowsProcesses(_settings.DashboardPage)) _procs.Sample(includeGpu: true);
        Invalidate();
    }

    /// <summary>Takes a process sample regardless of visibility (README previews).</summary>
    internal void SampleProcessesNow() => _procs.Sample(includeGpu: true);

    /// <summary>Opens the page for a taskbar item (or the overview). Returns true if the page changed.</summary>
    public bool ShowPage(WidgetKind? kind)
    {
        var s = _snap?.Summary;
        var key = kind switch
        {
            WidgetKind.Cpu => "cpu",
            WidgetKind.Gpu when s?.Gpu != null => "gpu:" + s.Gpu.Id,
            WidgetKind.SecondGpu when s?.SecondGpu != null => "gpu:" + s.SecondGpu.Id,
            WidgetKind.Memory => "memory",
            WidgetKind.Network => "network",
            WidgetKind.Board => "board",
            WidgetKind.Disk when s?.Disks.Count > 0 => "disk:" + s.Disks[0].Id,
            WidgetKind.Battery when s?.Battery != null => "battery",
            null => null,
            _ => "overview",
        };
        return key != null && Navigate(key);
    }

    bool Navigate(string key)
    {
        if (_settings.DashboardPage == key) return false;
        _settings.DashboardPage = key;
        _settings.Save();
        _scroll = 0;
        if (ShowsProcesses(key)) _procs.Sample(includeGpu: true);
        Invalidate();
        return true;
    }

    int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    // ---- Page model -------------------------------------------------------------------------

    List<Page> BuildPages(Snapshot snap)
    {
        var s = snap.Summary;
        var pages = new List<Page>
        {
            new()
            {
                Key = "overview", Name = "Overview", Glyph = "", Accent = Theme.Cpu,
                Sub = MachineLabel ?? Environment.MachineName,
                RailValue = s.TotalPower is { } p ? $"{p:0} W CPU + GPU" : null,
            },
        };

        var cpuHw = snap.Hardware.FirstOrDefault(h => h.Type == HardwareType.Cpu);
        if (cpuHw != null)
        {
            int threads = cpuHw.Sensors.Count(x => x.Type == SensorType.Load && x.Name.StartsWith("CPU Core #"));
            var page = new Page
            {
                Key = "cpu", Name = "CPU", Glyph = "", Accent = Theme.Cpu, View = Vitals.Build(WidgetKind.Cpu, null, snap),
                Sub = s.CpuShortName, HeaderSub = threads > 0 ? $"Processor · {threads} threads" : "Processor",
                RailValue = $"{Vitals.Pct(s.CpuLoad)} · {Fmt.Temp(s.CpuTemp)}",
                MainSeries = "cpu", MainMax = 100, SecondSeries = "cputemp", SecondLabel = "Temperature", SecondIsTemp = true,
                Processes = ProcessSort.Cpu,
            };
            page.Readings.Add(cpuHw);
            pages.Add(page);
        }

        var gpus = s.Gpus.OrderBy(g => g.Id == s.Gpu?.Id ? 0 : 1).ToList();
        foreach (var g in gpus)
        {
            var page = new Page
            {
                Key = "gpu:" + g.Id, Name = g.Integrated ? "iGPU" : "GPU", Glyph = "",
                Accent = g.Integrated ? Theme.IGpu : Theme.Gpu, View = Vitals.ForGpu(g, snap), Sub = g.ShortName,
                HeaderSub = g.Integrated
                    ? "Integrated graphics"
                    : $"Graphics card{(g.VramTotalMb is { } vram ? $" · {vram / 1024:0} GB VRAM" : "")}",
                RailValue = $"{Vitals.Pct(g.Load)} · {Fmt.Temp(g.Temp)}",
                MainSeries = "gpu:" + g.Id, MainMax = 100, SecondSeries = "gputemp:" + g.Id, SecondLabel = "Temperature", SecondIsTemp = true,
                Processes = ProcessSort.Gpu,
            };
            page.Readings.AddRange(snap.Hardware.Where(h => h.Id == g.Id));
            pages.Add(page);
        }

        if (s.RamTotalGb != null)
        {
            var view = Vitals.Build(WidgetKind.Memory, null, snap);
            var page = new Page
            {
                Key = "memory", Name = "Memory", Glyph = "", Accent = Theme.Ram, View = view,
                Sub = $"{s.RamTotalGb:0} GB", HeaderSub = view?.Device is { } dev && dev != "System memory" ? dev : $"{s.RamTotalGb:0} GB installed",
                RailValue = $"{Vitals.Pct(s.RamLoad)} · {s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB",
                MainSeries = "ram", MainMax = 100, Processes = ProcessSort.Memory,
            };
            page.Readings.AddRange(snap.Hardware.Where(h => h.Type == HardwareType.Memory));
            pages.Add(page);
        }

        foreach (var d in s.Disks)
        {
            var page = new Page
            {
                Key = "disk:" + d.Id, Name = "Drive", Glyph = "", Accent = Theme.Storage, View = Vitals.ForDisk(d, snap),
                Sub = d.Name, HeaderSub = d.UsedPct is { } u ? $"Storage · {u:0}% full" : "Storage",
                RailValue = $"{Fmt.Temp(d.Temp)}{(d.UsedPct is { } used ? $" · {used:0}% full" : "")}",
                MainSeries = "diskio:" + d.Id, MainLabel = "Read + write", MainFormat = v => Fmt.Rate(v),
                SecondSeries = "disk:" + d.Id, SecondLabel = "Temperature", SecondIsTemp = true,
            };
            page.Readings.AddRange(snap.Hardware.Where(h => h.Id == d.Id));
            pages.Add(page);
        }

        if (s.NetDown != null)
        {
            var page = new Page
            {
                Key = "network", Name = "Network", Glyph = "", Accent = Theme.Net, View = Vitals.Build(WidgetKind.Network, null, snap),
                Sub = s.NetAdapter, HeaderSub = s.NetAdapter != null ? $"Network · {s.NetAdapter}" : "Network",
                RailValue = $"↓ {Fmt.Rate(s.NetDown ?? 0)}  ↑ {Fmt.Rate(s.NetUp ?? 0)}",
                MainSeries = "netdown", MainLabel = "Download", MainFormat = v => Fmt.Rate(v),
                SecondSeries = "netup", SecondLabel = "Upload",
            };
            page.Readings.AddRange(snap.Hardware.Where(h => h.Type == HardwareType.Network));
            pages.Add(page);
        }

        var boardHw = snap.Hardware.Where(h => h.Type is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController).ToList();
        if (boardHw.Count > 0)
        {
            var page = new Page
            {
                Key = "board", Name = "Board", Glyph = "", Accent = Theme.Board, View = Vitals.Build(WidgetKind.Board, null, snap),
                Sub = s.BoardName is { } b ? HardwareService.ShortName(b) : null, HeaderSub = "Motherboard · chipset, VRM, fans",
                RailValue = s.ChipsetTemp is { } ch ? $"Chipset {Fmt.Temp(ch)}" : null,
                MainSeries = "chipset", MainLabel = "Chipset temperature", MainMax = 100, MainFormat = v => Fmt.Temp(v),
            };
            page.Readings.AddRange(boardHw);
            pages.Add(page);
        }

        if (s.Battery is { } battery && snap.Hardware.FirstOrDefault(h => h.Type == HardwareType.Battery) is { } batteryHw)
        {
            var view = Vitals.ForBattery(snap);
            var page = new Page
            {
                Key = "battery", Name = "Battery", Glyph = "", Accent = Theme.Good, View = view, Sub = battery.Name,
                HeaderSub = view?.TileSub, RailValue = $"{Vitals.Pct(battery.Charge)}{(view?.Line1 is { } l ? $" · {l}" : "")}",
                MainSeries = "battery", MainLabel = "Charge", MainMax = 100,
            };
            page.Readings.Add(batteryHw);
            pages.Add(page);
        }

        foreach (var hw in snap.Hardware.Where(h => h.Type is HardwareType.Psu or HardwareType.Cooler or HardwareType.PowerMonitor))
        {
            var page = new Page
            {
                Key = "hw:" + hw.Id, Name = Theme.Badge(hw.Type) switch { var b => char.ToUpper(b[0]) + b[1..].ToLowerInvariant() },
                Glyph = hw.Type == HardwareType.Battery ? "" : "", Accent = Theme.ForHardware(hw.Type),
                View = Vitals.ForHardware(hw), Sub = hw.Name,
            };
            page.Readings.Add(hw);
            pages.Add(page);
        }
        return pages;
    }

    // ---- Painting ---------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        _f = UiFonts.For(DeviceDpi);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        _hits.Clear();
        _plot = Rectangle.Empty;

        int w = ClientSize.Width, h = ClientSize.Height, headerH = S(58);
        if (_snap == null)
        {
            DrawHeader(g, w, headerH);
            Gfx.Text(g, "Reading sensors…", _f.Body, Theme.TextDim, new Rectangle(0, h / 2 - S(10), w, S(24)), TextFormatFlags.HorizontalCenter);
            return;
        }

        var pages = BuildPages(_snap);
        var page = pages.FirstOrDefault(p => p.Key == _settings.DashboardPage) ?? pages[0];

        int railW = S(236);
        DrawRail(g, pages, page, new Rectangle(0, headerH, railW, h - headerH));

        int contentX = railW + S(6);
        _viewport = new Rectangle(contentX, headerH, w - contentX, Math.Max(0, h - headerH));
        g.SetClip(_viewport);
        int top = headerH - (int)_scroll;
        int bottom = page.Key == "overview"
            ? DrawOverview(g, pages, contentX + S(8), top, _viewport.Width - S(30))
            : DrawDevicePage(g, page, contentX + S(8), top, _viewport.Width - S(30));
        _contentHeight = bottom - top;
        g.ResetClip();

        DrawHeader(g, w, headerH);
        DrawScrollbar(g);
        DrawFocusRing(g);
        if (ClampScroll()) Invalidate();

        // What a screen reader announces for the dashboard itself: the page and its key values.
        var rows = page.View?.Rows.Select(r => $"{r.Label} {r.Value}") ?? Enumerable.Empty<string>();
        _a11ySummary = string.Join(". ", new[] { page.View?.Device ?? page.Name }.Concat(rows));
    }

    void DrawHeader(Graphics g, int width, int height)
    {
        using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, width, height);
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, height - 1, width, height - 1);

        int pad = S(18);
        Gfx.Text(g, "PC Vitals", _f.Title, Theme.Text, new Rectangle(pad, S(10), S(300), S(24)));
        var uptime = Fmt.Duration(Environment.TickCount64 / 1000f);
        Gfx.Text(g, $"{MachineLabel ?? Environment.MachineName}  ·  up {uptime}", _f.Sub, Theme.TextDim, new Rectangle(pad, S(33), S(360), S(18)));

        // At most one banner: missing admin rights first, then a missing PawnIO driver.
        (string Text, Action Action)? banner = null;
        if (!_isAdmin) banner = ("Limited mode: click to restart as administrator", () => RestartAsAdminRequested?.Invoke());
        else if (PawnIoMissing) banner = ("CPU temperature needs the free PawnIO driver: click to get it", OpenPawnIoSite);
        if (banner is { } b)
        {
            int ww = Gfx.TextWidth(g, b.Text, _f.Small) + S(20);
            var r = new Rectangle((width - ww) / 2, S(16), ww, S(26));
            Gfx.FillRounded(g, r, S(13), Theme.WithAlpha(Theme.Warn, r.Contains(_mouse) ? 60 : 36));
            Gfx.Text(g, b.Text, _f.Small, Theme.Warn, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            _hits.Add((r, b.Action, b.Text));
        }

        int btn = S(32);
        var close = new Rectangle(width - pad - btn + S(6), S(13), btn, btn);
        var pin = new Rectangle(close.Left - btn - S(2), close.Top, btn, btn);
        var gear = new Rectangle(pin.Left - btn - S(2), close.Top, btn, btn);
        DrawIconButton(g, close, "", Theme.TextDim, "Close", () => CloseRequested?.Invoke());
        DrawIconButton(g, pin, Pinned ? "" : "", Pinned ? Theme.Cpu : Theme.TextDim, Pinned ? "Unpin dashboard" : "Pin dashboard (keep it open)", () => PinToggled?.Invoke());
        DrawIconButton(g, gear, "", Theme.TextDim, "Settings", () => SettingsRequested?.Invoke());
    }

    public const string PawnIoUrl = "https://pawnio.eu/";

    // Checked once per process; installing the driver needs an app restart anyway.
    static readonly Lazy<bool> PawnIoMissingLazy = new(() => HardwareService.PawnIoStatus().Installed == false);
    static bool PawnIoMissing => PawnIoMissingLazy.Value;

    static void OpenPawnIoSite()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PawnIoUrl) { UseShellExecute = true }); }
        catch { }
    }

    void DrawIconButton(Graphics g, Rectangle r, string glyph, Color color, string name, Action onClick)
    {
        if (r.Contains(_mouse)) Gfx.FillRounded(g, r, S(6), Theme.SurfaceHover);
        Gfx.Text(g, glyph, _f.Icon, color, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        _hits.Add((r, onClick, name));
    }

    // ---- Rail -------------------------------------------------------------------------------

    void DrawRail(Graphics g, List<Page> pages, Page current, Rectangle area)
    {
        using (var bg = new SolidBrush(Theme.Rail)) g.FillRectangle(bg, area);
        int pad = S(8);
        int entryH = Math.Min(S(64), (area.Height - pad * 2) / Math.Max(1, pages.Count));
        int y = area.Top + pad;
        foreach (var p in pages)
        {
            var r = new Rectangle(area.Left + pad, y, area.Width - pad * 2, entryH - S(4));
            bool selected = p == current;
            if (selected) Gfx.FillRounded(g, r, S(8), Theme.SurfaceHover);
            else if (r.Contains(_mouse)) Gfx.FillRounded(g, r, S(8), Theme.Surface);
            if (selected)
            {
                using var bar = new SolidBrush(p.Accent);
                g.FillRectangle(bar, r.Left, r.Top + r.Height / 4, S(3), r.Height / 2);
            }

            // Mini graph (or icon for pages without a series).
            var thumb = new Rectangle(r.Left + S(12), r.Top + (r.Height - S(34)) / 2, S(52), S(34));
            using (var pen = new Pen(Theme.WithAlpha(p.Accent, 110))) g.DrawRectangle(pen, thumb);
            if (p.MainSeries != null && _snap!.History.TryGetValue(p.MainSeries, out var hist))
            {
                var state = g.Save();
                g.SetClip(thumb);
                Gfx.Sparkline(g, Rectangle.Inflate(thumb, -1, -2), hist, p.MainMax, p.Accent, Math.Max(1f, S(1.2f)));
                g.Restore(state);
            }
            else
            {
                Gfx.Text(g, p.Glyph, _f.IconLarge, p.Accent, thumb, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int tx = thumb.Right + S(10), tw = r.Right - tx - S(6);
            int lineH = S(17);
            int ty = r.Top + (r.Height - lineH * 3) / 2;
            Gfx.Text(g, p.Name, _f.BodyBold, selected ? Theme.Text : Theme.Text, new Rectangle(tx, ty, tw, lineH), TextFormatFlags.EndEllipsis);
            Gfx.Text(g, p.RailValue ?? "", _f.Small, Theme.TextDim, new Rectangle(tx, ty + lineH, tw, lineH), TextFormatFlags.EndEllipsis);
            Gfx.Text(g, p.Sub ?? "", _f.Small, Theme.TextFaint, new Rectangle(tx, ty + lineH * 2, tw, lineH), TextFormatFlags.EndEllipsis);

            var key = p.Key;
            _hits.Add((r, () => Navigate(key), $"{p.Name}{(p.Sub != null ? $" {p.Sub}" : "")}: {p.RailValue}"));
            y += entryH;
        }
    }

    // ---- Overview ---------------------------------------------------------------------------

    int DrawOverview(Graphics g, List<Page> pages, int x, int y, int w)
    {
        y += S(14);
        Gfx.Text(g, "Overview", _f.Heading, Theme.Text, new Rectangle(x, y, w, S(30)));
        y += S(34);
        Gfx.Text(g, "Everything at a glance. Pick a device on the left for details.", _f.Sub, Theme.TextDim, new Rectangle(x, y, w, S(18)));
        y += S(30);

        var s = _snap!.Summary;
        var tiles = new (WidgetKind Kind, string? Page)[]
        {
            (WidgetKind.Cpu, "cpu"), (WidgetKind.Gpu, s.Gpu != null ? "gpu:" + s.Gpu.Id : null), (WidgetKind.Memory, "memory"),
            (WidgetKind.Power, "cpu"), (WidgetKind.Board, "board"), (WidgetKind.Network, "network"),
        }.Select(t => (View: Vitals.Build(t.Kind, null, _snap), t.Page)).Where(t => t.View != null).ToList();

        int gap = S(10), cols = 3;
        int tileW = (w - gap * (cols - 1)) / cols, tileH = S(108);
        for (int i = 0; i < tiles.Count; i++)
        {
            var r = new Rectangle(x + (i % cols) * (tileW + gap), y + (i / cols) * (tileH + gap), tileW, tileH);
            DrawTile(g, r, tiles[i].View!);
            if (tiles[i].Page is { } key) AddHit(r, () => Navigate(key), $"{tiles[i].View!.Title}: {tiles[i].View!.Primary ?? tiles[i].View!.Line2}, open details");
        }
        int rows = (tiles.Count + cols - 1) / cols;
        y += rows * tileH + Math.Max(0, rows - 1) * gap + S(18);

        // System summary card.
        var info = new List<(string, string)> { ("Windows", OsName()) };
        if (s.CpuName != null) info.Add(("Processor", s.CpuName));
        foreach (var gpu in s.Gpus) info.Add((gpu.Integrated ? "Integrated graphics" : "Graphics", gpu.Name));
        var mem = Vitals.Build(WidgetKind.Memory, null, _snap);
        if (mem != null) info.Add(("Memory", $"{s.RamTotalGb:0} GB" + (mem.Device != "System memory" ? $" · {mem.Device}" : "")));
        if (s.BoardName != null) info.Add(("Motherboard", s.BoardName));
        if (s.Disks.Count > 0) info.Add(("Storage", string.Join(", ", s.Disks.Select(d => d.Name))));
        info.Add(("Uptime", Fmt.Duration(Environment.TickCount64 / 1000f)));
        y = DrawKeyValueCard(g, "System", info, x, y, w) + S(14);

        y = DrawProcesses(g, ProcessSort.Cpu, x, y, w, 5) + S(14);
        return y + S(20);
    }

    static string OsName()
    {
        if (_osName != null) return _osName;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var build = key?.GetValue("CurrentBuild") as string ?? "";
            var display = key?.GetValue("DisplayVersion") as string;
            // ProductName still says "Windows 10" on Windows 11.
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            _osName = $"{product}{(display != null ? $" {display}" : "")} (build {build})";
        }
        catch
        {
            _osName = Environment.OSVersion.VersionString;
        }
        return _osName;
    }

    int DrawKeyValueCard(Graphics g, string title, List<(string Key, string Value)> rows, int x, int y, int w)
    {
        int rowH = S(26);
        var card = new Rectangle(x, y, w, S(44) + rows.Count * rowH + S(8));
        if (card.Bottom < _viewport.Top || card.Top > _viewport.Bottom) return card.Bottom;
        Gfx.FillRounded(g, card, S(10), Theme.Surface);
        Gfx.Text(g, title.ToUpperInvariant(), _f.Label, Theme.TextFaint, new Rectangle(x + S(16), y + S(14), w, S(16)));
        int ry = y + S(40);
        foreach (var (k, v) in rows)
        {
            Gfx.Text(g, k, _f.Body, Theme.TextDim, new Rectangle(x + S(16), ry, S(170), rowH), TextFormatFlags.VerticalCenter);
            Gfx.Text(g, v, _f.Body, Theme.Text, new Rectangle(x + S(190), ry, w - S(206), rowH), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            ry += rowH;
        }
        return card.Bottom;
    }

    void DrawTile(Graphics g, Rectangle r, ItemView v)
    {
        bool hover = r.Contains(_mouse);
        Gfx.FillRounded(g, r, S(10), hover ? Theme.SurfaceHover : Theme.Surface);

        var spark = new Rectangle(r.Left, r.Top + r.Height * 48 / 100, r.Width, r.Height * 52 / 100);
        var state = g.Save();
        using (var clip = Gfx.RoundedPath(r, S(10))) g.SetClip(clip, CombineMode.Intersect);
        Gfx.Sparkline(g, spark, v.History, v.HistoryMax, v.Accent, Math.Max(1.2f, S(1.5f)));
        g.Restore(state);

        int px = r.Left + S(12), inner = r.Width - S(24);
        using (var dot = new SolidBrush(v.Accent)) g.FillEllipse(dot, px, r.Top + S(15), S(7), S(7));
        var title = v.Title.ToUpperInvariant();
        Gfx.Text(g, title, _f.Label, Theme.TextDim, new Rectangle(px + S(12), r.Top + S(11), inner, S(16)));
        if (v.DeviceShort != null)
        {
            int tx = px + S(12) + Gfx.TextWidth(g, title, _f.Label) + S(6);
            Gfx.Text(g, v.DeviceShort, _f.Small, Theme.TextFaint, new Rectangle(tx, r.Top + S(10), r.Right - S(10) - tx, S(17)), TextFormatFlags.EndEllipsis);
        }

        var primary = v.Primary ?? v.Line2 ?? "--";
        var bigFont = Gfx.TextWidth(g, primary, _f.Big) <= inner - S(56) ? _f.Big : _f.Mid;
        var valueRow = new Rectangle(px, r.Top + S(30), inner, S(38));
        Gfx.Text(g, primary, bigFont, Theme.Text, valueRow, TextFormatFlags.VerticalCenter);
        if (v.Line1Temp != null && v.Line1 != null)
            Gfx.Text(g, v.Line1, _f.Mid, Theme.TempColor(v.Line1Temp), valueRow, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        Gfx.Text(g, v.TileSub ?? "", _f.Small, Theme.TextDim, new Rectangle(px, r.Bottom - S(25), inner, S(18)), TextFormatFlags.EndEllipsis);
    }

    // ---- Device page ------------------------------------------------------------------------

    int DrawDevicePage(Graphics g, Page page, int x, int y, int w)
    {
        y += S(14);
        int pillX = x;
        var title = page.View?.Device ?? page.Name;
        int titleW = Math.Min(Gfx.TextWidth(g, title, _f.Heading), w - S(200));
        Gfx.Text(g, title, _f.Heading, Theme.Text, new Rectangle(x, y, titleW + S(4), S(30)), TextFormatFlags.EndEllipsis);
        pillX = x + titleW + S(12);
        if (page.View?.Tag is { } tag) pillX = DrawPill(g, tag.ToUpperInvariant(), page.Accent, pillX, y + S(2), S(26), filled: true) + S(6);
        if (page.Key.StartsWith("gpu:") && _snap!.Summary.Gpus.Count > 1 && page.Key == "gpu:" + _snap.Summary.Gpu?.Id)
            DrawPill(g, "PRIMARY", Theme.Cpu, pillX, y + S(2), S(26), filled: false);
        y += S(34);
        Gfx.Text(g, page.HeaderSub ?? page.Name, _f.Sub, Theme.TextDim, new Rectangle(x, y, w, S(18)), TextFormatFlags.EndEllipsis);
        y += S(30);

        if (page.MainSeries != null) y = DrawGraphCard(g, page, x, y, w) + S(14);
        if (page.View != null) y = DrawStatGrid(g, page, x, y, w) + S(14);

        if (page.Key == "cpu")
        {
            var threads = page.Readings.SelectMany(h => h.Sensors)
                .Where(s => s.Type == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)).ToList();
            if (threads.Count > 1) y = DrawThreadBars(g, threads, page.Accent, x, y, w) + S(14);
        }
        if (page.Processes != ProcessSort.None) y = DrawProcesses(g, page.Processes, x, y, w, 6) + S(14);

        // Every sensor for this device.
        y += S(6);
        Gfx.Text(g, "ALL SENSORS", _f.Label, Theme.TextFaint, new Rectangle(x + S(4), y, w, S(16)));
        var toggle = _settings.ShowAdvancedSensors ? "Hide advanced" : "Show advanced";
        int tw = Gfx.TextWidth(g, toggle, _f.Small);
        var tr = new Rectangle(x + w - tw - S(14), y - S(5), tw + S(14), S(24));
        if (tr.Contains(_mouse)) Gfx.FillRounded(g, tr, S(6), Theme.SurfaceHover);
        Gfx.Text(g, toggle, _f.Small, Theme.Cpu, tr, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        AddHit(tr, toggle + " sensors", () =>
        {
            _settings.ShowAdvancedSensors = !_settings.ShowAdvancedSensors;
            _settings.Save();
            Invalidate();
        });
        y += S(24);
        foreach (var hw in page.Readings) y = DrawSection(g, hw, x, y, w) + S(8);

        var footer = $"Updated {_snap!.Time:HH:mm:ss}  ·  every {_settings.IntervalMs / 1000.0:0.#} s  ·  sensors by LibreHardwareMonitor";
        Gfx.Text(g, footer, _f.Small, Theme.TextFaint, new Rectangle(x, y + S(6), w, S(20)), TextFormatFlags.HorizontalCenter);
        return y + S(40);
    }

    int DrawGraphCard(Graphics g, Page page, int x, int y, int w)
    {
        var card = new Rectangle(x, y, w, S(236));
        if (card.Bottom < _viewport.Top || card.Top > _viewport.Bottom) return card.Bottom;
        Gfx.FillRounded(g, card, S(10), Theme.Surface);

        int seconds = _settings.GraphSeconds;
        bool minuteTier = seconds > 900;
        int samples = minuteTier
            ? Math.Clamp(seconds / 60, 2, HardwareService.MinuteHistoryLength)
            : Math.Clamp(seconds * 1000 / Math.Max(100, _settings.IntervalMs), 2, HardwareService.LongHistoryLength);
        int secondsPerSample = minuteTier ? 60 : Math.Max(1, _settings.IntervalMs / 1000);
        var provider = minuteTier ? MinuteHistoryProvider : HistoryProvider;
        float[] main = provider?.Invoke(page.MainSeries!, samples) ?? (minuteTier ? null : _snap!.History.GetValueOrDefault(page.MainSeries!)) ?? Array.Empty<float>();
        float[]? second = page.SecondSeries == null ? null
            : provider?.Invoke(page.SecondSeries, samples) ?? (minuteTier ? null : _snap!.History.GetValueOrDefault(page.SecondSeries));

        // Legend.
        int lx = x + S(16), ly = y + S(14);
        lx = DrawLegend(g, page.MainLabel, page.Accent, lx, ly) + S(16);
        if (page.SecondLabel != null) DrawLegend(g, page.SecondLabel, page.SecondIsTemp ? TempLine : Theme.Ram, lx, ly);

        // Range chips.
        int cx = x + w - S(14);
        for (int i = Ranges.Length - 1; i >= 0; i--)
        {
            int secs = Ranges[i];
            var label = secs >= 3600 ? $"{secs / 3600} h" : $"{secs / 60} min";
            int cw = Gfx.TextWidth(g, label, _f.Small) + S(18);
            var chip = new Rectangle(cx - cw, y + S(10), cw, S(24));
            bool on = seconds == secs;
            Gfx.FillRounded(g, chip, S(12), on ? Theme.WithAlpha(page.Accent, 60) : chip.Contains(_mouse) ? Theme.SurfaceHover : Theme.Bg);
            Gfx.Text(g, label, _f.Small, on ? Theme.Text : Theme.TextDim, chip, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            AddHit(chip, $"Graph range {label}{(on ? ", selected" : "")}", () =>
            {
                _settings.GraphSeconds = secs;
                _settings.Save();
                Invalidate();
            });
            cx = chip.Left - S(6);
        }

        // No temperature axis when there's no temperature data (e.g. CPU without admin rights).
        bool tempAxis = page.SecondIsTemp && second != null && second.Any(v => !float.IsNaN(v));
        if (page.SecondIsTemp && !tempAxis) second = null;
        var plot = new Rectangle(x + S(tempAxis ? 52 : 16), y + S(46), w - S(tempAxis ? 52 : 16) - S(58), S(160));
        _plot = plot;

        float mainMax = page.MainMax ?? NiceMax(main.Concat(page.SecondIsTemp ? Array.Empty<float>() : second ?? Array.Empty<float>()));
        float tempMax = tempAxis ? Math.Max(100, (float)Math.Ceiling(MaxOf(second!) / 10) * 10) : 0;

        // Grid and axis labels.
        using (var grid = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
        {
            for (int i = 0; i <= 4; i++)
            {
                int gy = plot.Bottom - plot.Height * i / 4;
                g.DrawLine(grid, plot.Left, gy, plot.Right, gy);
                Gfx.Text(g, page.MainFormat(mainMax * i / 4), _f.Small, Theme.TextFaint,
                         new Rectangle(plot.Right + S(6), gy - S(9), S(56), S(18)), TextFormatFlags.VerticalCenter);
                if (tempAxis)
                    Gfx.Text(g, Fmt.Temp(tempMax * i / 4), _f.Small, Theme.WithAlpha(TempLine, 200),
                             new Rectangle(x + S(4), gy - S(9), S(44), S(18)), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }
        Gfx.Text(g, $"{FormatRange(seconds)} ago", _f.Small, Theme.TextFaint, new Rectangle(plot.Left, plot.Bottom + S(4), S(120), S(18)));
        Gfx.Text(g, "now", _f.Small, Theme.TextFaint, new Rectangle(plot.Right - S(60), plot.Bottom + S(4), S(60), S(18)), TextFormatFlags.Right);
        if (minuteTier && main.Length < 2)
            Gfx.Text(g, "One-minute averages build up while PC Vitals runs (up to 24 hours).", _f.Small, Theme.TextDim, plot,
                     TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var state = g.Save();
        g.SetClip(Rectangle.Inflate(plot, 0, 2), CombineMode.Intersect);
        DrawSeries(g, plot, main, samples, mainMax, page.Accent, fill: true);
        if (second != null)
            DrawSeries(g, plot, second, samples, page.SecondIsTemp ? tempMax : mainMax, page.SecondIsTemp ? TempLine : Theme.Ram, fill: false);
        g.Restore(state);

        // Crosshair readout.
        if (plot.Contains(_mouse) && main.Length > 0)
        {
            float step = plot.Width / (float)(samples - 1);
            int fromRight = (int)Math.Round((plot.Right - _mouse.X) / step);
            int idx = main.Length - 1 - fromRight;
            if (idx >= 0 && idx < main.Length)
            {
                float px = plot.Right - fromRight * step;
                using (var pen = new Pen(Theme.TextFaint)) g.DrawLine(pen, px, plot.Top, px, plot.Bottom);
                var lines = new List<(string, Color)>
                {
                    ($"{FormatRange(fromRight * secondsPerSample)} ago", Theme.TextDim),
                    ($"{page.MainLabel}: {(float.IsNaN(main[idx]) ? "—" : page.MainFormat(main[idx]))}", page.Accent),
                };
                if (second != null)
                {
                    int sIdx = second.Length - 1 - fromRight;
                    if (sIdx >= 0 && sIdx < second.Length && !float.IsNaN(second[sIdx]))
                        lines.Add(($"{page.SecondLabel}: {(page.SecondIsTemp ? Fmt.Temp(second[sIdx]) : page.MainFormat(second[sIdx]))}",
                                   page.SecondIsTemp ? TempLine : Theme.Ram));
                }
                int bw = lines.Max(l => Gfx.TextWidth(g, l.Item1, _f.Small)) + S(20), bh = lines.Count * S(18) + S(12);
                int bx = px + S(12) + bw > plot.Right ? (int)px - bw - S(12) : (int)px + S(12);
                var box = new Rectangle(bx, plot.Top + S(6), bw, bh);
                Gfx.FillRounded(g, box, S(6), Theme.Tooltip);
                for (int i = 0; i < lines.Count; i++)
                    Gfx.Text(g, lines[i].Item1, _f.Small, lines[i].Item2, new Rectangle(box.Left + S(10), box.Top + S(6) + i * S(18), bw, S(18)));
            }
        }
        return card.Bottom;
    }

    int DrawLegend(Graphics g, string label, Color color, int x, int y)
    {
        using (var b = new SolidBrush(color)) g.FillRectangle(b, x, y + S(8), S(12), S(3));
        Gfx.Text(g, label, _f.Small, Theme.TextDim, new Rectangle(x + S(18), y, S(200), S(18)));
        return x + S(18) + Gfx.TextWidth(g, label, _f.Small);
    }

    void DrawSeries(Graphics g, Rectangle plot, float[] values, int samples, float max, Color color, bool fill)
    {
        if (values.Length < 2 || max <= 0) return;
        float step = plot.Width / (float)(samples - 1);
        var segment = new List<PointF>();
        void Flush()
        {
            if (segment.Count >= 2)
            {
                if (fill)
                {
                    using var path = new GraphicsPath();
                    path.AddLines(segment.ToArray());
                    path.AddLine(segment[^1], new PointF(segment[^1].X, plot.Bottom));
                    path.AddLine(new PointF(segment[^1].X, plot.Bottom), new PointF(segment[0].X, plot.Bottom));
                    path.CloseFigure();
                    using var brush = new LinearGradientBrush(plot, Theme.WithAlpha(color, 90), Theme.WithAlpha(color, 6), LinearGradientMode.Vertical);
                    g.FillPath(brush, path);
                }
                using var pen = new Pen(color, Math.Max(1.5f, S(1.6f))) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, segment.ToArray());
            }
            segment.Clear();
        }
        for (int i = 0; i < values.Length; i++)
        {
            if (float.IsNaN(values[i]))
            {
                Flush(); // a gap (e.g. sensor unavailable) rather than a fake zero
                continue;
            }
            float px = plot.Right - (values.Length - 1 - i) * step;
            float py = plot.Bottom - Math.Clamp(values[i] / max, 0, 1) * plot.Height;
            segment.Add(new PointF(px, py));
        }
        Flush();
    }

    static float MaxOf(IEnumerable<float> values)
    {
        float m = 0;
        foreach (var v in values) if (!float.IsNaN(v)) m = Math.Max(m, v);
        return m;
    }

    /// <summary>A "round" axis maximum above the data (1, 2, 5 × 10^n).</summary>
    static float NiceMax(IEnumerable<float> values)
    {
        float m = Math.Max(MaxOf(values) * 1.1f, 1);
        float pow = (float)Math.Pow(10, Math.Floor(Math.Log10(m)));
        foreach (var f in new[] { 1f, 2f, 2.5f, 5f, 10f })
            if (f * pow >= m) return f * pow;
        return 10 * pow;
    }

    static string FormatRange(int seconds) => seconds switch
    {
        >= 3600 => $"{seconds / 3600} h{(seconds % 3600 / 60 != 0 ? $" {seconds % 3600 / 60} min" : "")}",
        >= 60 => $"{seconds / 60} min{(seconds % 60 != 0 ? $" {seconds % 60} s" : "")}",
        _ => $"{seconds} s",
    };

    int DrawStatGrid(Graphics g, Page page, int x, int y, int w)
    {
        var rows = new List<DetailRow>(page.View!.Rows);
        if (page.Key == "cpu" && _procs.Processes > 0)
        {
            rows.Add(new("Processes", $"{_procs.Processes:N0}"));
            rows.Add(new("Threads", $"{_procs.Threads:N0}"));
            rows.Add(new("Handles", $"{_procs.Handles:N0}"));
            rows.Add(new("Up time", Fmt.Duration(Environment.TickCount64 / 1000f)));
        }
        if (rows.Count == 0) return y;

        int cols = w > S(600) ? 4 : 3, gap = S(8);
        int tileW = (w - gap * (cols - 1)) / cols, tileH = S(60);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = new Rectangle(x + (i % cols) * (tileW + gap), y + (i / cols) * (tileH + gap), tileW, tileH);
            if (r.Bottom < _viewport.Top || r.Top > _viewport.Bottom) continue;
            Gfx.FillRounded(g, r, S(8), Theme.Surface);
            Gfx.Text(g, rows[i].Label, _f.Small, Theme.TextDim, new Rectangle(r.Left + S(12), r.Top + S(9), r.Width - S(20), S(17)), TextFormatFlags.EndEllipsis);
            var font = Gfx.TextWidth(g, rows[i].Value, _f.Mid) <= r.Width - S(24) ? _f.Mid : _f.BodyBold;
            Gfx.Text(g, rows[i].Value, font, rows[i].Color ?? Theme.Text, new Rectangle(r.Left + S(12), r.Top + S(28), r.Width - S(20), S(24)),
                     TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }
        int lines = (rows.Count + cols - 1) / cols;
        return y + lines * tileH + (lines - 1) * gap;
    }

    int DrawThreadBars(Graphics g, List<SensorReading> threads, Color color, int x, int y, int w)
    {
        var card = new Rectangle(x, y, w, S(118));
        if (card.Bottom < _viewport.Top || card.Top > _viewport.Bottom) return card.Bottom;
        Gfx.FillRounded(g, card, S(10), Theme.Surface);
        Gfx.Text(g, $"PER-THREAD USAGE  ·  {threads.Count} THREADS", _f.Label, Theme.TextFaint, new Rectangle(x + S(16), y + S(14), w, S(16)));
        var area = new Rectangle(x + S(16), y + S(40), w - S(32), S(52));
        int barGap = S(4);
        float barW = (area.Width - barGap * (threads.Count - 1)) / (float)threads.Count;
        using var fill = new SolidBrush(color);
        for (int i = 0; i < threads.Count; i++)
        {
            var bx = area.Left + i * (barW + barGap);
            var track = new Rectangle((int)bx, area.Top, Math.Max(1, (int)barW), area.Height);
            Gfx.FillRounded(g, track, S(3), Theme.Track);
            float pct = Math.Clamp((threads[i].Value ?? 0) / 100f, 0, 1);
            int h = (int)Math.Round(pct * area.Height);
            if (h > 0)
            {
                var bar = new Rectangle(track.Left, track.Bottom - h, track.Width, h);
                if (h >= S(6)) Gfx.FillRounded(g, bar, S(3), Theme.WithAlpha(color, 90 + (int)(165 * pct)));
                else g.FillRectangle(fill, bar);
            }
            if (track.Contains(_mouse))
                Gfx.Text(g, $"Thread {i + 1}: {threads[i].Value:0}%", _f.Small, Theme.Text,
                         new Rectangle(x + S(16), area.Bottom + S(4), w, S(18)));
        }
        return card.Bottom;
    }

    int DrawProcesses(Graphics g, ProcessSort sort, int x, int y, int w, int count)
    {
        var list = (sort switch
        {
            ProcessSort.Memory => _procs.TopByMemory(count),
            ProcessSort.Gpu => _procs.TopByGpu(count),
            _ => _procs.TopByCpu(count),
        }).ToList();
        bool showGpu = _procs.HasGpu;
        int rowH = S(26);
        var card = new Rectangle(x, y, w, S(70) + Math.Max(1, list.Count) * rowH);
        if (card.Bottom < _viewport.Top || card.Top > _viewport.Bottom) return card.Bottom;
        Gfx.FillRounded(g, card, S(10), Theme.Surface);
        var title = sort switch { ProcessSort.Memory => "TOP PROCESSES BY MEMORY", ProcessSort.Gpu => "TOP PROCESSES BY GPU", _ => "TOP PROCESSES BY CPU" };
        Gfx.Text(g, title, _f.Label, Theme.TextFaint, new Rectangle(x + S(16), y + S(14), w, S(16)));

        // Columns, right to left: memory, CPU, GPU (when per-process GPU data is available).
        int memRight = x + w - S(16), cpuRight = memRight - S(110), gpuRight = cpuRight - S(90);
        int nameRight = (showGpu ? gpuRight : cpuRight) - S(90);
        int hy = y + S(38);
        Gfx.Text(g, "NAME", _f.Label, Theme.TextFaint, new Rectangle(x + S(16), hy, S(200), S(16)));
        if (showGpu) Gfx.Text(g, "GPU", _f.Label, Theme.TextFaint, new Rectangle(gpuRight - S(80), hy, S(80), S(16)), TextFormatFlags.Right);
        Gfx.Text(g, "CPU", _f.Label, Theme.TextFaint, new Rectangle(cpuRight - S(80), hy, S(80), S(16)), TextFormatFlags.Right);
        Gfx.Text(g, "MEMORY", _f.Label, Theme.TextFaint, new Rectangle(memRight - S(100), hy, S(100), S(16)), TextFormatFlags.Right);

        int ry = y + S(58);
        if (list.Count == 0)
            Gfx.Text(g, "Collecting…", _f.Body, Theme.TextDim, new Rectangle(x + S(16), ry, w, rowH), TextFormatFlags.VerticalCenter);
        float maxCpu = Math.Max(1, list.Max(p => (float?)p.CpuPercent) ?? 1);
        float maxGpu = Math.Max(1, list.Max(p => (float?)p.GpuPercent) ?? 1);
        long maxMem = Math.Max(1, list.Max(p => (long?)p.PrivateBytes) ?? 1);
        foreach (var p in list)
        {
            // Subtle bar showing the share of the leading resource.
            float frac = sort switch
            {
                ProcessSort.Memory => p.PrivateBytes / (float)maxMem,
                ProcessSort.Gpu => p.GpuPercent / maxGpu,
                _ => p.CpuPercent / maxCpu,
            };
            var barColor = sort switch { ProcessSort.Memory => Theme.Ram, ProcessSort.Gpu => Theme.Gpu, _ => Theme.Cpu };
            var bar = new Rectangle(x + S(8), ry + S(2), (int)((w - S(16)) * Math.Clamp(frac, 0, 1)), rowH - S(4));
            if (bar.Width > S(8)) Gfx.FillRounded(g, bar, S(5), Theme.WithAlpha(barColor, 24));
            var name = p.Count > 1 ? $"{p.Name}  ({p.Count})" : p.Name;
            Gfx.Text(g, name, _f.Body, Theme.Text, new Rectangle(x + S(16), ry, nameRight - x - S(16), rowH), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (showGpu)
                Gfx.Text(g, $"{p.GpuPercent:0.0}%", _f.BodyBold, p.GpuPercent >= 10 ? Theme.Warn : Theme.Text,
                         new Rectangle(gpuRight - S(80), ry, S(80), rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Gfx.Text(g, $"{p.CpuPercent:0.0}%", _f.BodyBold, p.CpuPercent >= 10 ? Theme.Warn : Theme.Text,
                     new Rectangle(cpuRight - S(80), ry, S(80), rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Gfx.Text(g, FormatBytes(p.PrivateBytes), _f.BodyBold, Theme.Text,
                     new Rectangle(memRight - S(100), ry, S(100), rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            ry += rowH;
        }
        return card.Bottom;
    }

    static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";

    // ---- Sensor sections --------------------------------------------------------------------

    /// <summary>Sensors most people never need: per-engine D3D load, connector pins, SPD timings, limits.</summary>
    static bool IsAdvanced(SensorReading s) =>
        s.Type is SensorType.Timing or SensorType.Factor ||
        s.Name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase) ||
        s.Name.Contains("Pin ", StringComparison.OrdinalIgnoreCase) ||
        (s.Type == SensorType.Temperature && (s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
                                              s.Name.Contains("Resolution", StringComparison.OrdinalIgnoreCase) ||
                                              s.Name.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) ||
                                              s.Name.StartsWith("Critical", StringComparison.OrdinalIgnoreCase)));

    int DrawSection(Graphics g, HardwareReading hw, int x, int y, int w)
    {
        bool collapsed = _settings.Collapsed.Contains(hw.Id);
        var sensors = (_settings.ShowAdvancedSensors ? hw.Sensors : hw.Sensors.Where(s => !IsAdvanced(s)))
            // Per-thread load is shown as the bar graph above, not 16+ rows.
            .Where(s => !(hw.Type == HardwareType.Cpu && s.Type == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (sensors.Count == 0) return y - S(8);
        var groups = sensors
            .GroupBy(s => s.Type)
            .OrderBy(gr => Array.IndexOf(GroupOrder, gr.Key) is var i && i >= 0 ? i : GroupOrder.Length)
            .ToList();

        int headH = S(42), groupH = S(26), rowH = S(22);
        int bodyH = collapsed ? 0 : groups.Sum(gr => groupH + gr.Count() * rowH) + S(8);
        var card = new Rectangle(x, y, w, headH + bodyH);
        if (card.Bottom < _viewport.Top || card.Top > _viewport.Bottom) return card.Bottom;

        var gpu = _snap!.Summary.Gpus.FirstOrDefault(gs => gs.Id == hw.Id);
        var color = gpu is { Integrated: true } ? Theme.IGpu : Theme.ForHardware(hw.Type);
        Gfx.FillRounded(g, card, S(10), Theme.Surface);

        var head = new Rectangle(x, y, w, headH);
        if (head.Contains(_mouse))
        {
            using var path = Gfx.RoundedPath(head, S(10), roundBottom: collapsed);
            using var hover = new SolidBrush(Theme.SurfaceHover);
            g.FillPath(hover, path);
        }
        AddHit(head, $"{(collapsed ? "Expand" : "Collapse")} {gpu?.ShortName ?? hw.Name} sensors", () =>
        {
            if (!_settings.Collapsed.Remove(hw.Id)) _settings.Collapsed.Add(hw.Id);
            _settings.Save();
            Invalidate();
        });

        DrawChevron(g, new Point(x + S(16), y + headH / 2), collapsed);
        int bx = DrawPill(g, Theme.Badge(hw.Type), color, x + S(28), y, headH, filled: true) + S(8);
        Gfx.Text(g, gpu?.ShortName ?? hw.Name, _f.Body, Theme.Text, new Rectangle(bx, y, x + w - S(120) - bx, headH),
                 TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Gfx.Text(g, $"{sensors.Count} sensors", _f.Small, Theme.TextFaint, new Rectangle(x, y, w - S(16), headH),
                 TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (collapsed) return card.Bottom;

        // Columns: name | min | max | now
        int valueRight = x + w - S(14), valueW = S(96);
        int maxRight = valueRight - valueW - S(6), colW = S(62);
        int minRight = maxRight - colW;
        int nameX = x + S(16), nameW = minRight - colW - S(8) - nameX;

        int ry = y + headH;
        using (var sep = new Pen(Theme.Border)) g.DrawLine(sep, x + S(12), ry, x + w - S(12), ry);
        ry += S(4);

        foreach (var group in groups)
        {
            if (ry + groupH >= _viewport.Top && ry <= _viewport.Bottom)
            {
                Gfx.Text(g, GroupName(group.Key).ToUpperInvariant(), _f.Label, Theme.TextFaint, new Rectangle(nameX, ry + S(8), nameW, S(16)));
                Gfx.Text(g, "MIN", _f.Label, Theme.TextFaint, new Rectangle(minRight - colW, ry + S(8), colW, S(16)), TextFormatFlags.Right);
                Gfx.Text(g, "MAX", _f.Label, Theme.TextFaint, new Rectangle(maxRight - colW, ry + S(8), colW, S(16)), TextFormatFlags.Right);
            }
            ry += groupH;

            foreach (var sensor in group)
            {
                if (ry + rowH >= _viewport.Top && ry <= _viewport.Bottom)
                {
                    var row = new Rectangle(x + S(8), ry, w - S(16), rowH);
                    if (row.Contains(_mouse)) Gfx.FillRounded(g, row, S(5), Theme.SurfaceHover);
                    if (sensor.Type is SensorType.Load or SensorType.Control or SensorType.Level && sensor.Value is { } pct)
                    {
                        var bar = new Rectangle(row.Left, row.Top + S(2), (int)(row.Width * Math.Clamp(pct / 100f, 0, 1)), row.Height - S(4));
                        if (bar.Width > S(8)) Gfx.FillRounded(g, bar, S(5), Theme.WithAlpha(color, 26));
                    }
                    var valueColor = sensor.Type == SensorType.Temperature ? Theme.TempColor(sensor.Value) : Theme.Text;
                    Gfx.Text(g, sensor.Name, _f.Body, Theme.TextDim, new Rectangle(nameX, ry, nameW, rowH),
                             TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    Gfx.Text(g, Fmt.Compact(sensor.Type, sensor.Min), _f.Small, Theme.TextFaint,
                             new Rectangle(minRight - colW, ry, colW, rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    Gfx.Text(g, Fmt.Compact(sensor.Type, sensor.Max), _f.Small, Theme.TextFaint,
                             new Rectangle(maxRight - colW, ry, colW, rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    Gfx.Text(g, Fmt.Sensor(sensor.Type, sensor.Value), _f.BodyBold, valueColor,
                             new Rectangle(valueRight - valueW, ry, valueW, rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                ry += rowH;
            }
        }
        return card.Bottom;
    }

    int DrawPill(Graphics g, string text, Color color, int x, int y, int rowH, bool filled)
    {
        int w = Gfx.TextWidth(g, text, _f.Label) + S(12);
        var r = new Rectangle(x, y + (rowH - S(18)) / 2, w, S(18));
        if (filled)
        {
            Gfx.FillRounded(g, r, S(9), Theme.WithAlpha(color, 38));
        }
        else
        {
            using var path = Gfx.RoundedPath(Rectangle.Inflate(r, -1, -1), S(8));
            using var pen = new Pen(Theme.WithAlpha(color, 120));
            g.DrawPath(pen, path);
        }
        Gfx.Text(g, text, _f.Label, color, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return r.Right;
    }

    static string GroupName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Usage",
        SensorType.Power => "Power",
        SensorType.Clock => "Clocks",
        SensorType.Voltage => "Voltages",
        SensorType.Current => "Currents",
        SensorType.Fan => "Fans",
        SensorType.Control => "Fan control",
        SensorType.SmallData => "Memory",
        SensorType.Data => "Data",
        SensorType.Throughput => "Throughput",
        SensorType.Level => "Levels",
        SensorType.Factor => "Factors",
        SensorType.TimeSpan => "Time",
        SensorType.Timing => "Timings",
        SensorType.Frequency => "Frequencies",
        _ => t.ToString(),
    };

    void DrawChevron(Graphics g, Point c, bool collapsed)
    {
        int a = S(4);
        var pts = collapsed
            ? new[] { new Point(c.X - a / 2, c.Y - a), new Point(c.X + a / 2 + 1, c.Y), new Point(c.X - a / 2, c.Y + a) }
            : new[] { new Point(c.X - a, c.Y - a / 2), new Point(c.X, c.Y + a / 2 + 1), new Point(c.X + a, c.Y - a / 2) };
        using var pen = new Pen(Theme.TextDim, Math.Max(1.5f, S(1.6f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    void DrawScrollbar(Graphics g)
    {
        _thumb = Rectangle.Empty;
        int view = _viewport.Height;
        if (_contentHeight <= view || view <= 0) return;
        int thumbH = Math.Max(S(36), view * view / _contentHeight);
        float maxScroll = _contentHeight - view;
        int thumbY = _viewport.Top + (int)((view - thumbH) * (_scroll / maxScroll));
        _thumb = new Rectangle(ClientSize.Width - S(8), thumbY, S(4), thumbH);
        bool active = _draggingThumb || new Rectangle(_thumb.Left - S(6), _viewport.Top, S(14), view).Contains(_mouse);
        Gfx.FillRounded(g, _thumb, S(2), active ? Theme.TextFaint : Theme.Border);
    }

    void AddHit(Rectangle r, Action action, string name)
    {
        // Only register what is visible, so content scrolled under the header can't be clicked.
        var visible = Rectangle.Intersect(r, _viewport);
        if (!visible.IsEmpty) _hits.Add((visible, action, name));
    }

    void AddHit(Rectangle r, string name, Action action) => AddHit(r, action, name);

    // ---- Keyboard & screen readers -----------------------------------------------------------
    // Everything clickable is a "hit" (rail entries, tiles, buttons, chips, section headers). Tab /
    // Shift+Tab move a focus ring through them, Enter/Space activates, and each is exposed to
    // screen readers as a named button.

    int _focus = -1;
    bool _keyboardFocus;
    string _a11ySummary = "";

    void MoveFocus(int delta)
    {
        if (_hits.Count == 0) return;
        _keyboardFocus = true;
        _focus = _focus < 0 ? (delta > 0 ? 0 : _hits.Count - 1) : (_focus + delta + _hits.Count) % _hits.Count;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, _focus);
    }

    void DrawFocusRing(Graphics g)
    {
        if (!_keyboardFocus || !Focused || _focus < 0 || _focus >= _hits.Count) return;
        var r = Rectangle.Inflate(_hits[_focus].Rect, S(2), S(2));
        using var path = Gfx.RoundedPath(r, S(8));
        using var pen = new Pen(Theme.Cpu, Math.Max(2, S(2)));
        g.DrawPath(pen, path);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new DashboardAccessible(this);

    sealed class DashboardAccessible : ControlAccessibleObject
    {
        readonly DashboardView _view;

        public DashboardAccessible(DashboardView view) : base(view) => _view = view;

        public override AccessibleRole Role => AccessibleRole.Pane;
        public override string? Name => "PC Vitals dashboard";
        public override string? Description => _view._a11ySummary;
        public override int GetChildCount() => _view._hits.Count;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < _view._hits.Count ? new HitAccessible(_view, index, this) : null;
        public override AccessibleObject? GetFocused() =>
            _view._focus >= 0 && _view._focus < _view._hits.Count ? GetChild(_view._focus) : base.GetFocused();
    }

    sealed class HitAccessible : AccessibleObject
    {
        readonly DashboardView _view;
        readonly int _index;
        readonly AccessibleObject _parent;

        public HitAccessible(DashboardView view, int index, AccessibleObject parent)
        {
            _view = view;
            _index = index;
            _parent = parent;
        }

        (Rectangle Rect, Action Action, string Name)? Hit => _index < _view._hits.Count ? _view._hits[_index] : null;
        public override string? Name => Hit?.Name;
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override AccessibleObject? Parent => _parent;
        public override string DefaultAction => "Press";
        public override Rectangle Bounds => Hit is { } h ? _view.RectangleToScreen(h.Rect) : Rectangle.Empty;
        public override AccessibleStates State =>
            AccessibleStates.Focusable | (_view._focus == _index ? AccessibleStates.Focused : AccessibleStates.None);

        public override void DoDefaultAction()
        {
            if (Hit is { } h) _view.BeginInvoke(h.Action);
        }
    }

    // ---- Input ------------------------------------------------------------------------------

    bool ClampScroll()
    {
        float max = Math.Max(0, _contentHeight - _viewport.Height);
        float clamped = Math.Clamp(_scroll, 0, max);
        if (Math.Abs(clamped - _scroll) < 0.5f) return false;
        _scroll = clamped;
        return true;
    }

    void ScrollBy(float delta)
    {
        _scroll += delta;
        ClampScroll();
        Invalidate();
    }

    void CyclePage(int delta)
    {
        if (_snap == null) return;
        var pages = BuildPages(_snap);
        int i = pages.FindIndex(p => p.Key == _settings.DashboardPage);
        Navigate(pages[(Math.Max(0, i) + delta + pages.Count) % pages.Count].Key);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-e.Delta / 120f * S(64));
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Tab
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Tab)
        {
            MoveFocus(e.Shift ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.Enter or Keys.Space && _keyboardFocus && _focus >= 0 && _focus < _hits.Count)
        {
            _hits[_focus].Action();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode is Keys.Up or Keys.Down)
        {
            CyclePage(e.KeyCode == Keys.Down ? 1 : -1);
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Up: ScrollBy(-S(40)); break;
            case Keys.Down: ScrollBy(S(40)); break;
            case Keys.PageUp: ScrollBy(-_viewport.Height * 0.9f); break;
            case Keys.PageDown: ScrollBy(_viewport.Height * 0.9f); break;
            case Keys.Home: ScrollBy(-_contentHeight); break;
            case Keys.End: ScrollBy(_contentHeight); break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _keyboardFocus = false; // the focus ring is for keyboard users; hide it once the mouse is used
        if (e.Button != MouseButtons.Left) return;

        if (!_thumb.IsEmpty && e.X >= ClientSize.Width - S(14) && e.Y >= _viewport.Top)
        {
            if (!_thumb.Contains(new Point(_thumb.X, e.Y)))
            {
                float ratio = (e.Y - _viewport.Top - _thumb.Height / 2f) / Math.Max(1, _viewport.Height - _thumb.Height);
                _scroll = ratio * (_contentHeight - _viewport.Height);
                ClampScroll();
            }
            _draggingThumb = true;
            _dragStartY = e.Y;
            _dragStartScroll = _scroll;
            Capture = true;
            Invalidate();
            return;
        }

        // Dragging the header (outside its buttons) moves the window, handy when pinned.
        if (e.Y < _viewport.Top && !_hits.Any(h => h.Rect.Contains(e.Location)) && FindForm() is { } form)
        {
            ReleaseCapture();
            SendMessage(form.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var before = _mouse;
        _mouse = e.Location;
        if (_draggingThumb)
        {
            float track = Math.Max(1, _viewport.Height - _thumb.Height);
            _scroll = _dragStartScroll + (e.Y - _dragStartY) * (_contentHeight - _viewport.Height) / track;
            ClampScroll();
            Invalidate();
            return;
        }
        bool hitNow = _hits.Any(h => h.Rect.Contains(e.Location));
        Cursor = hitNow ? Cursors.Hand : Cursors.Default;
        // Repaint only when hover feedback can change, not on every pixel of movement.
        if (hitNow || _hits.Any(h => h.Rect.Contains(before)) || _plot.Contains(e.Location) || _plot.Contains(before) ||
            e.X >= ClientSize.Width - S(16) || _viewport.Contains(e.Location))
            Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_draggingThumb)
        {
            _draggingThumb = false;
            Capture = false;
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        foreach (var (rect, action, _) in _hits.ToList())
        {
            if (!rect.Contains(e.Location)) continue;
            action();
            break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
