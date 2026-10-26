using LibreHardwareMonitor.Hardware;

namespace PCVitals.Core;

public sealed record SensorEntry(HardwareReading Hardware, SensorReading Sensor)
{
    public string Id => Sensor.Id;
    public SensorKey Key => SensorKey.For(Hardware, Sensor);
    public string Unit => SensorCatalog.UnitOf(Sensor.Type);
    /// <summary>"RTX 5080 · GPU Hot Spot"</summary>
    public string DisplayName => $"{HardwareService.ShortName(Hardware.Name)} · {Sensor.Name}";
}

/// <summary>Every sensor in one snapshot, by id, for pickers and for resolving saved <see cref="SensorKey"/>s.</summary>
public sealed class SensorCatalog
{
    readonly List<SensorEntry> _entries = new();
    readonly Dictionary<string, SensorEntry> _byId = new(StringComparer.Ordinal);

    public SensorCatalog(IEnumerable<HardwareReading> hardware)
    {
        foreach (var hw in hardware)
            foreach (var s in hw.Sensors)
            {
                var entry = new SensorEntry(hw, s);
                // Ids are unique per library contract; if a driver breaks that, keep the first.
                if (_byId.TryAdd(s.Id, entry)) _entries.Add(entry);
            }
    }

    public IReadOnlyList<SensorEntry> All => _entries;

    public SensorEntry? this[string id] => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The sensor a saved key refers to: the same id if it is still the same sensor, otherwise the one
    /// sensor with the same fingerprint (preferring the key's own device when several match), otherwise
    /// whatever now has that id. Null when the sensor is gone or the match would be a guess.
    /// </summary>
    public SensorEntry? Find(SensorKey key)
    {
        var byId = _byId.GetValueOrDefault(key.Id);
        if (byId != null && byId.Sensor.Type == key.SensorType &&
            byId.Sensor.Name.Equals(key.SensorName, StringComparison.OrdinalIgnoreCase))
            return byId;

        var matches = _entries.Where(e => key.SameFingerprint(e.Hardware.Type, e.Hardware.Name, e.Sensor.Type, e.Sensor.Name)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            var hw = SensorKey.HardwarePart(key.Id);
            var sameDevice = matches.Where(m => m.Hardware.Id == hw).ToList();
            return sameDevice.Count == 1 ? sameDevice[0] : null;
        }
        return byId;
    }

    /// <summary>Sensors whose device name, sensor name, type or unit contain every word of <paramref name="text"/>.</summary>
    public IEnumerable<SensorEntry> Search(string text)
    {
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return _entries;
        return _entries.Where(e => terms.All(t =>
            e.Hardware.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            e.Sensor.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            e.Sensor.Type.ToString().Contains(t, StringComparison.OrdinalIgnoreCase) ||
            (e.Unit.Length > 0 && e.Unit.Equals(t, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>The unit a sensor type is reported in (temperatures always in °C).</summary>
    public static string UnitOf(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Timing => "ns",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.Conductivity => "µS/cm",
        _ => "",
    };
}
