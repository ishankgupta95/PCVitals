using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace PCVitals.Core;

/// <summary>A lettered volume on a drive.</summary>
public sealed record VolumeInfo(char Letter, ulong TotalBytes, ulong FreeBytes)
{
    public float FreePercent => TotalBytes == 0 ? 0 : (float)(FreeBytes * 100.0 / TotalBytes);
}

public enum HealthLevel
{
    Unknown,
    Good,
    Caution,
    Bad,
}

/// <param name="Notes">Plain-language reasons, worst first; may hold notes even when Good (e.g. cable errors).</param>
public sealed record DriveHealth(HealthLevel Level, IReadOnlyList<string> Notes)
{
    public static readonly DriveHealth Unknown = new(HealthLevel.Unknown, Array.Empty<string>());

    public string Word => Level switch
    {
        HealthLevel.Good => "Good",
        HealthLevel.Caution => "Caution",
        HealthLevel.Bad => "Bad",
        _ => "Unknown",
    };
}

/// <summary>What a drive reports about its condition (nulls where it doesn't report something).</summary>
public sealed record DriveHealthInput
{
    public bool IsNvme { get; init; }
    /// <summary>The drive's own verdict as worked out by the SMART library.</summary>
    public HealthLevel Reported { get; init; }
    /// <summary>Remaining rated life, 100 = new.</summary>
    public float? LifePercent { get; init; }
    // NVMe health log
    public int? CriticalWarning { get; init; }
    public float? AvailableSpare { get; init; }
    public float? SpareThreshold { get; init; }
    public float? PercentageUsed { get; init; }
    public ulong? MediaErrors { get; init; }
    // SATA SMART
    public ulong? Reallocated { get; init; }
    public ulong? Pending { get; init; }
    public ulong? Uncorrectable { get; init; }
    public ulong? CrcErrors { get; init; }

    /// <summary>
    /// Reads the inputs from SMART attribute ids and raw values. NVMe health-log fields use the
    /// pseudo-ids the SMART library assigns them (01 critical warning … 0E media errors); SATA uses
    /// the standard attribute ids.
    /// </summary>
    public static DriveHealthInput FromAttributes(bool isNvme, HealthLevel reported, float? life, IEnumerable<(byte Id, ulong Raw)> attributes)
    {
        var raw = new Dictionary<byte, ulong>();
        foreach (var (id, value) in attributes) raw.TryAdd(id, value);
        ulong? Get(byte id) => raw.TryGetValue(id, out var v) ? v : null;
        return isNvme
            ? new DriveHealthInput
            {
                IsNvme = true, Reported = reported, LifePercent = life,
                CriticalWarning = (int?)Get(0x01), AvailableSpare = Get(0x03), SpareThreshold = Get(0x04),
                PercentageUsed = Get(0x05), MediaErrors = Get(0x0E),
            }
            : new DriveHealthInput
            {
                Reported = reported, LifePercent = life,
                Reallocated = Get(0x05), Pending = Get(0xC5), Uncorrectable = Get(0xC6), CrcErrors = Get(0xC7),
            };
    }
}

public static class DriveHealthRules
{
    /// <summary>
    /// Good / Caution / Bad with reasons. Anything that means data may already be at risk is Bad;
    /// early warning signs are Caution; connection (cable) errors are only noted.
    /// </summary>
    public static DriveHealth Assess(DriveHealthInput d)
    {
        var bad = new List<string>();
        var caution = new List<string>();
        var notes = new List<string>();

        if (d.CriticalWarning is { } cw && cw != 0)
        {
            if ((cw & 0x01) != 0) bad.Add("The drive reports its spare blocks have run low.");
            if ((cw & 0x04) != 0) bad.Add("The drive reports its reliability is degraded by media or internal errors.");
            if ((cw & 0x08) != 0) bad.Add("The drive has switched itself to read-only.");
            if ((cw & 0x10) != 0) bad.Add("The drive's power-loss protection has failed.");
            if ((cw & 0x02) != 0) caution.Add("The drive reports it has been too hot or too cold.");
        }
        if (d.AvailableSpare is { } spare && d.SpareThreshold is { } threshold && threshold > 0 && spare < threshold && ((d.CriticalWarning ?? 0) & 0x01) == 0)
            bad.Add($"Spare blocks are at {spare:0}%, below the drive's {threshold:0}% minimum.");
        if (d.MediaErrors is > 0)
            caution.Add($"{d.MediaErrors} data integrity error{Plural(d.MediaErrors)} recorded: data couldn't be read back correctly.");
        if (d.Uncorrectable is > 0)
            caution.Add($"{d.Uncorrectable} sector{Plural(d.Uncorrectable)} couldn't be read even after retrying.");
        if (d.Pending is > 0)
            caution.Add($"{d.Pending} sector{Plural(d.Pending)} {(d.Pending == 1 ? "is" : "are")} unstable and waiting to be remapped.");
        if (d.Reallocated is > 0)
            caution.Add($"{d.Reallocated} damaged sector{Plural(d.Reallocated)} {(d.Reallocated == 1 ? "has" : "have")} been replaced with spares.");

        // Wear: NVMe "percentage used" can pass 100; other drives report remaining life.
        float? life = d.PercentageUsed is { } used ? Math.Max(0, 100 - used) : d.LifePercent;
        if (life is { } l)
        {
            if (l <= 0) caution.Add("The drive has used up its rated write endurance; it may keep working, but keep backups.");
            else if (l <= 10) caution.Add($"Only {l:0}% of the drive's rated write endurance is left.");
        }
        if (d.CrcErrors is > 0)
            notes.Add($"{d.CrcErrors} transfer error{Plural(d.CrcErrors)} on the cable or connector (not the drive itself); reseat or replace the cable if the count keeps rising.");

        var level = bad.Count > 0 ? HealthLevel.Bad : caution.Count > 0 ? HealthLevel.Caution : HealthLevel.Good;
        // The drive's own SMART verdict can see attributes these rules don't; never report better than it.
        if (d.Reported > level)
        {
            (d.Reported == HealthLevel.Bad ? bad : caution).Add("The drive's own SMART status reports a problem.");
            level = d.Reported;
        }
        if (level == HealthLevel.Good && d.Reported == HealthLevel.Unknown && life == null && d.CriticalWarning == null && d.Reallocated == null)
            return new DriveHealth(HealthLevel.Unknown, notes); // nothing to judge (no SMART access)
        return new DriveHealth(level, bad.Concat(caution).Concat(notes).ToList());
    }

    static string Plural(ulong? n) => n == 1 ? "" : "s";
}

/// <summary>Volumes and health of a drive, read from the library's storage device (poll thread only).</summary>
public sealed record DriveDetails(IReadOnlyList<VolumeInfo> Volumes, DriveHealth Health)
{
    public static readonly DriveDetails None = new(Array.Empty<VolumeInfo>(), DriveHealth.Unknown);

    /// <summary>"C:, D:"</summary>
    public string Letters => string.Join(", ", Volumes.Select(v => v.Letter + ":"));

    public static DriveDetails Read(IHardware hw)
    {
        if (hw is not StorageDevice { Storage: { } storage }) return None;
        var volumes = storage.Partitions
            .Where(p => p.DriveLetter is { } c && char.IsLetter(c) && p.AvailableFreeSpace != null && p.PartitionLength > 0)
            .Select(p => new VolumeInfo(char.ToUpperInvariant(p.DriveLetter!.Value), (ulong)p.PartitionLength, (ulong)p.AvailableFreeSpace!.Value))
            .OrderBy(v => v.Letter)
            .ToList();
        var smart = storage.Smart;
        var reported = smart?.DiskStatus switch
        {
            DiskInfoToolkit.Enums.Interop.DiskStatus.Good => HealthLevel.Good,
            DiskInfoToolkit.Enums.Interop.DiskStatus.Caution => HealthLevel.Caution,
            DiskInfoToolkit.Enums.Interop.DiskStatus.Bad => HealthLevel.Bad,
            _ => HealthLevel.Unknown,
        };
        var attributes = smart?.SmartAttributes?.Select(a => (a.Info.ID, a.Attribute.RawValueULong)) ?? Enumerable.Empty<(byte, ulong)>();
        var input = DriveHealthInput.FromAttributes(storage.IsNVMe, reported, smart?.Life, attributes);
        return new DriveDetails(volumes, DriveHealthRules.Assess(input));
    }
}
