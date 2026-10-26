using Xunit;

namespace PCVitals.Tests;

public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcvitals-tests-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = AppSettings.LoadFrom(FilePath);
        Assert.True(s.WidgetEnabled);
        Assert.Equal(1000, s.IntervalMs);
        Assert.Equal(ThemeMode.System, s.ColorTheme);
        Assert.Equal(new[] { WidgetKind.Cpu, WidgetKind.Gpu, WidgetKind.Memory, WidgetKind.Network },
                     s.WidgetItems.Where(i => i.Enabled).Select(i => i.Kind));
        Assert.Equal(Enum.GetValues<WidgetKind>().Length, s.WidgetItems.Count);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_instead_of_crashing()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        var s = AppSettings.LoadFrom(FilePath);
        Assert.True(s.WidgetEnabled);
    }

    [Fact]
    public void Early_settings_are_migrated_to_the_taskbar_layout()
    {
        // Settings from early builds: tray number icons only, no version field.
        File.WriteAllText(FilePath, """{ "TrayMetrics": ["CpuTemp","GpuTemp"], "IntervalMs": 2000, "Collapsed": ["/amdcpu/0"] }""");
        var s = AppSettings.LoadFrom(FilePath);
        Assert.Empty(s.TrayMetrics);
        Assert.True(s.WidgetEnabled);
        Assert.Equal(2000, s.IntervalMs);
        Assert.Contains("/amdcpu/0", s.Collapsed);
        Assert.Contains($"\"Version\": {AppSettings.CurrentVersion}", File.ReadAllText(FilePath)); // migration is saved
    }

    [Fact]
    public void Item_kinds_added_in_updates_are_appended_and_duplicates_dropped()
    {
        File.WriteAllText(FilePath, """
            { "Version": 2, "WidgetItems": [
                { "Kind": "Memory", "Enabled": true, "Label": "RAM" },
                { "Kind": "Memory", "Enabled": false, "Label": "dup" } ] }
            """);
        var s = AppSettings.LoadFrom(FilePath);
        Assert.Equal(WidgetKind.Memory, s.WidgetItems[0].Kind);
        Assert.Equal("RAM", s.WidgetItems[0].Label); // user's order and label kept
        Assert.Single(s.WidgetItems, i => i.Kind == WidgetKind.Memory);
        Assert.Equal(Enum.GetValues<WidgetKind>().Length, s.WidgetItems.Count);
        Assert.False(s.WidgetItems.Single(i => i.Kind == WidgetKind.Battery).Enabled); // new kinds start off
    }

    [Fact]
    public void Version_2_settings_migrate_to_3_keeping_everything()
    {
        File.WriteAllText(FilePath, """
            { "Version": 2, "IntervalMs": 2000, "ColorTheme": "Dark", "AlertCpuEnabled": true, "AlertCpuTemp": 80,
              "WidgetItems": [ { "Kind": "Gpu", "Enabled": true, "Label": "VGA" } ] }
            """);
        var s = AppSettings.LoadFrom(FilePath);
        Assert.Equal(3, s.Version);
        Assert.Equal(2000, s.IntervalMs);
        Assert.Equal(ThemeMode.Dark, s.ColorTheme);
        Assert.True(s.AlertCpuEnabled);
        Assert.Equal(80, s.AlertCpuTemp);
        Assert.Equal("VGA", s.WidgetItems[0].Label);
        Assert.Empty(s.Sensors);
        Assert.Contains("\"Version\": 3", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Null_sensor_preferences_become_empty()
    {
        File.WriteAllText(FilePath, """{ "Version": 3, "Sensors": null }""");
        Assert.NotNull(AppSettings.LoadFrom(FilePath).Sensors);
    }

    [Fact]
    public void Sensor_preferences_round_trip()
    {
        var s = AppSettings.CreateDefault();
        s.Sensors["/amdcpu/0/temperature/2"] = new SensorPref { DisplayName = "CCD", Favorite = true };
        s.Sensors["/nvme/1/temperature/0"] = new SensorPref { Hidden = true };
        s.SaveTo(FilePath);

        var back = AppSettings.LoadFrom(FilePath);
        Assert.Equal("CCD", back.Sensors["/amdcpu/0/temperature/2"].DisplayName);
        Assert.True(back.Sensors["/amdcpu/0/temperature/2"].Favorite);
        Assert.True(back.Sensors["/nvme/1/temperature/0"].Hidden);
        Assert.Null(back.Sensors["/nvme/1/temperature/0"].DisplayName);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var s = AppSettings.CreateDefault();
        s.ColorTheme = ThemeMode.Amoled;
        s.Fahrenheit = true;
        s.WidgetScale = 85;
        s.AlertCpuEnabled = true;
        s.GpuPowerPerPercent["NVIDIA GeForce RTX 5080"] = 3.98f;
        s.SaveTo(FilePath);

        var back = AppSettings.LoadFrom(FilePath);
        Assert.Equal(ThemeMode.Amoled, back.ColorTheme);
        Assert.True(back.Fahrenheit);
        Assert.Equal(85, back.WidgetScale);
        Assert.True(back.AlertCpuEnabled);
        Assert.Equal(3.98f, back.GpuPowerPerPercent["NVIDIA GeForce RTX 5080"], 3);
    }
}
