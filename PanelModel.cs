using System.Globalization;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;

namespace PCVitals;

public enum PanelMode
{
    Normal,
    AlwaysOnTop,
    /// <summary>Behind every window, like part of the wallpaper (and back in view after "show desktop").</summary>
    Desktop,
}

public enum PanelTileKind
{
    /// <summary>A reading: a headline alias (<see cref="PanelTile.Source"/>) or any sensor (<see cref="PanelTile.Sensor"/>).</summary>
    Value,
    /// <summary>Text, with readings filled in for placeholders such as <c>{cputemp}</c> (see <see cref="PanelText"/>).</summary>
    Text,
    Clock,
    Divider,
    /// <summary>A picture (<see cref="PanelTile.Image"/>).</summary>
    Image,
}

/// <summary>What a value tile draws; any combination.</summary>
[Flags]
public enum PanelVisual
{
    None = 0,
    Label = 1,
    Number = 2,
    Bar = 4,
    Ring = 8,
    Graph = 16,
}

public enum PanelBarDirection
{
    Horizontal,
    Vertical,
}

public enum PanelGaugeStyle
{
    /// <summary>Three-quarter ring open at the bottom.</summary>
    Ring,
    Semicircle,
    Circle,
    /// <summary>A three-quarter ring of separate segments.</summary>
    Segmented,
}

public enum PanelBarStyle
{
    Solid,
    /// <summary>Separate blocks, like an LED meter.</summary>
    Segmented,
}

/// <summary>How a background picture covers its area.</summary>
public enum PanelImageFit
{
    /// <summary>Covers the area, cropping what doesn't fit.</summary>
    Fill,
    /// <summary>Shows all of it, leaving bands.</summary>
    Fit,
    Stretch,
    Tile,
    Center,
}

/// <summary>From <see cref="Value"/> upwards (in the reading's own unit, °C for temperatures) the tile turns <see cref="Color"/>.</summary>
public sealed class PanelThreshold
{
    public float Value { get; set; }
    /// <summary>"#RRGGBB", or "warn" / "hot" for the theme's amber and red.</summary>
    public string Color { get; set; } = "warn";
}

/// <summary>Panel colours. Null means "follow the app theme".</summary>
public sealed class PanelStyle
{
    public string? Background { get; set; }
    /// <summary>0 (invisible) to 100 (solid). Text and graphs stay opaque.</summary>
    public int BackgroundOpacity { get; set; } = 88;
    /// <summary>Each tile's own backdrop; null for the theme's card colour.</summary>
    public string? TileBackground { get; set; }
    public bool TileBackgrounds { get; set; } = true;
    public int CornerRadius { get; set; } = 10;
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>A picture behind the tiles: file name in the panels folder (<see cref="PanelImages"/>).</summary>
    public string? BackgroundImage { get; set; }
    public PanelImageFit BackgroundFit { get; set; }
    public string? LabelColor { get; set; }
    public string? ValueColor { get; set; }
    /// <summary>Bars, rings and graphs; null for each device's own colour (CPU blue, GPU green…).</summary>
    public string? AccentColor { get; set; }

    public PanelStyle Clone() => (PanelStyle)MemberwiseClone();
}

public sealed class PanelTile
{
    public PanelTileKind Kind { get; set; }
    public int Column { get; set; }
    public int Row { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    /// <summary>Headline reading, e.g. "cpu", "gputemp", "disk:/nvme/0" (see <see cref="PanelSources"/>).</summary>
    public string? Source { get; set; }
    /// <summary>Any sensor instead of a headline reading.</summary>
    public SensorKey? Sensor { get; set; }

    /// <summary>Free placement (<see cref="PanelLayout.FreePlacement"/>): position and size at 100 % and 96 DPI.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 120;
    public int Height { get; set; } = 64;

    public PanelVisual Visuals { get; set; } = PanelVisual.Label | PanelVisual.Number;
    public PanelBarDirection BarDirection { get; set; }
    public PanelBarStyle BarStyle { get; set; }
    public PanelGaugeStyle GaugeStyle { get; set; }
    /// <summary>Null for the panel's font.</summary>
    public string? FontFamily { get; set; }
    /// <summary>Image tiles: file name in the panels folder (<see cref="PanelImages"/>).</summary>
    public string? Image { get; set; }
    public PanelImageFit ImageFit { get; set; } = PanelImageFit.Fit;
    public int GraphSeconds { get; set; } = 60;
    /// <summary>Decimal places; null for the reading's usual format.</summary>
    public int? Decimals { get; set; }
    public bool ShowUnit { get; set; } = true;
    /// <summary>Text size in percent of the automatic size.</summary>
    public int FontSize { get; set; } = 100;
    /// <summary>Null for the reading's default label; empty for none.</summary>
    public string? Label { get; set; }
    /// <summary>Text tiles: the text. Clock tiles: a .NET date format ("HH:mm", "ddd d MMM").</summary>
    public string? Text { get; set; }
    /// <summary>Null for the defaults (temperatures turn amber at 65 °C and red at 82 °C); empty for none.</summary>
    public List<PanelThreshold>? Thresholds { get; set; }
    /// <summary>Bar and ring scale; null for automatic (100 for percentages and temperatures).</summary>
    public float? Max { get; set; }
    public string? ValueColor { get; set; }
    public string? AccentColor { get; set; }

    [JsonIgnore] public Rectangle Cells => new(Column, Row, Math.Max(1, ColumnSpan), Math.Max(1, RowSpan));

    public PanelTile Clone()
    {
        var t = (PanelTile)MemberwiseClone();
        t.Thresholds = Thresholds?.Select(x => new PanelThreshold { Value = x.Value, Color = x.Color }).ToList();
        return t;
    }
}

/// <summary>A floating panel of tiles on a grid.</summary>
public sealed class PanelLayout
{
    /// <summary>Tells panels apart (menus, the editor); stays the same when the panel is renamed.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Panel";
    /// <summary>The preset it was made from, for the gallery's selection.</summary>
    public string? Preset { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Monitor device name ("\\.\DISPLAY1") and its size, so the panel finds its monitor again after they are reordered.</summary>
    public string? Monitor { get; set; }
    public Size MonitorSize { get; set; }
    /// <summary>Top-left corner relative to the monitor, in physical pixels; null to place it automatically.</summary>
    public Point? Position { get; set; }

    public PanelMode Mode { get; set; } = PanelMode.Desktop;
    /// <summary>Clicks go through to the windows underneath.</summary>
    public bool Locked { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Covers the whole monitor (for a small screen in or on the case).</summary>
    public bool FillDisplay { get; set; }
    /// <summary>Size in percent, on top of the monitor's scaling.</summary>
    public int Scale { get; set; } = 100;

    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 2;
    /// <summary>Cell size at 100 % and 96 DPI.</summary>
    public int CellWidth { get; set; } = 120;
    public int CellHeight { get; set; } = 64;
    public int Gap { get; set; } = 6;
    public int Padding { get; set; } = 8;

    /// <summary>Tiles are placed anywhere by <see cref="PanelTile.X"/>/<see cref="PanelTile.Y"/> (later tiles on top) instead of on the grid.</summary>
    public bool FreePlacement { get; set; }
    /// <summary>Free placement: the panel's size at 100 % and 96 DPI.</summary>
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 240;

    public PanelStyle Style { get; set; } = new();
    public List<PanelTile> Tiles { get; set; } = new();

    public PanelLayout Clone()
    {
        var l = (PanelLayout)MemberwiseClone();
        l.Style = Style.Clone();
        l.Tiles = Tiles.Select(t => t.Clone()).ToList();
        return l;
    }

    /// <summary>Copies the tiles, grid and look of <paramref name="preset"/>, keeping where and how the window shows.</summary>
    public void ApplyPreset(PanelLayout preset)
    {
        Name = preset.Name;
        Preset = preset.Preset;
        Columns = preset.Columns;
        Rows = preset.Rows;
        CellWidth = preset.CellWidth;
        CellHeight = preset.CellHeight;
        Gap = preset.Gap;
        Padding = preset.Padding;
        FreePlacement = preset.FreePlacement;
        Width = preset.Width;
        Height = preset.Height;
        Style = preset.Style.Clone();
        Tiles = preset.Tiles.Select(t => t.Clone()).ToList();
    }
}

/// <summary>
/// Layout maths: where tiles are in pixels, whether they fit, and the moves the editor makes. Pure, so it is
/// unit-tested. Grid panels place tiles in cells; free-placement panels by position and size.
/// </summary>
static class PanelGrid
{
    /// <summary>The smallest tile in free placement, at 100 %.</summary>
    public const int MinFreeSize = 16;
    public const int MaxColumns = 12, MaxRows = 24;

    /// <summary>The panel's size in pixels at <paramref name="scale"/>.</summary>
    public static Size PanelSize(PanelLayout l, float scale) => l.FreePlacement
        ? new(Px(Math.Max(MinFreeSize, l.Width), scale), Px(Math.Max(MinFreeSize, l.Height), scale))
        : new(Px(l.Padding * 2 + l.Columns * l.CellWidth + Math.Max(0, l.Columns - 1) * l.Gap, scale),
              Px(l.Padding * 2 + l.Rows * l.CellHeight + Math.Max(0, l.Rows - 1) * l.Gap, scale));

    /// <summary>
    /// A block of cells in pixels. Edges are rounded from unscaled positions (not added up from rounded
    /// cell sizes), so the gaps stay even at any scale.
    /// </summary>
    public static Rectangle CellRect(PanelLayout l, Rectangle cells, float scale)
    {
        int Left(int col) => l.Padding + col * (l.CellWidth + l.Gap);
        int Top(int row) => l.Padding + row * (l.CellHeight + l.Gap);
        int x0 = Px(Left(cells.Left), scale), y0 = Px(Top(cells.Top), scale);
        int x1 = Px(Left(cells.Right - 1) + l.CellWidth, scale), y1 = Px(Top(cells.Bottom - 1) + l.CellHeight, scale);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>Where a tile is drawn, in pixels from the panel's top-left corner.</summary>
    public static Rectangle TileRect(PanelLayout l, PanelTile t, float scale) => l.FreePlacement
        ? Rectangle.FromLTRB(Px(t.X, scale), Px(t.Y, scale), Px(t.X + Math.Max(MinFreeSize, t.Width), scale), Px(t.Y + Math.Max(MinFreeSize, t.Height), scale))
        : CellRect(l, t.Cells, scale);

    static int Px(float v, float scale) => (int)Math.Round(v * scale);

    /// <summary>Problems with a layout ("tile 3 is outside the grid"), or none.</summary>
    public static List<string> Problems(PanelLayout l)
    {
        var problems = new List<string>();
        if (l.FreePlacement)
        {
            var bounds = new Rectangle(0, 0, l.Width, l.Height);
            for (int i = 0; i < l.Tiles.Count; i++)
            {
                var t = l.Tiles[i];
                if (t.Width < MinFreeSize || t.Height < MinFreeSize) problems.Add($"tile {i} ({Describe(t)}) is smaller than {MinFreeSize} px");
                if (!bounds.Contains(new Rectangle(t.X, t.Y, t.Width, t.Height))) problems.Add($"tile {i} ({Describe(t)}) is outside the {l.Width}×{l.Height} panel");
            }
            return problems;
        }
        var grid = new Rectangle(0, 0, l.Columns, l.Rows);
        for (int i = 0; i < l.Tiles.Count; i++)
        {
            var a = l.Tiles[i].Cells;
            if (a.Left < 0 || a.Top < 0 || !grid.Contains(a)) problems.Add($"tile {i} ({Describe(l.Tiles[i])}) is outside the {l.Columns}×{l.Rows} grid");
            for (int j = 0; j < i; j++)
                if (a.IntersectsWith(l.Tiles[j].Cells)) problems.Add($"tile {i} ({Describe(l.Tiles[i])}) overlaps tile {j} ({Describe(l.Tiles[j])})");
        }
        return problems;
    }

    static string Describe(PanelTile t) => t.Source ?? t.Sensor?.SensorName ?? t.Kind.ToString();

    /// <summary>
    /// Places tiles in order, each in the first free spot reading left to right, top to bottom, and sets
    /// <see cref="PanelLayout.Rows"/> to what they need. Tiles wider than the grid are narrowed to fit.
    /// </summary>
    public static void Pack(PanelLayout l)
    {
        var taken = new List<Rectangle>();
        foreach (var t in l.Tiles)
        {
            t.ColumnSpan = Math.Clamp(t.ColumnSpan, 1, l.Columns);
            t.RowSpan = Math.Max(1, t.RowSpan);
            var spot = FirstFree(l.Columns, taken, t.ColumnSpan, t.RowSpan);
            (t.Column, t.Row) = (spot.X, spot.Y);
            taken.Add(new Rectangle(spot.X, spot.Y, t.ColumnSpan, t.RowSpan));
        }
        l.Rows = Math.Max(1, taken.Select(r => r.Bottom).DefaultIfEmpty(1).Max());
    }

    /// <summary>The first spot, reading order, where a block of cells fits among <paramref name="taken"/> (rows grow downwards without limit).</summary>
    static Point FirstFree(int columns, IReadOnlyList<Rectangle> taken, int colSpan, int rowSpan)
    {
        colSpan = Math.Clamp(colSpan, 1, columns);
        for (int row = 0; ; row++)
            for (int col = 0; col + colSpan <= columns; col++)
            {
                var spot = new Rectangle(col, row, colSpan, rowSpan);
                if (!taken.Any(r => r.IntersectsWith(spot))) return spot.Location;
            }
    }

    /// <summary>The tile at a point on the panel (the topmost, when tiles overlap), or -1.</summary>
    public static int HitTest(PanelLayout l, Point p, float scale)
    {
        for (int i = l.Tiles.Count - 1; i >= 0; i--)
            if (TileRect(l, l.Tiles[i], scale).Contains(p)) return i;
        return -1;
    }

    /// <summary>Grid panels: the cell under a point (clamped to the grid).</summary>
    public static Point CellAt(PanelLayout l, Point p, float scale)
    {
        float x = p.X / scale - l.Padding, y = p.Y / scale - l.Padding;
        int col = (int)Math.Floor(x / (l.CellWidth + l.Gap)), row = (int)Math.Floor(y / (l.CellHeight + l.Gap));
        return new Point(Math.Clamp(col, 0, l.Columns - 1), Math.Clamp(row, 0, l.Rows - 1));
    }

    /// <summary>Grid panels: whether tile <paramref name="index"/> (or a new tile, with -1) can take <paramref name="cells"/>.</summary>
    public static bool Fits(PanelLayout l, int index, Rectangle cells)
    {
        if (cells.Left < 0 || cells.Top < 0 || cells.Width < 1 || cells.Height < 1 || !new Rectangle(0, 0, l.Columns, l.Rows).Contains(cells)) return false;
        for (int i = 0; i < l.Tiles.Count; i++)
            if (i != index && l.Tiles[i].Cells.IntersectsWith(cells)) return false;
        return true;
    }

    /// <summary>Grid panels: moves and/or resizes a tile when the cells are free; false (and no change) otherwise.</summary>
    public static bool Place(PanelLayout l, int index, Rectangle cells)
    {
        if (!Fits(l, index, cells)) return false;
        var t = l.Tiles[index];
        (t.Column, t.Row, t.ColumnSpan, t.RowSpan) = (cells.X, cells.Y, cells.Width, cells.Height);
        return true;
    }

    /// <summary>
    /// Adds a tile in the first free spot (grid) or below the others (free placement), growing the panel when it
    /// is full. Returns its index.
    /// </summary>
    public static int Add(PanelLayout l, PanelTile tile)
    {
        if (l.FreePlacement)
        {
            int bottom = l.Tiles.Select(t => t.Y + t.Height).DefaultIfEmpty(0).Max();
            tile.X = Math.Clamp(tile.X, 0, Math.Max(0, l.Width - tile.Width));
            tile.Y = l.Tiles.Count == 0 ? 8 : bottom + 8;
            l.Height = Math.Max(l.Height, tile.Y + tile.Height + 8);
            l.Width = Math.Max(l.Width, tile.X + tile.Width);
        }
        else
        {
            tile.ColumnSpan = Math.Clamp(tile.ColumnSpan, 1, l.Columns);
            tile.RowSpan = Math.Max(1, tile.RowSpan);
            var spot = FirstFree(l.Columns, l.Tiles.Select(t => t.Cells).ToList(), tile.ColumnSpan, tile.RowSpan);
            (tile.Column, tile.Row) = (spot.X, spot.Y);
            l.Rows = Math.Min(MaxRows, Math.Max(l.Rows, spot.Y + tile.RowSpan));
        }
        l.Tiles.Add(tile);
        return l.Tiles.Count - 1;
    }

    /// <summary>Grid panels: changes the grid size. Shrinking is refused (false) while a tile would end up outside.</summary>
    public static bool Resize(PanelLayout l, int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, MaxColumns);
        rows = Math.Clamp(rows, 1, MaxRows);
        if (l.Tiles.Any(t => t.Column + t.ColumnSpan > columns || t.Row + t.RowSpan > rows)) return false;
        (l.Columns, l.Rows) = (columns, rows);
        return true;
    }

    /// <summary>Switches between grid and free placement, keeping every tile where it is on screen (at 100 %).</summary>
    public static void SetFreePlacement(PanelLayout l, bool free)
    {
        if (free == l.FreePlacement) return;
        if (free)
        {
            var size = PanelSize(l, 1);
            foreach (var t in l.Tiles)
            {
                var r = CellRect(l, t.Cells, 1);
                (t.X, t.Y, t.Width, t.Height) = (r.X, r.Y, r.Width, r.Height);
            }
            (l.Width, l.Height) = (size.Width, size.Height);
            l.FreePlacement = true;
            return;
        }
        // Back to the grid: each tile takes the cells nearest where it is. If that makes tiles overlap, they are
        // packed in top-to-bottom, left-to-right order instead.
        l.FreePlacement = false;
        int pitchX = l.CellWidth + l.Gap, pitchY = l.CellHeight + l.Gap;
        l.Columns = Math.Clamp((int)Math.Round((l.Width - l.Padding * 2 + l.Gap) / (float)pitchX), 1, MaxColumns);
        foreach (var t in l.Tiles)
        {
            t.ColumnSpan = Math.Clamp((int)Math.Round((t.Width + l.Gap) / (float)pitchX), 1, l.Columns);
            t.RowSpan = Math.Clamp((int)Math.Round((t.Height + l.Gap) / (float)pitchY), 1, MaxRows);
            t.Column = Math.Clamp((int)Math.Round((t.X - l.Padding) / (float)pitchX), 0, l.Columns - t.ColumnSpan);
            t.Row = Math.Clamp((int)Math.Round((t.Y - l.Padding) / (float)pitchY), 0, MaxRows - t.RowSpan);
        }
        l.Rows = Math.Clamp(l.Tiles.Select(t => t.Row + t.RowSpan).DefaultIfEmpty(1).Max(), 1, MaxRows);
        if (Problems(l).Count == 0) return;
        l.Tiles = l.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        Pack(l);
    }

    /// <summary>
    /// Free placement: snaps a moving edge to the nearest guide within <paramref name="threshold"/>. Returns the
    /// snapped value and the guide it snapped to (or null).
    /// </summary>
    public static (int Value, int? Guide) Snap(int value, IEnumerable<int> guides, int threshold)
    {
        int? best = null;
        foreach (var g in guides)
            if (Math.Abs(g - value) <= threshold && (best == null || Math.Abs(g - value) < Math.Abs(best.Value - value))) best = g;
        return (best ?? value, best);
    }

    /// <summary>Free placement: the lines a tile's edges can snap to (panel edges and centre, other tiles' edges).</summary>
    public static (List<int> X, List<int> Y) Guides(PanelLayout l, int except)
    {
        var xs = new List<int> { 0, l.Width, l.Width / 2 };
        var ys = new List<int> { 0, l.Height, l.Height / 2 };
        for (int i = 0; i < l.Tiles.Count; i++)
        {
            if (i == except) continue;
            var t = l.Tiles[i];
            xs.Add(t.X);
            xs.Add(t.X + t.Width);
            ys.Add(t.Y);
            ys.Add(t.Y + t.Height);
        }
        return (xs, ys);
    }
}

/// <summary>"#RRGGBB" colours in settings.</summary>
static class PanelColor
{
    public static Color? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Equals("warn", StringComparison.OrdinalIgnoreCase)) return Theme.Warn;
        if (s.Equals("hot", StringComparison.OrdinalIgnoreCase)) return Theme.Hot;
        if (s.Equals("good", StringComparison.OrdinalIgnoreCase)) return Theme.Good;
        if (s.StartsWith('#')) s = s[1..];
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return s.Length switch
        {
            6 => Color.FromArgb(255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255),
            8 => Color.FromArgb((int)(v >> 24), (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255),
            _ => null,
        };
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The threshold colour a value has reached, or null below all of them.</summary>
    public static Color? ForValue(IReadOnlyList<PanelThreshold> thresholds, float? value)
    {
        if (value is not { } v || float.IsNaN(v)) return null;
        Color? hit = null;
        float best = float.MinValue;
        foreach (var t in thresholds)
            if (v >= t.Value && t.Value >= best)
            {
                best = t.Value;
                hit = Parse(t.Color);
            }
        return hit;
    }

    /// <summary>The theme's temperature steps (amber from 65 °C, red from 82 °C), as on the taskbar.</summary>
    public static List<PanelThreshold> TemperatureDefaults() => new()
    {
        new() { Value = 65, Color = "warn" },
        new() { Value = 82, Color = "hot" },
    };
}

public enum PanelUnit
{
    None,
    Percent,
    Temp,
    Watts,
    Clock,
    Rpm,
    Rate,
    Gigabytes,
    Ms,
    Volts,
}

/// <summary>One reading resolved from a snapshot, ready to draw.</summary>
sealed record PanelReading(
    string Label,
    float? Value,
    PanelUnit Unit,
    Color Accent,
    /// <summary>Key of its history series in <see cref="Snapshot.History"/>, if it has one.</summary>
    string? HistoryKey,
    /// <summary>Bar/ring scale when the tile doesn't set one; null to scale to what has been seen.</summary>
    float? Max,
    /// <summary>The dashboard page for the device.</summary>
    string? Page,
    SensorType? SensorType = null,
    /// <summary>Overrides the formatted number (e.g. "8.1 GB" for VRAM shown as a percentage bar).</summary>
    string? Text = null);

/// <summary>The headline readings a tile can show, and how each is read from a snapshot.</summary>
static class PanelSources
{
    /// <summary>Every headline source with a name for pickers, for the devices in <paramref name="s"/>.</summary>
    public static List<(string Key, string Name)> All(Summary? s)
    {
        var list = new List<(string, string)>
        {
            ("cpu", "CPU usage"), ("cputemp", "CPU temperature"), ("cpupower", "CPU power"), ("cpuclock", "CPU clock"), ("cpufan", "CPU fan"),
            ("gpu", "GPU usage"), ("gputemp", "GPU temperature"), ("gpuhotspot", "GPU hot spot"), ("gpupower", "GPU power"),
            ("gpuclock", "GPU clock"), ("gpuvram", "GPU memory (VRAM)"), ("gpufan", "GPU fan"),
            ("igpu", "Second GPU usage"), ("igputemp", "Second GPU temperature"),
            ("ram", "Memory usage"), ("ramused", "Memory in use (GB)"), ("power", "CPU + GPU power"),
            ("netdown", "Download speed"), ("netup", "Upload speed"), ("ping", "Ping latency"),
            ("chipset", "Chipset temperature"), ("vrm", "VRM temperature"), ("disk", "Hottest drive temperature"),
            ("battery", "Battery charge"),
        };
        foreach (var d in s?.Disks ?? Array.Empty<DiskSummary>())
            list.Add(("disk:" + d.Id, $"{d.Name} temperature"));
        return list;
    }

    /// <summary>
    /// Whether a source needs a device group the background poller only reads on request (drives,
    /// motherboard, second GPU, battery), so the app can ask for it while a panel shows it.
    /// </summary>
    public static (bool Storage, bool Board, bool SecondGpu, bool Battery) Needs(PanelTile tile) => tile.Sensor is { } key
        ? key.HardwareType switch
        {
            HardwareType.Storage => (true, false, false, false),
            HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => (false, true, false, false),
            // It may be the GPU that isn't the primary one.
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => (false, false, true, false),
            HardwareType.Battery => (false, false, false, true),
            _ => default,
        }
        : tile.Source switch
    {
        "disk" => (true, false, false, false),
        { } s when s.StartsWith("disk:") => (true, false, false, false),
        "chipset" or "vrm" or "cpufan" => (false, true, false, false),
        "igpu" or "igputemp" => (false, false, true, false),
        "battery" => (false, false, false, true),
        _ => default,
    };

    public static PanelReading? Read(PanelTile tile, Snapshot snap, AppSettings? settings)
    {
        if (tile.Sensor is { } key) return ReadSensor(key, snap, settings);
        if (tile.Source is not { } src) return null;
        var s = snap.Summary;
        if (src.StartsWith("disk:", StringComparison.Ordinal))
        {
            var id = src[5..];
            var d = s.Disks.FirstOrDefault(x => x.Id == id);
            return new(d?.Name is { } n ? Short(n) : "DRIVE", d?.Temp, PanelUnit.Temp, Theme.Storage, src, 100, "disk:" + id);
        }
        var gpu = src.StartsWith("igpu", StringComparison.Ordinal) ? s.SecondGpu : s.Gpu;
        string? gpuPage = gpu != null ? "gpu:" + gpu.Id : null;
        var gpuAccent = gpu?.Integrated == true ? Theme.IGpu : Theme.Gpu;
        bool gpuAwake = gpu is { Sleeping: false };
        return src switch
        {
            "cpu" => new("CPU", s.CpuLoad, PanelUnit.Percent, Theme.Cpu, "cpu", 100, "cpu"),
            "cputemp" => new("CPU TEMP", s.CpuTemp, PanelUnit.Temp, Theme.Cpu, "cputemp", 100, "cpu"),
            "cpupower" => new("CPU POWER", s.CpuPower, PanelUnit.Watts, Theme.Cpu, "cpupower", null, "cpu"),
            "cpuclock" => new("CPU CLOCK", s.CpuClock, PanelUnit.Clock, Theme.Cpu, null, null, "cpu"),
            "cpufan" => new("CPU FAN", s.CpuFanRpm, PanelUnit.Rpm, Theme.Cpu, null, null, "board"),
            "gpu" or "igpu" => gpu == null ? null : new(src == "igpu" ? "iGPU" : "GPU", gpuAwake ? gpu.Load : null, PanelUnit.Percent, gpuAccent, "gpu:" + gpu.Id, 100, gpuPage),
            "gputemp" or "igputemp" => gpu == null ? null : new(src == "igputemp" ? "iGPU TEMP" : "GPU TEMP", gpu.Temp, PanelUnit.Temp, gpuAccent, "gputemp:" + gpu.Id, 100, gpuPage),
            "gpuhotspot" => gpu?.HotSpot == null ? null : new("HOT SPOT", gpu.HotSpot, PanelUnit.Temp, gpuAccent, null, 110, gpuPage),
            "gpupower" => gpu == null ? null : new("GPU POWER", gpu.Power, PanelUnit.Watts, gpuAccent, "gpupower:" + gpu.Id,
                                                   gpu.Power is { } w && gpu.PowerLimitPct is > 1 and var pct ? w / pct * 100 : null, gpuPage),
            "gpuclock" => gpu == null ? null : new("GPU CLOCK", gpu.CoreClock, PanelUnit.Clock, gpuAccent, null, null, gpuPage),
            "gpuvram" => gpu?.VramTotalMb is not { } total ? null
                : new("VRAM", gpu.VramUsedMb is { } used ? used / total * 100 : null, PanelUnit.Percent, gpuAccent, null, 100, gpuPage,
                      Text: gpu.VramUsedMb is { } u ? $"{u / 1024:0.0} GB" : null),
            "gpufan" => gpu?.FanRpm == null ? null : new("GPU FAN", gpu.FanRpm, PanelUnit.Rpm, gpuAccent, null, null, gpuPage),
            "ram" => s.RamTotalGb == null ? null : new("MEMORY", s.RamLoad, PanelUnit.Percent, Theme.Ram, "ram", 100, "memory"),
            "ramused" => s.RamTotalGb is not { } totalGb ? null : new("MEMORY", s.RamUsedGb, PanelUnit.Gigabytes, Theme.Ram, null, totalGb, "memory"),
            "power" => new("POWER", s.TotalPower, PanelUnit.Watts, Theme.Power, "power", null, "cpu"),
            "netdown" => new("DOWNLOAD", s.NetDown, PanelUnit.Rate, Theme.Net, "netdown", null, "network"),
            "netup" => new("UPLOAD", s.NetUp, PanelUnit.Rate, Theme.Net, "netup", null, "network"),
            "ping" => s.Ping is not { } ping ? null : new("PING", ping.AvgMs, PanelUnit.Ms, Theme.Net, "ping", null, "network"),
            "chipset" => new("CHIPSET", s.ChipsetTemp ?? s.SystemTemp, PanelUnit.Temp, Theme.Board, "chipset", 100, "board"),
            "vrm" => new("VRM", s.VrmTemp, PanelUnit.Temp, Theme.Board, null, 110, "board"),
            "disk" => s.Disks.Count == 0 ? null : new("DRIVES", s.Disks.Select(d => d.Temp).DefaultIfEmpty(null).Max(), PanelUnit.Temp, Theme.Storage, "disk", 100,
                                                     "disk:" + s.Disks[0].Id),
            "battery" => s.Battery == null ? null : new("BATTERY", s.Battery.Charge, PanelUnit.Percent, Theme.Good, "battery", 100, "battery"),
            _ => null,
        };
    }

    static PanelReading ReadSensor(SensorKey key, Snapshot snap, AppSettings? settings)
    {
        var entry = snap.FindSensor(key);
        var name = settings?.SensorName(key.Id, key.SensorName) ?? key.SensorName;
        var label = SensorFormat.DefaultLabel(name).ToUpperInvariant();
        var unit = UnitOf(key.SensorType);
        float? max = SensorFormat.IsPercent(key.SensorType) || key.SensorType == SensorType.Temperature ? 100 : null;
        var accent = Theme.ForHardware(key.HardwareType);
        if (entry == null) return new(label, null, unit, accent, null, max, null, key.SensorType);
        var s = entry.Sensor;
        return new(label, s.Value, unit, accent, s.Id, max ?? (s.Max is > 0 and var m ? m : null),
                   Vitals.PageFor(entry.Hardware), s.Type,
                   unit == PanelUnit.None ? SensorFormat.Short(s.Type, s.Value) : null);
    }

    public static PanelUnit UnitOf(SensorType t) => t switch
    {
        SensorType.Temperature => PanelUnit.Temp,
        SensorType.Power => PanelUnit.Watts,
        SensorType.Clock => PanelUnit.Clock,
        SensorType.Fan => PanelUnit.Rpm,
        SensorType.Throughput => PanelUnit.Rate,
        SensorType.Voltage => PanelUnit.Volts,
        SensorType.Data => PanelUnit.Gigabytes,
        _ when SensorFormat.IsPercent(t) => PanelUnit.Percent,
        _ => PanelUnit.None,
    };

    /// <summary>"Samsung SSD 990 PRO 2TB" → "SAMSUNG SSD", for drive labels.</summary>
    static string Short(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(2)).ToUpperInvariant();
    }

    /// <summary>The number and its unit, separately so the unit can be drawn smaller.</summary>
    public static (string Number, string Unit) Format(float? value, PanelUnit unit, int? decimals)
    {
        if (value is not { } v || float.IsNaN(v)) return ("--", UnitText(unit));
        string N(float x, int d) => x.ToString("F" + Math.Clamp(decimals ?? d, 0, 3), CultureInfo.CurrentCulture);
        switch (unit)
        {
            case PanelUnit.Temp: return (N(Fmt.TempValue(v), 0), Fmt.TempUnit);
            case PanelUnit.Watts: return (N(v, v < 10 ? 1 : 0), "W");
            case PanelUnit.Clock: return v >= 1000 ? (N(v / 1000, 2), "GHz") : (N(v, 0), "MHz");
            case PanelUnit.Rate:
            {
                var (k, u) = v switch
                {
                    < 1024 => (1f, "B/s"),
                    < 1024 * 1024 => (1024f, "KB/s"),
                    < 1024 * 1024 * 1024 => (1024f * 1024, "MB/s"),
                    _ => (1024f * 1024 * 1024, "GB/s"),
                };
                return (N(v / k, k == 1 ? 0 : v / k < 100 ? 1 : 0), u);
            }
            case PanelUnit.Gigabytes: return (N(v, 1), "GB");
            case PanelUnit.Volts: return (N(v, 3), "V");
            case PanelUnit.Ms: return (N(v, 0), "ms");
            case PanelUnit.Rpm: return (N(v, 0), "RPM");
            case PanelUnit.Percent: return (N(v, 0), "%");
            default: return (N(v, v < 10 ? 2 : v < 100 ? 1 : 0), "");
        }
    }

    static string UnitText(PanelUnit unit) => unit switch
    {
        PanelUnit.Temp => Fmt.TempUnit,
        PanelUnit.Percent => "%",
        PanelUnit.Watts => "W",
        PanelUnit.Clock => "GHz",
        PanelUnit.Rpm => "RPM",
        PanelUnit.Rate => "B/s",
        PanelUnit.Gigabytes => "GB",
        PanelUnit.Volts => "V",
        PanelUnit.Ms => "ms",
        _ => "",
    };
}
