using LibreHardwareMonitor.Hardware;
using Xunit;

namespace PCVitals.Tests;

public class DriveHealthTests
{
    static DriveHealth Nvme(int cw = 0, float spare = 100, float threshold = 10, float used = 3, ulong media = 0, HealthLevel reported = HealthLevel.Good) =>
        DriveHealthRules.Assess(new DriveHealthInput
        {
            IsNvme = true, Reported = reported, CriticalWarning = cw, AvailableSpare = spare, SpareThreshold = threshold,
            PercentageUsed = used, MediaErrors = media,
        });

    static DriveHealth Sata(ulong realloc = 0, ulong pending = 0, ulong uncorrectable = 0, ulong crc = 0, float? life = null, HealthLevel reported = HealthLevel.Good) =>
        DriveHealthRules.Assess(new DriveHealthInput
        {
            Reported = reported, Reallocated = realloc, Pending = pending, Uncorrectable = uncorrectable, CrcErrors = crc, LifePercent = life,
        });

    [Fact]
    public void Healthy_drives_are_good_without_notes()
    {
        Assert.Equal((HealthLevel.Good, 0), (Nvme().Level, Nvme().Notes.Count));
        Assert.Equal(HealthLevel.Good, Sata(life: 95).Level);
    }

    [Theory]
    [InlineData(0x01, HealthLevel.Bad, "spare")]
    [InlineData(0x04, HealthLevel.Bad, "reliability")]
    [InlineData(0x08, HealthLevel.Bad, "read-only")]
    [InlineData(0x10, HealthLevel.Bad, "power-loss")]
    [InlineData(0x02, HealthLevel.Caution, "too hot")]
    public void Nvme_critical_warning_bits(int bits, HealthLevel level, string reason)
    {
        var h = Nvme(cw: bits);
        Assert.Equal(level, h.Level);
        Assert.Contains(h.Notes, n => n.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Spare_below_threshold_is_bad_even_without_the_warning_bit()
    {
        var h = Nvme(spare: 5, threshold: 10);
        Assert.Equal(HealthLevel.Bad, h.Level);
        Assert.Contains("5%", h.Notes[0]);
    }

    [Theory]
    [InlineData(95f, HealthLevel.Caution)]  // 5% endurance left
    [InlineData(100f, HealthLevel.Caution)] // rated endurance used up
    [InlineData(120f, HealthLevel.Caution)]
    [InlineData(80f, HealthLevel.Good)]
    public void Wear(float percentageUsed, HealthLevel level) => Assert.Equal(level, Nvme(used: percentageUsed).Level);

    [Fact]
    public void Media_errors_are_caution() => Assert.Equal(HealthLevel.Caution, Nvme(media: 2).Level);

    [Fact]
    public void Sata_sector_problems_are_caution_with_counts()
    {
        var h = Sata(realloc: 8, pending: 1);
        Assert.Equal(HealthLevel.Caution, h.Level);
        Assert.Contains(h.Notes, n => n.StartsWith("1 sector is unstable"));
        Assert.Contains(h.Notes, n => n.StartsWith("8 damaged sectors have"));
        Assert.Equal(HealthLevel.Caution, Sata(uncorrectable: 3).Level);
    }

    [Fact]
    public void Cable_errors_are_only_noted()
    {
        var h = Sata(crc: 12);
        Assert.Equal(HealthLevel.Good, h.Level);
        Assert.Contains("cable", Assert.Single(h.Notes));
    }

    [Fact]
    public void Never_better_than_the_drives_own_verdict()
    {
        var h = Sata(reported: HealthLevel.Bad);
        Assert.Equal(HealthLevel.Bad, h.Level);
        Assert.Contains("SMART status", Assert.Single(h.Notes));
    }

    [Fact]
    public void Without_smart_data_health_is_unknown() =>
        Assert.Equal(HealthLevel.Unknown, DriveHealthRules.Assess(new DriveHealthInput()).Level);

    [Fact]
    public void Attributes_map_by_id()
    {
        var nvme = DriveHealthInput.FromAttributes(true, HealthLevel.Good, null, new (byte, ulong)[] { (0x01, 4), (0x03, 100), (0x04, 10), (0x05, 7), (0x0E, 2) });
        Assert.Equal((4, 100f, 10f, 7f, 2UL), (nvme.CriticalWarning, nvme.AvailableSpare, nvme.SpareThreshold, nvme.PercentageUsed, nvme.MediaErrors));
        var sata = DriveHealthInput.FromAttributes(false, HealthLevel.Good, 90, new (byte, ulong)[] { (0x05, 3), (0xC5, 1), (0xC6, 0), (0xC7, 9) });
        Assert.Equal((3UL, 1UL, 0UL, 9UL, 90f), (sata.Reallocated, sata.Pending, sata.Uncorrectable, sata.CrcErrors, sata.LifePercent));
        Assert.Null(sata.CriticalWarning);
    }

    [Fact]
    public void Volume_free_percent() => Assert.Equal(25f, new VolumeInfo('C', 400, 100).FreePercent);
}

public class NetworkTests
{
    [Fact]
    public void Ping_stats_average_jitter_and_loss()
    {
        var s = PingStats.From(new float?[] { 10, 14, null, 12, null });
        Assert.Equal(12f, s.AvgMs);
        Assert.Equal(3f, s.JitterMs); // |14-10| and |12-14|
        Assert.Equal(40f, s.LossPercent);
        Assert.Equal(5, s.Samples);
    }

    [Fact]
    public void All_lost_or_single_reply()
    {
        var lost = PingStats.From(new float?[] { null, null });
        Assert.Null(lost.AvgMs);
        Assert.Equal(100f, lost.LossPercent);
        var one = PingStats.From(new float?[] { 8 });
        Assert.Equal(8f, one.AvgMs);
        Assert.Null(one.JitterMs);
        Assert.Equal(0, PingStats.From(Array.Empty<float?>()).Samples);
    }

    [Fact]
    public void Wifi_interface_matches_the_library_network_id()
    {
        var wifi = new WifiStatus(Guid.Parse("2201123E-D5B9-409D-8183-B5BBC26FBD67"), "Home", 72, 866, 866);
        Assert.True(wifi.Matches("/nic/%7B2201123E-D5B9-409D-8183-B5BBC26FBD67%7D"));
        Assert.False(wifi.Matches("/nic/%7B7FD493BD-3F1F-4CA4-8DFD-978F79598FB1%7D"));
    }

    [Fact]
    public void Drive_health_and_ping_can_be_watched_by_alerts()
    {
        var disks = new[]
        {
            new DiskSummary { Id = "/nvme/0", Name = "A", Health = new DriveHealth(HealthLevel.Good, Array.Empty<string>()) },
            new DiskSummary { Id = "/nvme/1", Name = "B", Health = new DriveHealth(HealthLevel.Caution, new[] { "x" }) },
        };
        var snap = new Snapshot(DateTime.Now, Array.Empty<HardwareReading>(),
                                new Summary { Disks = disks, Ping = new PingStats(23, 2, 25, 4) }, new Dictionary<string, float[]>(), Array.Empty<HardwareReading>());
        var health = new AlertRule { Series = "diskhealth", Threshold = 1 };
        Assert.Equal((float)HealthLevel.Caution, health.ValueIn(snap));
        Assert.Equal("Caution", AlertText.Value(health, 2));
        Assert.Equal("above Good for 15 s", AlertText.Condition(health));
        Assert.Equal(0.5f, health.Margin);
        Assert.Equal(23f, new AlertRule { Series = "ping" }.ValueIn(snap));
        Assert.Equal("23 ms", AlertText.Value(new AlertRule { Series = "ping" }, 23.4f));
        Assert.Equal(25f, new AlertRule { Series = "pingloss" }.ValueIn(snap));
        Assert.Null(new AlertRule { Series = "ping" }.ValueIn(new Snapshot(DateTime.Now, Array.Empty<HardwareReading>(), new Summary(),
                                                                          new Dictionary<string, float[]>(), Array.Empty<HardwareReading>())));
    }
}
