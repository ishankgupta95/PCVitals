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
    Battery,
    /// <summary>Any one sensor the user picked (<see cref="WidgetItem.Sensor"/>); there can be several.</summary>
    Sensor,
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
    /// <summary>For <see cref="WidgetKind.Sensor"/> items: the sensor shown.</summary>
    public SensorKey? Sensor { get; set; }
}

/// <summary>The user's preferences for one sensor (see <see cref="AppSettings.Sensors"/>).</summary>
public sealed class SensorPref
{
    /// <summary>Shown instead of the sensor's own name; null keeps the original.</summary>
    public string? DisplayName { get; set; }
    public bool Favorite { get; set; }
    public bool Hidden { get; set; }
}

public sealed class AppSettings
{
    internal const int CurrentVersion = 4;
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
    /// <summary>Also draw the strip on the taskbars of other displays.</summary>
    public bool ShowOnAllDisplays { get; set; }

    public List<TrayMetric> TrayMetrics { get; set; } = new();
    public TrayIconStyle TrayStyle { get; set; } = TrayIconStyle.LabelAndNumber;

    public string? PrimaryGpuId { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public bool ShowAdvancedSensors { get; set; }
    /// <summary>List sensors the user hid in the dashboard (dimmed) instead of leaving them out.</summary>
    public bool ShowHiddenSensors { get; set; }
    public bool Fahrenheit { get; set; }
    /// <summary>The one-time "install PawnIO" notification has been shown.</summary>
    public bool PawnIoNoticeShown { get; set; }
    /// <summary>The battery item was switched on automatically the first time a battery was seen.</summary>
    public bool BatteryItemAutoEnabled { get; set; }
    public ThemeMode ColorTheme { get; set; } = ThemeMode.System;
    /// <summary>Taskbar strip size in percent of normal (it may still shrink further to fit).</summary>
    public int WidgetScale { get; set; } = 100;
    /// <summary>Global shortcut that toggles the dashboard, e.g. "Ctrl+Shift+Alt+V", or "Off".</summary>
    public string Hotkey { get; set; } = "Off";

    // Dashboard
    public string DashboardPage { get; set; } = "overview";
    public int GraphSeconds { get; set; } = 60;
    /// <summary>Dashboard window position and size when not maximised ("x,y,width,height"), or null before it was first moved.</summary>
    public string? DashboardBounds { get; set; }
    public bool DashboardMaximized { get; set; }

    // History saved to disk (see HistoryArchive.DefaultDir)
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;

    // Network
    /// <summary>Show Wi-Fi signal strength as the NET item's main value.</summary>
    public bool NetShowsWifi { get; set; }
    /// <summary>Ping a host every 5 s (off by default: it is the only feature that sends network traffic).</summary>
    public bool PingEnabled { get; set; }
    /// <summary>Host to ping; empty for the default gateway (router).</summary>
    public string PingHost { get; set; } = "";

    // Alerts
    public List<AlertRule> AlertRules { get; set; } = DefaultAlertRules();

    // The three fixed alerts of settings v2/v3, read only to migrate them into rules.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AlertCpuEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AlertCpuTemp { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AlertGpuEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AlertGpuTemp { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AlertRamEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AlertRamPercent { get; set; }
    /// <summary>Learned watts per 1% of NVAPI power, per NVIDIA GPU name (see HardwareService).</summary>
    public Dictionary<string, float> GpuPowerPerPercent { get; set; } = new();
    public HashSet<string> Collapsed { get; set; } = new();
    /// <summary>Per-sensor preferences by sensor id (e.g. "/amdcpu/0/temperature/2").</summary>
    public Dictionary<string, SensorPref> Sensors { get; set; } = new();

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
        new() { Kind = WidgetKind.Battery, Enabled = false, Label = "BAT" },
    };

    /// <summary>The name to show for a sensor: the user's rename, else <paramref name="original"/>.</summary>
    public string SensorName(string sensorId, string original) =>
        Sensors.TryGetValue(sensorId, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName! : original;

    public bool IsFavorite(string sensorId) => Sensors.TryGetValue(sensorId, out var p) && p.Favorite;

    public bool IsHidden(string sensorId) => Sensors.TryGetValue(sensorId, out var p) && p.Hidden;

    /// <summary>Changes a sensor's preferences, dropping the entry once nothing is set.</summary>
    public void UpdateSensor(string sensorId, Action<SensorPref> change)
    {
        if (!Sensors.TryGetValue(sensorId, out var p)) p = new SensorPref();
        change(p);
        if (string.IsNullOrWhiteSpace(p.DisplayName)) p.DisplayName = null;
        if (p.DisplayName == null && !p.Favorite && !p.Hidden) Sensors.Remove(sensorId);
        else Sensors[sensorId] = p;
    }

    public bool IsPinned(string sensorId) => WidgetItems.Any(i => i.Kind == WidgetKind.Sensor && i.Sensor?.Id == sensorId);

    /// <summary>Adds a sensor to the taskbar (at the end, switched on) unless it is already there.</summary>
    public WidgetItem PinSensor(SensorKey key, string label)
    {
        var existing = WidgetItems.FirstOrDefault(i => i.Kind == WidgetKind.Sensor && i.Sensor?.Id == key.Id);
        if (existing != null)
        {
            existing.Enabled = true;
            return existing;
        }
        var item = new WidgetItem { Kind = WidgetKind.Sensor, Enabled = true, Label = label, Sensor = key };
        WidgetItems.Add(item);
        return item;
    }

    public void UnpinSensor(string sensorId) => WidgetItems.RemoveAll(i => i.Kind == WidgetKind.Sensor && i.Sensor?.Id == sensorId);

    /// <summary>Sensors to keep history for: pinned (and switched on), watched by alerts, and favourites, by key.</summary>
    public List<SensorKey> TrackedSensors(Snapshot? snap)
    {
        var keys = WidgetItems.Where(i => i.Kind == WidgetKind.Sensor && i.Enabled && i.Sensor != null).Select(i => i.Sensor!).ToList();
        foreach (var rule in AlertRules)
            if (rule.Enabled && rule.Sensor is { } key && keys.All(k => k.Id != key.Id)) keys.Add(key);
        if (snap != null)
            foreach (var (id, pref) in Sensors)
                if (pref.Favorite && keys.All(k => k.Id != id) && snap.Catalog[id] is { } e) keys.Add(e.Key);
        return keys;
    }

    /// <summary>CPU temperature, GPU temperature and memory usage rules: what the app had before alerts on any sensor (off by default).</summary>
    public static List<AlertRule> DefaultAlertRules(bool cpu = false, int cpuTemp = 90, bool gpu = false, int gpuTemp = 85, bool ram = false, int ramPercent = 95) => new()
    {
        new() { Series = "cputemp", Threshold = cpuTemp, Enabled = cpu },
        new() { Series = "gputemp", Threshold = gpuTemp, Enabled = gpu },
        new() { Series = "ram", Threshold = ramPercent, Enabled = ram },
    };

    public static AppSettings CreateDefault() => new() { Version = CurrentVersion };

    public static AppSettings Load() => LoadFrom(FilePath);

    internal static AppSettings LoadFrom(string path)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings { Version = CurrentVersion }
                : new AppSettings { Version = CurrentVersion };
        }
        catch
        {
            // Corrupt settings: fall back to defaults rather than refusing to start.
            settings = new AppSettings { Version = CurrentVersion };
        }

        if (settings.Version < 2)
        {
            // Settings from early builds only had tray number icons; start them on the taskbar layout.
            settings = new AppSettings { Version = CurrentVersion, IntervalMs = settings.IntervalMs, Collapsed = settings.Collapsed };
            settings.SaveTo(path);
        }
        else if (settings.Version < CurrentVersion)
        {
            // v3 added per-sensor preferences (nothing to convert). v4 turned the three fixed alerts into rules.
            if (settings.Version < 4)
                settings.AlertRules = DefaultAlertRules(settings.AlertCpuEnabled ?? false, settings.AlertCpuTemp ?? 90,
                                                        settings.AlertGpuEnabled ?? false, settings.AlertGpuTemp ?? 85,
                                                        settings.AlertRamEnabled ?? false, settings.AlertRamPercent ?? 95);
            settings.AlertCpuEnabled = settings.AlertGpuEnabled = settings.AlertRamEnabled = null;
            settings.AlertCpuTemp = settings.AlertGpuTemp = settings.AlertRamPercent = null;
            settings.Version = CurrentVersion;
            settings.SaveTo(path);
        }
        settings.Sensors ??= new();
        settings.AlertRules ??= new();
        settings.AlertRules.RemoveAll(r => r == null! || (r.Sensor == null && (r.Series == null || !AlertRule.SeriesTargets.ContainsKey(r.Series))));

        // Keep the item list complete if new kinds were added in an update.
        foreach (var def in DefaultWidgetItems())
            if (settings.WidgetItems.All(i => i.Kind != def.Kind)) settings.WidgetItems.Add(new WidgetItem { Kind = def.Kind, Label = def.Label });
        // One item per built-in kind; sensor items are kept once per sensor, and dropped if they lost their sensor.
        settings.WidgetItems = settings.WidgetItems
            .Where(i => i.Kind != WidgetKind.Sensor || i.Sensor != null)
            .GroupBy(i => i.Kind == WidgetKind.Sensor ? "sensor:" + i.Sensor!.Id : i.Kind.ToString())
            .Select(g => g.First()).ToList();
        return settings;
    }

    public void Save() => SaveTo(FilePath);

    internal void SaveTo(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
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
