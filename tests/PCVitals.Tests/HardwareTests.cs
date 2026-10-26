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
        vramMb is { } v ? new[] { new SensorReading("GPU Memory Total", SensorType.SmallData, v, v, v) } : Array.Empty<SensorReading>());

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

    [Fact]
    public void Ring_returns_newest_samples_oldest_first_across_wrap()
    {
        var ring = new HardwareService.Ring(4);
        foreach (var v in new[] { 1f, 2, 3, 4, 5, 6 }) ring.Add(v);
        Assert.Equal(new[] { 3f, 4, 5, 6 }, ring.Last(10));
        Assert.Equal(new[] { 5f, 6 }, ring.Last(2));
        Assert.Empty(new HardwareService.Ring(3).Last(5));
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
