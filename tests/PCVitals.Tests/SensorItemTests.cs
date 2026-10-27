using LibreHardwareMonitor.Hardware;
using Xunit;

namespace PCVitals.Tests;

public class SensorItemTests
{
    static readonly HardwareReading Gpu = new("/gpu-nvidia/0", "NVIDIA GeForce RTX 5080", HardwareType.GpuNvidia, new[]
    {
        new SensorReading("/gpu-nvidia/0/temperature/2", "GPU Hot Spot", SensorType.Temperature, 71.4f, 40f, 83.2f, 60f),
        new SensorReading("/gpu-nvidia/0/fan/1", "GPU Fan 1", SensorType.Fan, 1234f, 0f, 2100f, 900f),
    });

    static Snapshot Snap(Dictionary<string, float[]>? history = null) =>
        new(DateTime.Now, new[] { Gpu }, new Summary(), history ?? new(), new[] { Gpu });

    static SensorKey Key(int i) => SensorKey.For(Gpu, Gpu.Sensors[i]);

    [Fact]
    public void Sensor_item_shows_value_min_max_and_history()
    {
        var history = new Dictionary<string, float[]> { ["/gpu-nvidia/0/temperature/2"] = new[] { 70f, 71f } };
        var v = Vitals.ForSensor(Snap(history), Key(0), "HOT");
        Assert.Equal(WidgetKind.Sensor, v.Kind);
        Assert.Equal("HOT", v.Label);
        Assert.Equal("71°C", v.Primary);
        Assert.Equal(71.4f, v.PrimaryTemp);
        Assert.Equal("max 83°C", v.Line1);
        Assert.Equal("min 40°C", v.Line2);
        Assert.Equal(new[] { 70f, 71f }, v.History);
        Assert.Equal("gpu:/gpu-nvidia/0", v.Page);
        Assert.Contains(v.Rows, r => r.Label.StartsWith("Average") && r.Value == "60.0 °C");
    }

    [Fact]
    public void Renamed_sensor_uses_the_new_name()
    {
        var settings = AppSettings.CreateDefault();
        settings.UpdateSensor("/gpu-nvidia/0/fan/1", p => p.DisplayName = "Front fan");
        var v = Vitals.ForSensor(Snap(), Key(1), null, settings);
        Assert.Equal("Front fan", v.Title);
        Assert.Equal("1234 RPM", v.Primary);
        Assert.Contains(v.Rows, r => r.Label == "Sensor" && r.Value == "GPU Fan 1");
    }

    [Fact]
    public void Missing_sensor_stays_on_the_taskbar_as_dashes()
    {
        var gone = new SensorKey("/nvme/3/temperature/0", HardwareType.Storage, "Old SSD", SensorType.Temperature, "Temperature");
        var v = Vitals.ForSensor(Snap(), gone, "SSD");
        Assert.Equal("--", v.Primary);
        Assert.Equal(new[] { "100°C" }, v.PrimaryTemplates);
        Assert.Contains(v.Rows, r => r.Value == "Not available");
    }

    [Theory]
    [InlineData(SensorType.Temperature, 52.4f, "52°C")]
    [InlineData(SensorType.Load, 7.6f, "8%")]
    [InlineData(SensorType.Power, 45.25f, "45.3 W")]
    [InlineData(SensorType.Power, 245.4f, "245 W")]
    [InlineData(SensorType.Fan, 980f, "980 RPM")]
    [InlineData(SensorType.Clock, 4850f, "4.85 GHz")]
    [InlineData(SensorType.Clock, 800f, "800 MHz")]
    [InlineData(SensorType.Voltage, 1.2f, "1.200 V")]
    [InlineData(SensorType.SmallData, 2662f, "2.6 GB")]
    [InlineData(SensorType.Throughput, 2048f, "2.0 KB/s")]
    public void Short_values_per_type(SensorType type, float value, string expected) =>
        Assert.Equal(expected, SensorFormat.Short(type, value));

    [Theory]
    [InlineData(SensorType.Temperature, 99.9f)]
    [InlineData(SensorType.Load, 100f)]
    [InlineData(SensorType.Power, 99.94f)]
    [InlineData(SensorType.Power, 999f)]
    [InlineData(SensorType.Fan, 9999f)]
    [InlineData(SensorType.Clock, 9990f)]
    [InlineData(SensorType.Voltage, 1.234f)]
    public void Templates_are_at_least_as_long_as_values(SensorType type, float value)
    {
        // Matching character counts are what keep the strip from changing width as values change.
        var longest = SensorFormat.Templates(type).Max(t => t.Length);
        Assert.True(SensorFormat.Short(type, value).Length <= longest, $"{SensorFormat.Short(type, value)} vs {longest}");
    }

    [Theory]
    [InlineData("GPU Hot Spot", "GPU Hot")]
    [InlineData("Core (Tctl/Tdie)", "Core")]
    [InlineData("Composite Temperature", "Composit")]
    [InlineData("Fan #2", "Fan 2")]
    public void Default_labels_are_short(string name, string label) => Assert.Equal(label, SensorFormat.DefaultLabel(name));
}

public sealed class SensorPreferenceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcvitals-tests-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "settings.json");

    public SensorPreferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly SensorKey Fan = new("/lpc/nct6799d/0/fan/1", HardwareType.SuperIO, "Nuvoton NCT6799D", SensorType.Fan, "Fan #2");

    [Fact]
    public void Pinned_sensor_survives_a_restart()
    {
        var s = AppSettings.CreateDefault();
        s.PinSensor(Fan, "PUMP");
        s.SaveTo(FilePath);

        var back = AppSettings.LoadFrom(FilePath);
        var item = Assert.Single(back.WidgetItems, i => i.Kind == WidgetKind.Sensor);
        Assert.True(item.Enabled);
        Assert.Equal("PUMP", item.Label);
        Assert.Equal(Fan, item.Sensor);
        Assert.True(back.IsPinned(Fan.Id));
    }

    [Fact]
    public void Pinning_twice_keeps_one_item_and_unpin_removes_it()
    {
        var s = AppSettings.CreateDefault();
        s.PinSensor(Fan, "A");
        s.PinSensor(Fan, "B");
        Assert.Single(s.WidgetItems, i => i.Kind == WidgetKind.Sensor);
        s.UnpinSensor(Fan.Id);
        Assert.DoesNotContain(s.WidgetItems, i => i.Kind == WidgetKind.Sensor);
    }

    [Fact]
    public void Several_sensor_items_load_and_broken_ones_are_dropped()
    {
        File.WriteAllText(FilePath, """
            { "Version": 3, "WidgetItems": [
                { "Kind": "Sensor", "Enabled": true, "Label": "A",
                  "Sensor": { "Id": "/a/0/fan/0", "HardwareType": "SuperIO", "HardwareName": "Chip", "SensorType": "Fan", "SensorName": "Fan #1" } },
                { "Kind": "Sensor", "Enabled": true, "Label": "B",
                  "Sensor": { "Id": "/a/0/fan/1", "HardwareType": "SuperIO", "HardwareName": "Chip", "SensorType": "Fan", "SensorName": "Fan #2" } },
                { "Kind": "Sensor", "Enabled": true, "Label": "dup",
                  "Sensor": { "Id": "/a/0/fan/1", "HardwareType": "SuperIO", "HardwareName": "Chip", "SensorType": "Fan", "SensorName": "Fan #2" } },
                { "Kind": "Sensor", "Enabled": true, "Label": "no sensor" } ] }
            """);
        var s = AppSettings.LoadFrom(FilePath);
        Assert.Equal(new[] { "A", "B" }, s.WidgetItems.Where(i => i.Kind == WidgetKind.Sensor).Select(i => i.Label));
    }

    [Fact]
    public void Rename_hide_and_favourite_persist_and_clearing_drops_the_entry()
    {
        var s = AppSettings.CreateDefault();
        s.UpdateSensor(Fan.Id, p =>
        {
            p.DisplayName = "Pump";
            p.Favorite = true;
            p.Hidden = true;
        });
        s.SaveTo(FilePath);

        var back = AppSettings.LoadFrom(FilePath);
        Assert.Equal("Pump", back.SensorName(Fan.Id, "Fan #2"));
        Assert.True(back.IsFavorite(Fan.Id));
        Assert.True(back.IsHidden(Fan.Id));

        back.UpdateSensor(Fan.Id, p =>
        {
            p.DisplayName = "  ";
            p.Favorite = false;
            p.Hidden = false;
        });
        Assert.Empty(back.Sensors);
        Assert.Equal("Fan #2", back.SensorName(Fan.Id, "Fan #2"));
    }
}

public class SensorPickerTests
{
    static SensorReading S(string id, string name, SensorType type) => new(id, name, type, 1, null, null);

    static readonly HardwareReading Cpu = new("/amdcpu/0", "AMD Ryzen 7 9800X3D", HardwareType.Cpu, new[]
    {
        S("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", SensorType.Temperature),
        S("/amdcpu/0/load/0", "CPU Total", SensorType.Load),
    });
    static readonly HardwareReading Board = new("/lpc/nct6799d/0", "Nuvoton NCT6799D", HardwareType.SuperIO, new[]
    {
        S("/lpc/nct6799d/0/fan/1", "Fan #2", SensorType.Fan),
        S("/lpc/nct6799d/0/temperature/0", "CPU", SensorType.Temperature),
    });

    [Fact]
    public void Favourites_come_first_then_devices_in_order()
    {
        var settings = AppSettings.CreateDefault();
        settings.UpdateSensor("/lpc/nct6799d/0/fan/1", p => p.Favorite = true);
        var rows = SensorPickerModel.Build(new SensorCatalog(new[] { Cpu, Board }), settings, "");
        Assert.Equal(new[] { "Favourites", null, "AMD Ryzen 7 9800X3D", null, null, "Nuvoton NCT6799D", null }, rows.Select(r => r.Header));
        Assert.Equal("/lpc/nct6799d/0/fan/1", rows[1].Entry!.Id);
    }

    [Fact]
    public void Search_matches_renamed_sensors_and_keeps_device_headers()
    {
        var settings = AppSettings.CreateDefault();
        settings.UpdateSensor("/lpc/nct6799d/0/fan/1", p => p.DisplayName = "Pump");
        var rows = SensorPickerModel.Build(new SensorCatalog(new[] { Cpu, Board }), settings, "pump");
        Assert.Equal(new[] { "Nuvoton NCT6799D", null }, rows.Select(r => r.Header));

        rows = SensorPickerModel.Build(new SensorCatalog(new[] { Cpu, Board }), settings, "temp");
        Assert.Equal(new[] { "/amdcpu/0/temperature/2", "/lpc/nct6799d/0/temperature/0" },
                     rows.Where(r => r.Entry != null).Select(r => r.Entry!.Id));
    }
}
