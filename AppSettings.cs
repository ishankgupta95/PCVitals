using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCVitals;

public enum TrayMetric
{
    CpuTemp,
    CpuLoad,
    GpuTemp,
    GpuLoad,
    RamLoad,
    Power,
}

public enum TrayIconStyle
{
    LabelAndNumber,
    NumberOnly,
}

/// <summary>An item in the taskbar widget strip.</summary>
public enum WidgetKind
{
    Cpu,
    Gpu,
    SecondGpu,
    Memory,
    Network,
    Power,
    Board,
    Disk,
}

public enum WidgetPosition
{
    NextToTray,
    LeftSide,
}

public sealed class WidgetItem
{
    public WidgetKind Kind { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Text shown above the value; empty hides the label.</summary>
    public string Label { get; set; } = "";
}

public sealed class AppSettings
{
    const int CurrentVersion = 2;
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCVitals");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>0 when missing from the file, i.e. a v1 settings file.</summary>
    public int Version { get; set; }

    public bool WidgetEnabled { get; set; } = true;
    public List<WidgetItem> WidgetItems { get; set; } = DefaultWidgetItems();
    public bool WidgetGraphs { get; set; } = true;
    public WidgetPosition WidgetPosition { get; set; } = WidgetPosition.NextToTray;
    /// <summary>Extra distance from the anchor edge, in 96-DPI pixels (set by dragging the widget).</summary>
    public int WidgetOffset { get; set; }
    /// <summary>With an auto-hiding taskbar, keep the stats on screen as a floating pill.</summary>
    public bool FloatWhenTaskbarHidden { get; set; }

    public List<TrayMetric> TrayMetrics { get; set; } = new();
    public TrayIconStyle TrayStyle { get; set; } = TrayIconStyle.LabelAndNumber;

    public string? PrimaryGpuId { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public bool ShowAdvancedSensors { get; set; }
    public bool Fahrenheit { get; set; }
    public ThemeMode ColorTheme { get; set; } = ThemeMode.System;
    /// <summary>Taskbar strip size in percent of normal (it may still shrink further to fit).</summary>
    public int WidgetScale { get; set; } = 100;
    /// <summary>Global shortcut that toggles the dashboard, e.g. "Ctrl+Shift+Alt+V", or "Off".</summary>
    public string Hotkey { get; set; } = "Off";

    // Dashboard
    public string DashboardPage { get; set; } = "overview";
    public int GraphSeconds { get; set; } = 60;

    // Alerts (Windows notifications when a reading stays above its limit)
    public bool AlertCpuEnabled { get; set; }
    public int AlertCpuTemp { get; set; } = 90;
    public bool AlertGpuEnabled { get; set; }
    public int AlertGpuTemp { get; set; } = 85;
    public bool AlertRamEnabled { get; set; }
    public int AlertRamPercent { get; set; } = 95;
    /// <summary>Learned watts per 1% of NVAPI power, per NVIDIA GPU name (see HardwareService).</summary>
    public Dictionary<string, float> GpuPowerPerPercent { get; set; } = new();
    public HashSet<string> Collapsed { get; set; } = new();

    public static List<WidgetItem> DefaultWidgetItems() => new()
    {
        new() { Kind = WidgetKind.Cpu, Enabled = true, Label = "CPU" },
        new() { Kind = WidgetKind.Gpu, Enabled = true, Label = "GPU" },
        new() { Kind = WidgetKind.Memory, Enabled = true, Label = "MEM" },
        new() { Kind = WidgetKind.Network, Enabled = true, Label = "NET" },
        new() { Kind = WidgetKind.Power, Enabled = false, Label = "PWR" },
        new() { Kind = WidgetKind.SecondGpu, Enabled = false, Label = "iGPU" },
        new() { Kind = WidgetKind.Board, Enabled = false, Label = "BOARD" },
        new() { Kind = WidgetKind.Disk, Enabled = false, Label = "SSD" },
    };

    public static AppSettings CreateDefault() => new() { Version = CurrentVersion };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings { Version = CurrentVersion }
                : new AppSettings { Version = CurrentVersion };
        }
        catch
        {
            // Corrupt settings: fall back to defaults rather than refusing to start.
            settings = new AppSettings { Version = CurrentVersion };
        }

        if (settings.Version < CurrentVersion)
        {
            // v1 only had tray number icons; move people onto the new taskbar widget layout.
            settings = new AppSettings { Version = CurrentVersion, IntervalMs = settings.IntervalMs, Collapsed = settings.Collapsed };
            settings.Save();
        }

        // Keep the item list complete if new kinds were added in an update.
        foreach (var def in DefaultWidgetItems())
            if (settings.WidgetItems.All(i => i.Kind != def.Kind)) settings.WidgetItems.Add(new WidgetItem { Kind = def.Kind, Label = def.Label });
        settings.WidgetItems = settings.WidgetItems.GroupBy(i => i.Kind).Select(g => g.First()).ToList();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Program.Log("Couldn't save settings: " + ex);
        }
    }
}

/// <summary>
/// "Start with Windows" via a logon scheduled task. A Run-key entry can't start an elevated
/// process without a UAC prompt every logon; a task with highest privileges can.
/// </summary>
static class Autostart
{
    const string TaskName = "PC Vitals";

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Shows PC vitals on the taskbar.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "pcvitals-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public static bool Disable() => Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
