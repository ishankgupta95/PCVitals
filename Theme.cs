using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PCVitals;

public enum ThemeMode
{
    System,
    Light,
    Dark,
    Amoled,
}

sealed record Palette(
    bool IsLight,
    Color Bg, Color Rail, Color Surface, Color SurfaceHover, Color Border, Color Track,
    Color Text, Color TextDim, Color TextFaint, Color Tooltip,
    Color Cpu, Color Gpu, Color IGpu, Color Ram, Color Power, Color Net, Color Storage, Color Board, Color Other,
    Color Good, Color Warn, Color Hot);

/// <summary>
/// App colours. The active palette (light, dark or AMOLED black) is switched at runtime by
/// <see cref="Apply"/>; every surface reads these properties at paint time.
/// </summary>
static class Theme
{
    static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    public static readonly Palette DarkPalette = new(false,
        Bg: C(17, 18, 22), Rail: C(21, 22, 27), Surface: C(26, 28, 34), SurfaceHover: C(33, 36, 44), Border: C(44, 48, 58), Track: C(40, 43, 52),
        Text: C(236, 238, 242), TextDim: C(160, 166, 180), TextFaint: C(105, 111, 126), Tooltip: Color.FromArgb(240, 10, 11, 14),
        Cpu: C(86, 168, 255), Gpu: C(118, 214, 140), IGpu: C(250, 140, 80), Ram: C(190, 140, 255), Power: C(255, 184, 77),
        Net: C(77, 208, 225), Storage: C(244, 114, 182), Board: C(255, 140, 105), Other: C(160, 166, 180),
        Good: C(118, 214, 140), Warn: C(255, 196, 61), Hot: C(255, 92, 92));

    // True black for OLED panels: pixels are off, surfaces are barely lifted.
    public static readonly Palette AmoledPalette = DarkPalette with
    {
        Bg = C(0, 0, 0), Rail = C(0, 0, 0), Surface = C(14, 14, 16), SurfaceHover = C(26, 26, 30),
        Border = C(36, 36, 42), Track = C(30, 30, 35), Tooltip = Color.FromArgb(245, 22, 22, 26),
    };

    public static readonly Palette LightPalette = new(true,
        Bg: C(243, 244, 247), Rail: C(235, 237, 241), Surface: C(255, 255, 255), SurfaceHover: C(232, 235, 241), Border: C(218, 222, 230), Track: C(226, 229, 235),
        Text: C(24, 26, 32), TextDim: C(86, 92, 106), TextFaint: C(132, 138, 152), Tooltip: Color.FromArgb(245, 255, 255, 255),
        Cpu: C(0, 103, 192), Gpu: C(16, 132, 60), IGpu: C(202, 80, 16), Ram: C(124, 58, 200), Power: C(178, 98, 0),
        Net: C(0, 122, 140), Storage: C(191, 0, 119), Board: C(196, 70, 30), Other: C(100, 106, 120),
        Good: C(16, 132, 60), Warn: C(176, 110, 0), Hot: C(196, 43, 28));

    static Palette _p = DarkPalette;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsLight => _p.IsLight;
    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event Action? Changed;

    /// <summary>Selects a palette; System follows Windows' app theme (light/dark).</summary>
    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var next = mode switch
        {
            ThemeMode.Light => LightPalette,
            ThemeMode.Dark => DarkPalette,
            ThemeMode.Amoled => AmoledPalette,
            _ => AppsUseLightTheme() ? LightPalette : DarkPalette,
        };
        if (next == _p) return;
        _p = next;
        Changed?.Invoke();
    }

    static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color Bg => _p.Bg;
    public static Color Rail => _p.Rail;
    public static Color Surface => _p.Surface;
    public static Color SurfaceHover => _p.SurfaceHover;
    public static Color Border => _p.Border;
    public static Color Track => _p.Track;
    public static Color Text => _p.Text;
    public static Color TextDim => _p.TextDim;
    public static Color TextFaint => _p.TextFaint;
    public static Color Tooltip => _p.Tooltip;

    public static Color Cpu => _p.Cpu;
    public static Color Gpu => _p.Gpu;
    public static Color IGpu => _p.IGpu;
    public static Color Ram => _p.Ram;
    public static Color Power => _p.Power;
    public static Color Net => _p.Net;
    public static Color Storage => _p.Storage;
    public static Color Board => _p.Board;
    public static Color Other => _p.Other;

    public static Color Good => _p.Good;
    public static Color Warn => _p.Warn;
    public static Color Hot => _p.Hot;

    /// <summary>
    /// Accent for a widget item drawn on the taskbar. That follows the taskbar's own light/dark
    /// setting, not the app theme, so it starts from the dark palette's bright accents.
    /// </summary>
    public static Color TaskbarAccent(ItemView v, bool lightTaskbar)
    {
        var d = DarkPalette;
        var c = v.Kind switch
        {
            WidgetKind.Cpu => d.Cpu,
            WidgetKind.Gpu or WidgetKind.SecondGpu => v.Tag == "Integrated" ? d.IGpu : d.Gpu,
            WidgetKind.Memory => d.Ram,
            WidgetKind.Network => d.Net,
            WidgetKind.Power => d.Power,
            WidgetKind.Disk => d.Storage,
            WidgetKind.Battery => d.Good,
            _ => d.Board,
        };
        return OnTaskbar(c, lightTaskbar);
    }

    public static Color TempColor(float? celsius) => celsius switch
    {
        null => TextDim,
        < 65 => Good,
        < 82 => Warn,
        _ => Hot,
    };

    public static Color ForHardware(HardwareType t) => t switch
    {
        HardwareType.Cpu => Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => Gpu,
        HardwareType.Memory => Ram,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => Board,
        HardwareType.Storage => Storage,
        HardwareType.Network => Net,
        HardwareType.Psu or HardwareType.Battery or HardwareType.PowerMonitor => Power,
        _ => Other,
    };

    public static string Badge(HardwareType t) => t switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "RAM",
        HardwareType.Motherboard => "BOARD",
        HardwareType.SuperIO => "CHIPSET",
        HardwareType.EmbeddedController => "EC",
        HardwareType.Storage => "DISK",
        HardwareType.Network => "NET",
        HardwareType.Cooler => "COOLER",
        HardwareType.Psu => "PSU",
        HardwareType.Battery => "BATTERY",
        _ => t.ToString().ToUpperInvariant(),
    };

    public static Color WithAlpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Accent for text drawn straight onto the taskbar, which may be light or dark.</summary>
    public static Color OnTaskbar(Color darkAccent, bool lightTaskbar)
    {
        if (!lightTaskbar) return darkAccent;
        // Darken bright accents so they stay readable on a light taskbar.
        return Color.FromArgb((int)(darkAccent.R * 0.55), (int)(darkAccent.G * 0.55), (int)(darkAccent.B * 0.55));
    }

    /// <summary>Taskbar text stays neutral while temperatures are normal and only turns amber/red when hot.</summary>
    public static Color TaskbarTemp(float? celsius, bool lightTaskbar, Color normal) => celsius switch
    {
        >= 82 => lightTaskbar ? LightPalette.Hot : DarkPalette.Hot,
        >= 65 => lightTaskbar ? LightPalette.Warn : DarkPalette.Warn,
        _ => normal,
    };

    /// <summary>True when the taskbar uses the light theme (affects tray icon text colour).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }
}

static class Fmt
{
    /// <summary>Display temperatures in Fahrenheit. Values stay Celsius internally (thresholds, colours).</summary>
    public static bool Fahrenheit { get; set; }

    public static string TempUnit => Fahrenheit ? "°F" : "°C";

    public static float TempValue(float celsius) => Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    /// <summary>"52°C" / "126°F", or "--°C" when unknown.</summary>
    public static string Temp(float? celsius, string format = "0") =>
        celsius is { } c ? TempValue(c).ToString(format) + TempUnit : "--" + TempUnit;

    public static string Sensor(SensorType t, float? v)
    {
        if (v is not { } x) return "—";
        if (t == SensorType.Temperature) return $"{TempValue(x):0.0} {TempUnit}";
        return t switch
        {
            SensorType.Voltage => $"{x:0.000} V",
            SensorType.Current => $"{x:0.00} A",
            SensorType.Power => $"{x:0.0} W",
            SensorType.Clock => x >= 1000 ? $"{x / 1000:0.00} GHz" : $"{x:0} MHz",
            SensorType.Temperature => $"{x:0.0} °C",
            SensorType.Load => $"{x:0.0} %",
            SensorType.Frequency => $"{x:0} Hz",
            SensorType.Fan => $"{x:0} RPM",
            SensorType.Flow => $"{x:0.0} L/h",
            SensorType.Control => $"{x:0} %",
            SensorType.Level => $"{x:0.0} %",
            SensorType.Factor => $"{x:0.000}",
            SensorType.Data => $"{x:0.0} GB",
            SensorType.SmallData => x >= 1024 ? $"{x / 1024:0.00} GB" : $"{x:0} MB",
            SensorType.Throughput => Rate(x),
            SensorType.TimeSpan => Duration(x),
            SensorType.Timing => $"{x:0.0} ns",
            SensorType.Energy => $"{x:0} mWh",
            SensorType.Noise => $"{x:0} dBA",
            SensorType.Conductivity => $"{x:0.0} µS/cm",
            SensorType.Humidity => $"{x:0} %",
            _ => $"{x:0.##}",
        };
    }

    /// <summary>Unit-less version used in the narrow min/max columns.</summary>
    public static string Compact(SensorType t, float? v)
    {
        if (v is not { } x) return "—";
        if (t == SensorType.Temperature) x = TempValue(x);
        return t switch
        {
            SensorType.Voltage => $"{x:0.000}",
            SensorType.Current or SensorType.Factor => $"{x:0.00}",
            SensorType.Clock or SensorType.Fan or SensorType.SmallData or SensorType.Control => $"{x:0}",
            SensorType.Throughput => Rate(x),
            SensorType.TimeSpan => Duration(x),
            _ => $"{x:0.0}",
        };
    }

    public static string Rate(float bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:0.0} KB/s";
        if (bytesPerSecond < 1024 * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        return $"{bytesPerSecond / 1024 / 1024 / 1024:0.00} GB/s";
    }

    public static string Duration(float seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    public static string Num(float? v, string format = "0") => v is { } x ? x.ToString(format) : "--";
}
