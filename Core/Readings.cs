using LibreHardwareMonitor.Hardware;

namespace PCVitals.Core;

/// <param name="Id">
/// Stable identifier from LibreHardwareMonitor (e.g. <c>/amdcpu/0/temperature/2</c>), or one under
/// <c>/pcvitals/...</c> for readings PC Vitals adds itself.
/// </param>
/// <param name="Avg">Mean of the readings since PC Vitals started or the sensor's min/max were last reset.</param>
public sealed record SensorReading(string Id, string Name, SensorType Type, float? Value, float? Min, float? Max, float? Avg = null);

public sealed record HardwareReading(string Id, string Name, HardwareType Type, IReadOnlyList<SensorReading> Sensors)
{
    public SensorReading? Find(SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var exact = Sensors.FirstOrDefault(s => s.Type == type && s.Value.HasValue &&
                                                    s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var name in names)
        {
            var partial = Sensors.FirstOrDefault(s => s.Type == type && s.Value.HasValue &&
                                                      s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (partial != null) return partial;
        }
        return null;
    }

    public float? Value(SensorType type, params string[] names) => Find(type, names)?.Value;

    public SensorReading? FirstSensor(SensorType type) => Sensors.FirstOrDefault(s => s.Type == type && s.Value.HasValue);

    public float? First(SensorType type) => FirstSensor(type)?.Value;
}

/// <summary>
/// A persistable reference to one sensor. Resolved by <see cref="Id"/> first; if that no longer
/// exists (sensor indices shift after some driver or library updates) by the fingerprint of
/// hardware type, hardware name, sensor type and sensor name.
/// </summary>
public sealed record SensorKey(string Id, HardwareType HardwareType, string HardwareName, SensorType SensorType, string SensorName)
{
    public static SensorKey For(HardwareReading hw, SensorReading s) => new(s.Id, hw.Type, hw.Name, s.Type, s.Name);

    public bool SameFingerprint(HardwareType hwType, string hwName, SensorType type, string name) =>
        hwType == HardwareType && type == SensorType &&
        hwName.Equals(HardwareName, StringComparison.OrdinalIgnoreCase) &&
        name.Equals(SensorName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "/amdcpu/0/temperature/2" → "/amdcpu/0": the id of the device a sensor belongs to (also for
    /// PC Vitals' own "/pcvitals/&lt;device id&gt;/..." readings).
    /// </summary>
    public static string HardwarePart(string sensorId)
    {
        // Sensor ids are "<hardware id>/<sensor type>/<index>".
        int last = sensorId.LastIndexOf('/');
        int type = last > 0 ? sensorId.LastIndexOf('/', last - 1) : -1;
        var hw = type > 0 ? sensorId[..type] : sensorId;
        return hw.StartsWith(SyntheticIds.Prefix + "/", StringComparison.Ordinal) ? hw[SyntheticIds.Prefix.Length..] : hw;
    }
}

/// <summary>Ids for readings PC Vitals adds itself, so they can be referred to like any other sensor.</summary>
public static class SyntheticIds
{
    public const string Prefix = "/pcvitals";

    public static string For(string hardwareId, string sensor) => Prefix + hardwareId + "/" + sensor;
}
