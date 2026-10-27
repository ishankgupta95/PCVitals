using LibreHardwareMonitor.Hardware;
using Xunit;

namespace PCVitals.Tests;

public class SensorKeyTests
{
    static SensorReading S(string id, string name, SensorType type, float value = 50) => new(id, name, type, value, null, null);

    static readonly HardwareReading Cpu = new("/amdcpu/0", "AMD Ryzen 7 9800X3D", HardwareType.Cpu, new[]
    {
        S("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", SensorType.Temperature),
        S("/amdcpu/0/temperature/3", "CCD1 (Tdie)", SensorType.Temperature),
        S("/amdcpu/0/load/0", "CPU Total", SensorType.Load),
    });

    // Two identical drives: same names everywhere, only the ids differ.
    static readonly HardwareReading Ssd0 = new("/nvme/0", "Samsung SSD 990 PRO 2TB", HardwareType.Storage, new[]
    {
        S("/nvme/0/temperature/0", "Composite Temperature", SensorType.Temperature, 40),
    });
    static readonly HardwareReading Ssd1 = new("/nvme/1", "Samsung SSD 990 PRO 2TB", HardwareType.Storage, new[]
    {
        S("/nvme/1/temperature/0", "Composite Temperature", SensorType.Temperature, 45),
    });

    static SensorCatalog Catalog(params HardwareReading[] hw) => new(hw);

    static SensorKey Key(string id, HardwareReading hw, string name, SensorType type) => new(id, hw.Type, hw.Name, type, name);

    [Fact]
    public void Resolves_by_id_when_it_is_still_the_same_sensor()
    {
        var found = Catalog(Cpu).Find(Key("/amdcpu/0/temperature/2", Cpu, "Core (Tctl/Tdie)", SensorType.Temperature));
        Assert.Equal("/amdcpu/0/temperature/2", found?.Id);
    }

    [Fact]
    public void Follows_the_sensor_when_its_index_shifted()
    {
        // After an update the CCD sensor moved from index 3 to 4, and index 3 is now something else.
        var updated = Cpu with
        {
            Sensors = new[]
            {
                S("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", SensorType.Temperature),
                S("/amdcpu/0/temperature/3", "CCD Average", SensorType.Temperature),
                S("/amdcpu/0/temperature/4", "CCD1 (Tdie)", SensorType.Temperature),
            },
        };
        var found = Catalog(updated).Find(Key("/amdcpu/0/temperature/3", Cpu, "CCD1 (Tdie)", SensorType.Temperature));
        Assert.Equal("/amdcpu/0/temperature/4", found?.Id);
    }

    [Fact]
    public void Follows_the_sensor_when_the_device_id_changed()
    {
        var moved = new HardwareReading("/gpu-nvidia/1", "NVIDIA GeForce RTX 5080", HardwareType.GpuNvidia,
                                        new[] { S("/gpu-nvidia/1/temperature/0", "GPU Core", SensorType.Temperature) });
        var key = new SensorKey("/gpu-nvidia/0/temperature/0", HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5080", SensorType.Temperature, "GPU Core");
        Assert.Equal("/gpu-nvidia/1/temperature/0", Catalog(moved).Find(key)?.Id);
    }

    [Fact]
    public void Identical_devices_stay_apart()
    {
        var catalog = Catalog(Ssd0, Ssd1);
        Assert.Equal("/nvme/1/temperature/0", catalog.Find(Key("/nvme/1/temperature/0", Ssd1, "Composite Temperature", SensorType.Temperature))?.Id);

        // The id changed index within the same drive: the fingerprint matches both drives, the device part picks one.
        var shifted = Ssd1 with { Sensors = new[] { S("/nvme/1/temperature/1", "Composite Temperature", SensorType.Temperature) } };
        Assert.Equal("/nvme/1/temperature/1", Catalog(Ssd0, shifted).Find(Key("/nvme/1/temperature/0", Ssd1, "Composite Temperature", SensorType.Temperature))?.Id);
    }

    [Fact]
    public void Ambiguous_match_is_not_guessed()
    {
        // A third identical drive's key can't be told apart from the two that are present.
        var key = new SensorKey("/nvme/2/temperature/0", HardwareType.Storage, "Samsung SSD 990 PRO 2TB", SensorType.Temperature, "Composite Temperature");
        Assert.Null(Catalog(Ssd0, Ssd1).Find(key));
    }

    [Fact]
    public void Missing_sensor_resolves_to_null() =>
        Assert.Null(Catalog(Cpu).Find(Key("/amdcpu/0/power/0", Cpu, "Package", SensorType.Power)));

    [Fact]
    public void Renamed_sensor_with_the_same_id_is_still_found()
    {
        // A library update renamed the sensor but kept its id, and nothing else has the old name.
        var renamed = Cpu with { Sensors = new[] { S("/amdcpu/0/load/0", "CPU Total Load", SensorType.Load) } };
        Assert.Equal("/amdcpu/0/load/0", Catalog(renamed).Find(Key("/amdcpu/0/load/0", Cpu, "CPU Total", SensorType.Load))?.Id);
    }

    [Theory]
    [InlineData("/amdcpu/0/temperature/2", "/amdcpu/0")]
    [InlineData("/lpc/nct6799d/0/fan/1", "/lpc/nct6799d/0")]
    [InlineData("/pcvitals/gpu-nvidia/0/power/board", "/gpu-nvidia/0")]
    public void Hardware_part_of_an_id(string id, string hardware) => Assert.Equal(hardware, SensorKey.HardwarePart(id));

    [Fact]
    public void Search_matches_every_word_across_device_sensor_and_type()
    {
        var catalog = Catalog(Cpu, Ssd0, Ssd1);
        Assert.Equal(new[] { "/amdcpu/0/temperature/2", "/amdcpu/0/temperature/3" }, catalog.Search("ryzen temp").Select(e => e.Id));
        Assert.Equal(new[] { "/amdcpu/0/temperature/3" }, catalog.Search("ccd1").Select(e => e.Id));
        Assert.Equal(2, catalog.Search("SAMSUNG composite").Count());
        Assert.Equal(new[] { "/amdcpu/0/load/0" }, catalog.Search("%").Select(e => e.Id));
        Assert.Equal(catalog.All.Count, catalog.Search("  ").Count());
        Assert.Empty(catalog.Search("fan"));
    }

    [Fact]
    public void Entries_know_their_unit_and_key()
    {
        var e = Catalog(Cpu)["/amdcpu/0/temperature/3"]!;
        Assert.Equal("°C", e.Unit);
        Assert.Equal("Ryzen 7 9800X3D · CCD1 (Tdie)", e.DisplayName);
        Assert.Equal(new SensorKey("/amdcpu/0/temperature/3", HardwareType.Cpu, "AMD Ryzen 7 9800X3D", SensorType.Temperature, "CCD1 (Tdie)"), e.Key);
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Aliases_read_the_series_of_their_source_sensor()
    {
        var h = new HistoryStore(10, 10);
        h.Push("cpu", 10, "/amdcpu/0/load/0");
        h.Push("cpu", 20, "/amdcpu/0/load/0");
        Assert.Equal(new[] { 10f, 20 }, h.Get("cpu", 5));
        Assert.Equal(new[] { 10f, 20 }, h.Get("/amdcpu/0/load/0", 5));
        var recent = h.Recent(5);
        Assert.Same(recent["cpu"], recent["/amdcpu/0/load/0"]);
    }

    [Fact]
    public void Series_without_a_source_are_kept_under_their_alias()
    {
        var h = new HistoryStore(10, 10);
        h.Push("power", 100);
        h.Push("power", null);
        var values = h.Get("power", 5);
        Assert.Equal(100f, values[0]);
        Assert.True(float.IsNaN(values[1]));
        Assert.Equal("power", h.Resolve("power"));
    }

    [Fact]
    public void Minute_averages_follow_the_alias()
    {
        var h = new HistoryStore(10, 10);
        var t = new DateTime(2026, 10, 26, 12, 0, 0);
        h.RollMinute(t);
        h.Push("ram", 40, "/ram/load/0");
        h.RollMinute(t.AddSeconds(30));
        h.Push("ram", 60, "/ram/load/0");
        h.RollMinute(t.AddMinutes(1));
        Assert.Equal(new[] { 50f }, h.GetMinutes("ram", 5));
        var (minutes, series) = h.MinuteTable(new[] { "ram", "missing" });
        Assert.Equal(new[] { t }, minutes);
        Assert.Equal("ram", Assert.Single(series).Key);
    }
}
