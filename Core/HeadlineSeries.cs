namespace PCVitals.Core;

/// <summary>Names and units of the history series, for saved history and CSV exports.</summary>
public static class HeadlineSeries
{
    static readonly Dictionary<string, (string Name, string Unit)> Fixed = new()
    {
        ["cpu"] = ("CPU usage", "%"),
        ["cputemp"] = ("CPU temperature", "°C"),
        ["cpupower"] = ("CPU power", "W"),
        ["ram"] = ("Memory usage", "%"),
        ["power"] = ("CPU + GPU power", "W"),
        ["net"] = ("Network (download + upload)", "B/s"),
        ["netdown"] = ("Download", "B/s"),
        ["netup"] = ("Upload", "B/s"),
        ["chipset"] = ("Chipset temperature", "°C"),
        ["battery"] = ("Battery charge", "%"),
        ["batteryrate"] = ("Battery charge rate", "W"),
        ["disk"] = ("Hottest drive temperature", "°C"),
        ["ping"] = ("Ping latency", "ms"),
        ["pingjitter"] = ("Ping jitter", "ms"),
        ["pingloss"] = ("Ping loss", "%"),
    };

    /// <summary>The headline series, in the order exports list them (per-device ones for the devices in <paramref name="summary"/>).</summary>
    public static List<string> Keys(Summary? summary)
    {
        var keys = new List<string> { "cpu", "cputemp", "cpupower", "ram", "power", "netdown", "netup", "chipset" };
        foreach (var g in summary?.Gpus ?? Array.Empty<GpuSummary>())
            keys.AddRange(new[] { "gpu:" + g.Id, "gputemp:" + g.Id, "gpupower:" + g.Id });
        foreach (var d in summary?.Disks ?? Array.Empty<DiskSummary>())
            keys.Add("disk:" + d.Id);
        if (summary?.Battery != null) keys.Add("battery");
        if (summary?.Ping != null) keys.AddRange(new[] { "ping", "pingjitter", "pingloss" });
        return keys;
    }

    public static bool IsHeadline(string key) => !key.StartsWith('/');

    /// <summary>"GPU temperature"-style name and unit for a series key (a headline alias or a sensor id).</summary>
    public static (string Name, string Unit) Describe(string key, Snapshot? snap)
    {
        if (Fixed.TryGetValue(key, out var known)) return known;
        int colon = key.IndexOf(':');
        if (colon > 0)
        {
            var (kind, id) = (key[..colon], key[(colon + 1)..]);
            var s = snap?.Summary;
            string gpu = s?.Gpus.FirstOrDefault(g => g.Id == id)?.ShortName ?? "GPU";
            string disk = s?.Disks.FirstOrDefault(d => d.Id == id)?.Name ?? "Drive";
            switch (kind)
            {
                case "gpu": return ($"{gpu} usage", "%");
                case "gputemp": return ($"{gpu} temperature", "°C");
                case "gpupower": return ($"{gpu} power", "W");
                case "disk": return ($"{disk} temperature", "°C");
                case "diskio": return ($"{disk} read + write", "B/s");
            }
        }
        if (snap?.Catalog[key] is { } entry) return (entry.DisplayName, entry.Unit);
        return (key, "");
    }
}
