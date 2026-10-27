using LibreHardwareMonitor.Hardware;
using Xunit;

// Fmt.Fahrenheit is process-wide state; run test classes one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PCVitals.Tests;

public class HardwareTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", "RTX 5080")]
    [InlineData("AMD Ryzen 7 9800X3D", "Ryzen 7 9800X3D")]
    [InlineData("AMD Ryzen 9 5950X 16-Core Processor", "Ryzen 9 5950X")]
    [InlineData("AMD Radeon(TM) Graphics", "Radeon Graphics")]
    [InlineData("Intel(R) Core(TM) i7-13700K", "i7-13700K")]
    [InlineData("Intel(R) UHD Graphics 770", "UHD Graphics 770")]
    [InlineData("Samsung SSD 980 1TB", "Samsung SSD 980 1TB")]
    public void Short_names(string full, string expected) => Assert.Equal(expected, HardwareService.ShortName(full));

    static HardwareReading Gpu(HardwareType type, string name, float? vramMb = null) => new(
        "/gpu/0", name, type,
        vramMb is { } v ? new[] { new SensorReading("/gpu/0/smalldata/0", "GPU Memory Total", SensorType.SmallData, v, v, v) } : Array.Empty<SensorReading>());

    [Fact]
    public void Nvidia_cards_are_discrete() => Assert.False(HardwareService.IsIntegrated(Gpu(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5080", 16303)));

    [Fact]
    public void Ryzen_integrated_graphics_is_integrated() => Assert.True(HardwareService.IsIntegrated(Gpu(HardwareType.GpuAmd, "AMD Radeon(TM) Graphics", 512)));

    [Fact]
    public void Radeon_discrete_card_is_discrete() => Assert.False(HardwareService.IsIntegrated(Gpu(HardwareType.GpuAmd, "AMD Radeon RX 7900 XTX", 24560)));

    [Fact]
    public void Intel_uhd_is_integrated() => Assert.True(HardwareService.IsIntegrated(Gpu(HardwareType.GpuIntel, "Intel(R) UHD Graphics 770")));

    [Fact]
    public void Small_dedicated_memory_means_integrated() => Assert.True(HardwareService.IsIntegrated(Gpu(HardwareType.GpuAmd, "Unknown GPU", 512)));

    // Newer iGPUs reserve gigabytes of RAM, so only their names give them away.
    [Theory]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon 780M Graphics")]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon(TM) 890M Graphics")]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon 760M")]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon 8060S Graphics")]
    [InlineData(HardwareType.GpuIntel, "Intel(R) Arc(TM) 140V GPU (16GB)")]
    [InlineData(HardwareType.GpuIntel, "Intel(R) Arc(TM) 130V GPU (8GB)")]
    [InlineData(HardwareType.GpuIntel, "Intel(R) Arc(TM) 140T GPU (16GB)")]
    public void New_integrated_gpus_with_lots_of_reserved_memory(HardwareType type, string name) =>
        Assert.True(HardwareService.IsIntegrated(Gpu(type, name, 8192)));

    [Theory]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon RX 7600M XT")]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon RX 9070 XT")]
    [InlineData(HardwareType.GpuIntel, "Intel(R) Arc(TM) A770 Graphics")]
    [InlineData(HardwareType.GpuIntel, "Intel(R) Arc(TM) B580 Graphics")]
    public void Cards_with_similar_names_stay_discrete(HardwareType type, string name) =>
        Assert.False(HardwareService.IsIntegrated(Gpu(type, name, 8192)));

    [Fact]
    public void Windows_answer_wins_over_the_name_and_memory_rules()
    {
        Assert.True(HardwareService.IsIntegrated(Gpu(HardwareType.GpuAmd, "Unknown GPU", 16384), windowsIntegrated: true));
        Assert.False(HardwareService.IsIntegrated(Gpu(HardwareType.GpuAmd, "AMD Radeon(TM) Graphics", 512), windowsIntegrated: false));
        Assert.True(HardwareService.IsIntegrated(Gpu(HardwareType.GpuNvidia, "NVIDIA N1X", 16384), windowsIntegrated: true));
    }

    static readonly GpuAdapters.Adapter[] Adapters =
    {
        new(0x1002, 0x15BF, 0x0B2B1028, true),    // Radeon 780M
        new(0x10DE, 0x2860, 0x0B2B1028, false),   // RTX 4070 Laptop
        new(0x8086, 0x56A0, 0x10201849, false),   // Arc A770, two different boards
        new(0x8086, 0x56A0, 0x60051ED8, false),
    };

    [Theory]
    [InlineData(@"\\?\PCI#VEN_1002&DEV_15BF&SUBSYS_0B2B1028&REV_C1#4&2c6a3a52&0&0041#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}", true)]
    [InlineData(@"\\?\pci#ven_10de&dev_2860&subsys_0b2b1028&rev_a1#4&1&0008#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}", false)]
    [InlineData(@"PCI\VEN_10DE&DEV_2860&SUBSYS_0B2B1028&REV_A1\FF03A962CF2DB04800", false)] // NVIDIA's form
    [InlineData(@"\\?\PCI#VEN_8086&DEV_56A0&SUBSYS_99999999&REV_08#5&1&0", false)] // unknown board: vendor + device agree
    [InlineData(@"\\?\PCI#VEN_1002&DEV_744C&SUBSYS_0E3A1002&REV_C8#6&1&0", null)] // not in the list
    [InlineData(@"\\?\ROOT#BasicRender#0000", null)]
    [InlineData(null, null)]
    public void Matches_windows_adapters_by_pci_ids(string? path, bool? expected) =>
        Assert.Equal(expected, GpuAdapters.IsIntegrated(path, Adapters));

    [Fact]
    public void Without_a_device_path_the_vendors_adapters_must_agree()
    {
        Assert.False(GpuAdapters.IsIntegrated(null, Adapters, GpuAdapters.VendorOf(HardwareType.GpuNvidia)));
        Assert.True(GpuAdapters.IsIntegrated(null, Adapters, GpuAdapters.VendorOf(HardwareType.GpuAmd)));
        var mixed = new GpuAdapters.Adapter[] { new(0x8086, 0x7D55, 0, true), new(0x8086, 0xE20B, 0, false) }; // Arc iGPU + B580
        Assert.Null(GpuAdapters.IsIntegrated(null, mixed, GpuAdapters.VendorOf(HardwareType.GpuIntel)));
    }

    static readonly string[] Displays =
    {
        @"PCI\VEN_1002&DEV_15BF&SUBSYS_0B2B1028&REV_C1\4&2C6A3A52&0&0041",
        @"PCI\VEN_10DE&DEV_2860&SUBSYS_0B2B1028&REV_A1\4&1&0008",
    };

    [Theory]
    [InlineData(@"PCI\VEN_1002&DEV_15BF&SUBSYS_0B2B1028&REV_C1\4&2C6A3A52&0&0041", 0x1002u, 0)]
    [InlineData(@"\\?\pci#ven_1002&dev_15bf&subsys_0b2b1028&rev_c1#4&2c6a3a52&0&0041#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}", 0x1002u, 0)]
    [InlineData(null, 0x10DEu, 1)] // NVIDIA without a path: the only NVIDIA adapter
    [InlineData(null, 0x8086u, -1)]
    public void Finds_the_display_adapter_instance(string? path, uint vendor, int expected) =>
        Assert.Equal(expected < 0 ? null : Displays[expected], GpuPowerState.InstanceFor(path, vendor, Displays));

    [Fact]
    public void Two_identical_cards_without_a_path_are_ambiguous()
    {
        var twins = new[] { @"PCI\VEN_10DE&DEV_2C02&SUBSYS_89DE1043&REV_A1\1", @"PCI\VEN_10DE&DEV_2C02&SUBSYS_89DE1043&REV_A1\2" };
        Assert.Null(GpuPowerState.InstanceFor(null, 0x10DE, twins));
        Assert.Equal(twins[1], GpuPowerState.InstanceFor(@"\\?\PCI#VEN_10DE&DEV_2C02&SUBSYS_89DE1043&REV_A1#2#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}", 0x10DE, twins));
    }

    [Theory]
    [InlineData(1, false)] // D0
    [InlineData(4, true)]  // D3
    [InlineData(0, false)] // unspecified
    public void Only_d3_counts_as_asleep(int state, bool asleep) => Assert.Equal(asleep, GpuPowerState.IsAsleep(state));

    static GpuSummary Summary(string id, bool integrated, bool sleeping = false) =>
        new() { Id = id, Name = id, ShortName = id, Integrated = integrated, Sleeping = sleeping };

    [Fact]
    public void A_sleeping_discrete_gpu_hands_the_main_item_to_the_igpu()
    {
        var igpu = Summary("igpu", true);
        var dgpu = Summary("dgpu", false, sleeping: true);
        Assert.Equal((igpu, dgpu), HardwareService.PickGpus(new[] { dgpu, igpu }, null));
        // Awake again: back to the discrete GPU.
        var awake = Summary("dgpu", false);
        Assert.Equal((awake, igpu), HardwareService.PickGpus(new[] { awake, igpu }, null));
        // A GPU the user chose stays chosen, asleep or not.
        Assert.Equal(dgpu, HardwareService.PickGpus(new[] { dgpu, igpu }, "dgpu").Primary);
    }

    [Fact]
    public void Disagreeing_adapters_give_no_answer() =>
        Assert.Null(GpuAdapters.IsIntegrated(@"\\?\PCI#VEN_1002&DEV_1234#1",
            new GpuAdapters.Adapter[] { new(0x1002, 0x1234, 1, true), new(0x1002, 0x1234, 2, false) }));

    [Fact]
    public void Ring_returns_newest_samples_oldest_first_across_wrap()
    {
        var ring = new HistoryStore.Ring(4);
        foreach (var v in new[] { 1f, 2, 3, 4, 5, 6 }) ring.Add(v);
        Assert.Equal(new[] { 3f, 4, 5, 6 }, ring.Last(10));
        Assert.Equal(new[] { 5f, 6 }, ring.Last(2));
        Assert.Empty(new HistoryStore.Ring(3).Last(5));
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1E1_phys_0_eng_3_engtype_3D", 1234, "3D")]
    [InlineData("pid_88_luid_0x0_0x1_phys_0_eng_0_engtype_VideoDecode", 88, "VideoDecode")]
    public void Gpu_engine_counter_names_parse(string instance, int pid, string engine)
    {
        Assert.True(GpuProcessUsage.TryParse(instance, out var p, out _, out var e));
        Assert.Equal(pid, p);
        Assert.Equal(engine, e);
    }

    [Fact]
    public void Non_process_counter_names_are_rejected() => Assert.False(GpuProcessUsage.TryParse("_Total", out _, out _, out _));
}

public class FormatTests
{
    [Fact]
    public void Temperatures_convert_to_fahrenheit_for_display_only()
    {
        try
        {
            Fmt.Fahrenheit = false;
            Assert.Equal("52°C", Fmt.Temp(52f));
            Fmt.Fahrenheit = true;
            Assert.Equal("126°F", Fmt.Temp(52f));
            Assert.Equal("212.0 °F", Fmt.Sensor(SensorType.Temperature, 100f));
            Assert.Equal("--°F", Fmt.Temp(null));
        }
        finally
        {
            Fmt.Fahrenheit = false;
        }
    }

    [Theory]
    [InlineData(512f, "512 B/s")]
    [InlineData(2048f, "2.0 KB/s")]
    [InlineData(3.5f * 1024 * 1024, "3.5 MB/s")]
    public void Rates(float bytesPerSecond, string expected) => Assert.Equal(expected, Fmt.Rate(bytesPerSecond));

    [Theory]
    [InlineData(59f, "0m 59s")]
    [InlineData(3700f, "1h 1m")]
    [InlineData(90000f, "1d 1h 0m")]
    public void Durations(float seconds, string expected) => Assert.Equal(expected, Fmt.Duration(seconds));
}
