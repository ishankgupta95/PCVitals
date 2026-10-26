using LibreHardwareMonitor.Hardware;

namespace PCVitals.Core;

public sealed class GpuSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public bool Integrated { get; init; }
    public float? Load { get; init; }
    public float? Temp { get; init; }
    public float? HotSpot { get; init; }
    public float? MemJunction { get; init; }
    public float? Power { get; init; }
    public float? PowerLimitPct { get; init; }
    public float? CoreClock { get; init; }
    public float? MemClock { get; init; }
    public float? VramUsedMb { get; init; }
    public float? VramTotalMb { get; init; }
    public float? FanRpm { get; init; }
    public float? FanPct { get; init; }
}

public sealed class DiskSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public float? Temp { get; init; }
    public float? UsedPct { get; init; }
    public float? ReadRate { get; init; }
    public float? WriteRate { get; init; }
}

public sealed class BatterySummary
{
    public required string Name { get; init; }
    public float? Charge { get; init; }
    /// <summary>Watts into (+) or out of (−) the battery.</summary>
    public float? RateW { get; init; }
    public float? RemainingSeconds { get; init; }
    /// <summary>Capacity lost compared with new, in percent.</summary>
    public float? Wear { get; init; }
    public float? DesignedWh { get; init; }
    public float? FullWh { get; init; }
    public float? Voltage { get; init; }
    public bool? PluggedIn { get; init; }
}

/// <summary>The headline numbers shown in the taskbar, tray, tiles and hover cards.</summary>
public sealed class Summary
{
    public string? CpuName { get; init; }
    public string? CpuShortName { get; init; }
    public float? CpuLoad { get; init; }
    public float? CpuMaxCoreLoad { get; init; }
    public float? CpuTemp { get; init; }
    public float? CpuCcdTemp { get; init; }
    public float? CpuPower { get; init; }
    public float? CpuClock { get; init; }
    public float? CpuEffectiveClock { get; init; }
    public float? CpuFanRpm { get; init; }

    public IReadOnlyList<GpuSummary> Gpus { get; init; } = Array.Empty<GpuSummary>();
    /// <summary>The GPU shown as "GPU" everywhere: the user's choice, else the discrete card.</summary>
    public GpuSummary? Gpu { get; init; }
    /// <summary>The other GPU (usually the integrated one), if any.</summary>
    public GpuSummary? SecondGpu { get; init; }

    public float? RamUsedGb { get; init; }
    public float? RamTotalGb { get; init; }
    public float? RamLoad { get; init; }
    public float? VirtualUsedGb { get; init; }
    public float? VirtualTotalGb { get; init; }
    public float? DimmTemp { get; init; }

    public string? BoardName { get; init; }
    public float? ChipsetTemp { get; init; }
    public float? VrmTemp { get; init; }
    public float? SystemTemp { get; init; }
    public float? SocketTemp { get; init; }

    public float? NetDown { get; init; }
    public float? NetUp { get; init; }
    public string? NetAdapter { get; init; }

    public IReadOnlyList<DiskSummary> Disks { get; init; } = Array.Empty<DiskSummary>();

    public BatterySummary? Battery { get; init; }

    /// <summary>
    /// The sensor each headline history series was read from, by alias (e.g. "cpu" → "/amdcpu/0/load/0").
    /// Series computed from several sensors (total power, network, disk I/O) have no entry.
    /// </summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>CPU package plus discrete GPU board power: the two components that dominate system draw.</summary>
    public float? TotalPower
    {
        get
        {
            var parts = new[] { CpuPower }.Concat(Gpus.Where(g => !g.Integrated).Select(g => g.Power)).Where(p => p.HasValue).ToList();
            return parts.Count == 0 ? null : parts.Sum(p => p!.Value);
        }
    }
}

/// <param name="Hardware">Devices as the dashboard lists them: ordered, idle adapters dropped, display names.</param>
/// <param name="History">Recent samples by sensor id, and by headline alias ("cpu", "gpu:/gpu-nvidia/0"…).</param>
/// <param name="AllHardware">Every device under the name the library reports (what <see cref="SensorKey"/>s refer to).</param>
public sealed record Snapshot(
    DateTime Time,
    IReadOnlyList<HardwareReading> Hardware,
    Summary Summary,
    IReadOnlyDictionary<string, float[]> History,
    IReadOnlyList<HardwareReading> AllHardware)
{
    SensorCatalog? _catalog;

    /// <summary>Every sensor in this snapshot by id; built on first use.</summary>
    public SensorCatalog Catalog => _catalog ??= new SensorCatalog(AllHardware);
}
