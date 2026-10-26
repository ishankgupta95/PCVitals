using LibreHardwareMonitor.Hardware;

namespace PCVitals;

sealed record DetailRow(string Label, string Value, Color? Color = null);

/// <summary>
/// Display-ready view of one vital (CPU, GPU, ...). The taskbar widget draws Label/Primary/Line1/Line2,
/// the hover card adds Device/Rows/History, and the dashboard tiles use TileSub.
/// </summary>
sealed class ItemView
{
    public required WidgetKind Kind { get; init; }
    public required string Title { get; init; }
    public required Color Accent { get; init; }
    public string? Device { get; init; }
    public string? DeviceShort { get; init; }
    public string? Tag { get; init; }
    public string? Label { get; init; }

    public string? Primary { get; init; }
    public float? PrimaryTemp { get; init; }
    public string[] PrimaryTemplates { get; init; } = { "100%" };

    public string? Line1 { get; init; }
    public float? Line1Temp { get; init; }
    public string? Line2 { get; init; }
    public string[] LineTemplates { get; init; } = { "100°C", "999 W" };

    public float[]? History { get; init; }
    public float? HistoryMax { get; init; }
    public string HistoryCaption { get; init; } = "Last 90 seconds";
    public string? TileSub { get; init; }

    public List<DetailRow> Rows { get; } = new();
}

static class Vitals
{
    public static string DisplayName(WidgetKind k) => k switch
    {
        WidgetKind.Cpu => "Processor",
        WidgetKind.Gpu => "Graphics card (primary GPU)",
        WidgetKind.SecondGpu => "Second GPU (integrated)",
        WidgetKind.Memory => "Memory",
        WidgetKind.Network => "Network",
        WidgetKind.Power => "Power draw (CPU + GPU)",
        WidgetKind.Board => "Motherboard / chipset",
        WidgetKind.Disk => "Storage",
        _ => k.ToString(),
    };

    public static ItemView? Build(WidgetKind kind, string? label, Snapshot snap)
    {
        var s = snap.Summary;
        float[]? H(string key) => snap.History.TryGetValue(key, out var h) ? h : null;

        switch (kind)
        {
            case WidgetKind.Cpu:
            {
                if (s.CpuName == null) return null;
                var v = new ItemView
                {
                    Kind = kind, Title = "CPU", Accent = Theme.Cpu, Label = label,
                    Device = s.CpuName, DeviceShort = s.CpuShortName,
                    Primary = Pct(s.CpuLoad), Line1 = Deg(s.CpuTemp), Line1Temp = s.CpuTemp, Line2 = Watts(s.CpuPower),
                    History = H("cpu"), HistoryMax = 100, HistoryCaption = "Usage · last 90 seconds",
                    TileSub = $"{Watts(s.CpuPower)}  ·  {Clock(s.CpuClock)}",
                };
                v.Rows.Add(new("Usage", Pct1(s.CpuLoad)));
                if (s.CpuMaxCoreLoad is { } mc) v.Rows.Add(new("Busiest thread", $"{mc:0}%"));
                v.Rows.Add(new("Temperature", Deg1(s.CpuTemp), Theme.TempColor(s.CpuTemp)));
                if (s.CpuCcdTemp is { } ccd) v.Rows.Add(new("Die (CCD) temperature", Deg1(ccd), Theme.TempColor(ccd)));
                v.Rows.Add(new("Package power", Watts1(s.CpuPower)));
                v.Rows.Add(new("Average clock", Clock(s.CpuClock)));
                if (s.CpuEffectiveClock is { } ec) v.Rows.Add(new("Effective clock", Clock(ec)));
                if (s.CpuFanRpm is { } fan) v.Rows.Add(new("CPU fan", $"{fan:0} RPM"));
                return v;
            }

            case WidgetKind.Gpu:
                return Gpu(kind, s.Gpu, label, snap);

            case WidgetKind.SecondGpu:
                return Gpu(kind, s.SecondGpu, label, snap);

            case WidgetKind.Memory:
            {
                if (s.RamTotalGb == null) return null;
                var v = new ItemView
                {
                    Kind = kind, Title = "Memory", Accent = Theme.Ram, Label = label,
                    Device = ModulesText(snap) ?? "System memory", DeviceShort = $"{s.RamTotalGb:0} GB",
                    Primary = Pct(s.RamLoad), Line1 = $"{s.RamUsedGb:0.0} GB", Line2 = $"of {s.RamTotalGb:0} GB",
                    LineTemplates = new[] { "99.9 GB", "of 999 GB" },
                    History = H("ram"), HistoryMax = 100, HistoryCaption = "Usage · last 90 seconds",
                    TileSub = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB",
                };
                v.Rows.Add(new("In use", $"{s.RamUsedGb:0.0} GB"));
                v.Rows.Add(new("Available", $"{s.RamTotalGb - s.RamUsedGb:0.0} GB"));
                v.Rows.Add(new("Usage", Pct1(s.RamLoad)));
                if (s.VirtualTotalGb is { } vt) v.Rows.Add(new("Committed (incl. page file)", $"{s.VirtualUsedGb:0.0} / {vt:0.0} GB"));
                if (s.DimmTemp is { } dt) v.Rows.Add(new("Module temperature", Deg1(dt), Theme.TempColor(dt)));
                return v;
            }

            case WidgetKind.Network:
            {
                if (s.NetDown == null) return null;
                var v = new ItemView
                {
                    Kind = kind, Title = "Network", Accent = Theme.Net, Label = label,
                    Device = s.NetAdapter ?? "Network", DeviceShort = s.NetAdapter,
                    Primary = null, PrimaryTemplates = Array.Empty<string>(),
                    Line1 = "↑ " + Fmt.Rate(s.NetUp ?? 0), Line2 = "↓ " + Fmt.Rate(s.NetDown ?? 0),
                    LineTemplates = new[] { "↓ 999.9 KB/s" },
                    History = H("net"), HistoryCaption = "Throughput · last 90 seconds",
                    TileSub = "↑ " + Fmt.Rate(s.NetUp ?? 0),
                };
                v.Rows.Add(new("Download", Fmt.Rate(s.NetDown ?? 0)));
                v.Rows.Add(new("Upload", Fmt.Rate(s.NetUp ?? 0)));
                var adapter = snap.Hardware.FirstOrDefault(h => h.Type == HardwareType.Network && h.Name == s.NetAdapter);
                if (adapter?.Value(SensorType.Data, "Data Downloaded") is { } dl) v.Rows.Add(new("Downloaded this session", $"{dl:0.0} GB"));
                if (adapter?.Value(SensorType.Data, "Data Uploaded") is { } ul) v.Rows.Add(new("Uploaded this session", $"{ul:0.0} GB"));
                return v;
            }

            case WidgetKind.Power:
            {
                if (s.TotalPower == null) return null;
                var v = new ItemView
                {
                    Kind = kind, Title = "Power", Accent = Theme.Power, Label = label,
                    Device = "CPU package + graphics card",
                    Primary = Watts(s.TotalPower), PrimaryTemplates = new[] { "999 W" },
                    Line1 = $"CPU {Fmt.Num(s.CpuPower)}", Line2 = $"GPU {Fmt.Num(s.Gpu?.Power)}",
                    LineTemplates = new[] { "GPU 999" },
                    History = H("power"), HistoryCaption = "Power · last 90 seconds",
                    TileSub = $"CPU {Fmt.Num(s.CpuPower)}  ·  GPU {Fmt.Num(s.Gpu?.Power)}",
                };
                v.Rows.Add(new("CPU package", Watts1(s.CpuPower)));
                foreach (var g in s.Gpus)
                    v.Rows.Add(new($"{g.ShortName}{(g.Integrated ? " (integrated, in CPU)" : "")}", Watts1(g.Power)));
                v.Rows.Add(new("Total (CPU + discrete GPU)", Watts1(s.TotalPower), Theme.Power));
                return v;
            }

            case WidgetKind.Board:
            {
                var temp = s.ChipsetTemp ?? s.SystemTemp;
                if (temp == null && s.BoardName == null) return null;
                var v = new ItemView
                {
                    Kind = kind, Title = "Board", Accent = Theme.Board, Label = label,
                    Device = s.BoardName ?? "Motherboard", DeviceShort = s.BoardName,
                    Primary = Deg(temp), PrimaryTemp = temp, PrimaryTemplates = new[] { "100°C" },
                    Line1 = s.VrmTemp is { } vrm ? $"VRM {Fmt.TempValue(vrm):0}°" : null,
                    Line2 = s.SystemTemp is { } sys ? $"SYS {Fmt.TempValue(sys):0}°" : null,
                    LineTemplates = new[] { "VRM 100°" },
                    History = H("chipset"), HistoryMax = 100, HistoryCaption = "Chipset temperature · last 90 seconds",
                    TileSub = $"VRM {Fmt.Temp(s.VrmTemp)}  ·  System {Fmt.Temp(s.SystemTemp)}",
                };
                if (s.ChipsetTemp is { } ch) v.Rows.Add(new("Chipset", Deg1(ch), Theme.TempColor(ch)));
                if (s.VrmTemp is { } vr) v.Rows.Add(new("VRM", Deg1(vr), Theme.TempColor(vr)));
                if (s.SystemTemp is { } st) v.Rows.Add(new("System", Deg1(st), Theme.TempColor(st)));
                if (s.SocketTemp is { } so) v.Rows.Add(new("CPU socket", Deg1(so), Theme.TempColor(so)));
                foreach (var fan in snap.Hardware.Where(h => h.Type is HardwareType.SuperIO or HardwareType.EmbeddedController)
                                                 .SelectMany(h => h.Sensors).Where(x => x.Type == SensorType.Fan && x.Value > 0))
                    v.Rows.Add(new(fan.Name, $"{fan.Value:0} RPM"));
                return v;
            }

            case WidgetKind.Disk:
            {
                if (s.Disks.Count == 0) return null;
                var hottest = s.Disks.Select(d => d.Temp).DefaultIfEmpty(null).Max();
                var read = s.Disks.Sum(d => d.ReadRate ?? 0);
                var write = s.Disks.Sum(d => d.WriteRate ?? 0);
                var v = new ItemView
                {
                    Kind = kind, Title = "Storage", Accent = Theme.Storage, Label = label,
                    Device = s.Disks.Count == 1 ? s.Disks[0].Name : $"{s.Disks.Count} drives",
                    DeviceShort = s.Disks.Count == 1 ? s.Disks[0].Name : $"{s.Disks.Count} drives",
                    Primary = Deg(hottest), PrimaryTemp = hottest, PrimaryTemplates = new[] { "100°C" },
                    Line1 = "R " + Fmt.Rate(read), Line2 = "W " + Fmt.Rate(write),
                    LineTemplates = new[] { "W 999.9 MB/s" },
                    History = H("disk"), HistoryMax = 100, HistoryCaption = "Hottest drive · last 90 seconds",
                    TileSub = $"R {Fmt.Rate(read)}  ·  W {Fmt.Rate(write)}",
                };
                foreach (var d in s.Disks)
                    v.Rows.Add(new(d.Name, $"{Fmt.Temp(d.Temp)}  ·  {Fmt.Num(d.UsedPct)}% full", Theme.TempColor(d.Temp)));
                v.Rows.Add(new("Read", Fmt.Rate(read)));
                v.Rows.Add(new("Write", Fmt.Rate(write)));
                return v;
            }
        }
        return null;
    }

    /// <summary>One drive's page/tile (the taskbar's Disk item aggregates all drives instead).</summary>
    public static ItemView ForDisk(DiskSummary d, Snapshot snap)
    {
        var hw = snap.Hardware.FirstOrDefault(h => h.Id == d.Id);
        var v = new ItemView
        {
            Kind = WidgetKind.Disk, Title = "Drive", Accent = Theme.Storage,
            Device = d.Name, DeviceShort = d.Name,
            Primary = Deg(d.Temp), PrimaryTemp = d.Temp,
            Line1 = "R " + Fmt.Rate(d.ReadRate ?? 0), Line2 = "W " + Fmt.Rate(d.WriteRate ?? 0),
            History = snap.History.TryGetValue("disk:" + d.Id, out var h) ? h : null, HistoryMax = 100,
            TileSub = $"{Fmt.Num(d.UsedPct)}% full",
        };
        v.Rows.Add(new("Temperature", Deg1(d.Temp), Theme.TempColor(d.Temp)));
        if (d.UsedPct is { } used) v.Rows.Add(new("Space used", $"{used:0.0}%"));
        v.Rows.Add(new("Read", Fmt.Rate(d.ReadRate ?? 0)));
        v.Rows.Add(new("Write", Fmt.Rate(d.WriteRate ?? 0)));
        if (hw?.Value(SensorType.Load, "Total Activity") is { } act) v.Rows.Add(new("Active time", $"{act:0}%"));
        if (hw?.Value(SensorType.Data, "Data Read") is { } dr) v.Rows.Add(new("Data read (lifetime)", Fmt.Sensor(SensorType.Data, dr)));
        if (hw?.Value(SensorType.Data, "Data Written") is { } dw) v.Rows.Add(new("Data written (lifetime)", Fmt.Sensor(SensorType.Data, dw)));
        if (hw?.Value(SensorType.Level, "Percentage Used") is { } wear) v.Rows.Add(new("Wear (life used)", $"{wear:0}%", wear > 80 ? Theme.Hot : null));
        if (hw?.Value(SensorType.Level, "Available Spare") is { } spare) v.Rows.Add(new("Available spare", $"{spare:0}%"));
        return v;
    }

    /// <summary>Anything without a dedicated view (battery, PSU, cooler…): its first few sensors.</summary>
    public static ItemView ForHardware(HardwareReading hw)
    {
        var v = new ItemView
        {
            Kind = WidgetKind.Board, Title = Theme.Badge(hw.Type), Accent = Theme.ForHardware(hw.Type),
            Device = hw.Name, DeviceShort = hw.Name,
        };
        foreach (var s in hw.Sensors.Take(12))
            v.Rows.Add(new(s.Name, Fmt.Sensor(s.Type, s.Value), s.Type == SensorType.Temperature ? Theme.TempColor(s.Value) : null));
        return v;
    }

    public static ItemView? ForGpu(GpuSummary g, Snapshot snap) => Gpu(WidgetKind.Gpu, g, null, snap);

    static ItemView? Gpu(WidgetKind kind, GpuSummary? g, string? label, Snapshot snap)
    {
        if (g == null) return null;
        var v = new ItemView
        {
            Kind = kind, Title = g.Integrated ? "iGPU" : "GPU", Accent = g.Integrated ? Theme.IGpu : Theme.Gpu, Label = label,
            Device = g.Name, DeviceShort = g.ShortName, Tag = g.Integrated ? "Integrated" : "Discrete",
            Primary = Pct(g.Load), Line1 = Deg(g.Temp), Line1Temp = g.Temp, Line2 = Watts(g.Power),
            History = snap.History.TryGetValue("gpu:" + g.Id, out var h) ? h : null, HistoryMax = 100,
            HistoryCaption = "Usage · last 90 seconds",
            TileSub = g.VramTotalMb is { } total && !g.Integrated
                ? $"{Watts(g.Power)}  ·  {(g.VramUsedMb ?? 0) / 1024:0.0}/{total / 1024:0} GB VRAM"
                : Watts(g.Power),
        };
        v.Rows.Add(new("Usage", Pct1(g.Load)));
        v.Rows.Add(new("Temperature", Deg1(g.Temp), Theme.TempColor(g.Temp)));
        if (g.HotSpot is { } hs) v.Rows.Add(new("Hot spot", Deg1(hs), Theme.TempColor(hs)));
        if (g.MemJunction is { } mj) v.Rows.Add(new("Memory junction", Deg1(mj), Theme.TempColor(mj - 10)));
        v.Rows.Add(new("Power", g.PowerLimitPct is { } pl ? $"{Watts1(g.Power)}  ({pl:0}% of limit)" : Watts1(g.Power)));
        if (g.CoreClock is { } cc) v.Rows.Add(new("Core clock", Clock(cc)));
        if (g.MemClock is { } mcl) v.Rows.Add(new("Memory clock", Clock(mcl)));
        if (g.VramTotalMb is { } vt)
            v.Rows.Add(new(g.Integrated ? "Reserved memory" : "VRAM", $"{Fmt.Sensor(SensorType.SmallData, g.VramUsedMb)} / {Fmt.Sensor(SensorType.SmallData, vt)}"));
        if (g.FanRpm is { } rpm)
            v.Rows.Add(new("Fans", rpm < 1 ? "Stopped (zero-RPM mode)" : $"{rpm:0} RPM{(g.FanPct is { } fp ? $"  ·  {fp:0}%" : "")}"));
        return v;
    }

    /// <summary>"2 × G Skill Intl - F5-6000J3238F16G (16 GB)" from the per-DIMM SPD readings.</summary>
    static string? ModulesText(Snapshot snap)
    {
        var dimms = snap.Hardware.Where(h => h.Type == HardwareType.Memory && h.Name.Contains("(#")).ToList();
        if (dimms.Count == 0) return null;
        var name = dimms[0].Name[..dimms[0].Name.LastIndexOf("(#", StringComparison.Ordinal)].Trim();
        var cap = dimms[0].Value(SensorType.Data, "Capacity");
        return $"{dimms.Count} × {name}{(cap is { } c ? $" ({c:0} GB)" : "")}";
    }

    public static string Pct(float? v) => v is { } x ? $"{x:0}%" : "--%";
    static string Pct1(float? v) => v is { } x ? $"{x:0.0}%" : "—";
    public static string Deg(float? v) => Fmt.Temp(v);
    static string Deg1(float? v) => v is { } x ? $"{Fmt.TempValue(x):0.0} {Fmt.TempUnit}" : "— (needs admin)";
    public static string Watts(float? v) => v is { } x ? $"{x:0} W" : "-- W";
    static string Watts1(float? v) => v is { } x ? $"{x:0.0} W" : "—";
    static string Clock(float? mhz) => mhz is { } x ? (x >= 1000 ? $"{x / 1000:0.00} GHz" : $"{x:0} MHz") : "-- GHz";
}
