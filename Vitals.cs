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
    /// <summary>Sensor items: the device the sensor belongs to (taskbar accent colour).</summary>
    public HardwareType? HardwareType { get; init; }
    /// <summary>Dashboard page to open when the item is clicked, when it isn't implied by <see cref="Kind"/>.</summary>
    public string? Page { get; init; }

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
        WidgetKind.Battery => "Battery",
        WidgetKind.Sensor => "Sensor",
        _ => k.ToString(),
    };

    /// <summary>The view for a taskbar item: a built-in kind, or a sensor the user picked.</summary>
    public static ItemView? ForItem(WidgetItem item, Snapshot snap, AppSettings settings) =>
        item.Kind == WidgetKind.Sensor
            ? item.Sensor is { } key ? ForSensor(snap, key, item.Label, settings) : null
            : Build(item.Kind, item.Label, snap);

    /// <summary>
    /// One sensor as a taskbar item / hover card: its value, session min and max, and history when
    /// it is tracked. A sensor that disappeared (device removed) still shows, as "--".
    /// </summary>
    public static ItemView ForSensor(Snapshot snap, SensorKey key, string? label, AppSettings? settings = null)
    {
        var entry = snap.FindSensor(key);
        string Named(string id, string fallback) => settings?.SensorName(id, fallback) ?? fallback;
        var templates = SensorFormat.Templates(key.SensorType);
        if (entry == null)
        {
            var missing = new ItemView
            {
                Kind = WidgetKind.Sensor, Title = Named(key.Id, key.SensorName), Accent = Theme.ForHardware(key.HardwareType), Label = label,
                HardwareType = key.HardwareType, Device = key.HardwareName, DeviceShort = HardwareService.ShortName(key.HardwareName),
                Primary = "--", PrimaryTemplates = templates,
                HistoryCaption = "No readings",
            };
            missing.Rows.Add(new("Status", "Not available", Theme.Warn));
            missing.Rows.Add(new("Why", "The device may be disconnected or asleep"));
            return missing;
        }

        var (hw, s) = (entry.Hardware, entry.Sensor);
        var name = Named(s.Id, s.Name);
        bool temp = s.Type == SensorType.Temperature;
        var v = new ItemView
        {
            Kind = WidgetKind.Sensor, Title = name, Accent = Theme.ForHardware(hw.Type), Label = label,
            HardwareType = hw.Type, Page = PageFor(hw),
            Device = hw.Name, DeviceShort = HardwareService.ShortName(hw.Name),
            Primary = SensorFormat.Short(s.Type, s.Value), PrimaryTemp = temp ? s.Value : null, PrimaryTemplates = templates,
            Line1 = s.Max is { } max ? "max " + SensorFormat.Short(s.Type, max) : null,
            Line2 = s.Min is { } min ? "min " + SensorFormat.Short(s.Type, min) : null,
            LineTemplates = templates.Select(t => "max " + t).ToArray(),
            History = snap.History.TryGetValue(s.Id, out var h) ? h : null,
            HistoryMax = SensorFormat.IsPercent(s.Type) || temp ? 100 : null,
            HistoryCaption = $"{name} · last 90 seconds",
            TileSub = HardwareService.ShortName(hw.Name),
        };
        Color? Tint(float? x) => temp ? Theme.TempColor(x) : null;
        v.Rows.Add(new("Now", Fmt.Sensor(s.Type, s.Value), Tint(s.Value)));
        v.Rows.Add(new("Minimum", Fmt.Sensor(s.Type, s.Min), Tint(s.Min)));
        v.Rows.Add(new("Maximum", Fmt.Sensor(s.Type, s.Max), Tint(s.Max)));
        v.Rows.Add(new("Average (this session)", Fmt.Sensor(s.Type, s.Avg)));
        if (name != s.Name) v.Rows.Add(new("Sensor", s.Name));
        return v;
    }

    /// <summary>The dashboard page that lists a device's sensors.</summary>
    public static string PageFor(HardwareReading hw) => hw.Type switch
    {
        HardwareType.Cpu => "cpu",
        HardwareType.GpuNvidia or HardwareType.GpuAmd
            or HardwareType.GpuIntel => "gpu:" + hw.Id,
        HardwareType.Memory => "memory",
        HardwareType.Storage => "disk:" + hw.Id,
        HardwareType.Network => "network",
        HardwareType.Motherboard or HardwareType.SuperIO
            or HardwareType.EmbeddedController => "board",
        HardwareType.Battery => "battery",
        _ => "hw:" + hw.Id,
    };

    public static ItemView? Build(WidgetKind kind, string? label, Snapshot snap)
    {
        var s = snap.Summary;
        float[]? H(string key) => snap.History.TryGetValue(key, out var h) ? h : null;

        switch (kind)
        {
            case WidgetKind.Battery:
                return ForBattery(snap, label);

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

    public static ItemView? ForBattery(Snapshot snap, string? label = null)
    {
        if (snap.Summary.Battery is not { } b) return null;
        bool charging = b.RateW > 0.05f || (b.PluggedIn == true && b.Charge < 99.5f);
        bool full = b.PluggedIn == true && b.Charge >= 99.5f;
        string state = full ? "Plugged in, full" : charging ? "Charging" : b.PluggedIn == true ? "Plugged in" : "On battery";
        string? time = b.RemainingSeconds is { } t && !full ? Fmt.Duration(t) : null;
        var v = new ItemView
        {
            Kind = WidgetKind.Battery, Title = "Battery", Accent = Theme.Good, Label = label,
            Device = b.Name, DeviceShort = b.Name,
            Primary = Pct(b.Charge),
            Line1 = b.RateW is { } r && Math.Abs(r) >= 0.1f ? $"{(r > 0 ? "+" : "−")}{Math.Abs(r):0.0} W" : charging ? "Charging" : full ? "Full" : null,
            Line2 = time ?? (b.PluggedIn == true ? "plugged in" : null),
            LineTemplates = new[] { "+99.9 W", "10h 59m" },
            History = snap.History.TryGetValue("battery", out var h) ? h : null, HistoryMax = 100,
            HistoryCaption = "Charge · last 90 seconds",
            TileSub = $"{state}{(time != null ? $"  ·  {time} left" : "")}",
        };
        v.Rows.Add(new("Charge", Pct1(b.Charge)));
        v.Rows.Add(new("State", state));
        if (b.RateW is { } rate) v.Rows.Add(new(rate >= 0 ? "Charging at" : "Discharging at", $"{Math.Abs(rate):0.0} W"));
        if (time != null) v.Rows.Add(new(charging ? "Time to full" : "Time remaining", time));
        if (b.Wear is { } wear) v.Rows.Add(new("Wear", $"{wear:0}%", wear >= 30 ? Theme.Warn : null));
        if (b.FullWh is { } fullWh) v.Rows.Add(new("Full capacity", b.DesignedWh is { } designed ? $"{fullWh:0.0} of {designed:0.0} Wh" : $"{fullWh:0.0} Wh"));
        if (b.Voltage is { } volts) v.Rows.Add(new("Voltage", $"{volts:0.00} V"));
        return v;
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

/// <summary>Short, unit-aware values for sensor items on the taskbar, and the widest text each type can take.</summary>
static class SensorFormat
{
    public static bool IsPercent(SensorType t) => t is SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity;

    public static string Short(SensorType t, float? value)
    {
        if (value is not { } x) return "--";
        return t switch
        {
            SensorType.Temperature => Fmt.Temp(x),
            _ when IsPercent(t) => $"{x:0}%",
            SensorType.Power => x < 100 ? $"{x:0.0} W" : $"{x:0} W",
            SensorType.Fan => $"{x:0} RPM",
            SensorType.Clock => x >= 1000 ? $"{x / 1000:0.00} GHz" : $"{x:0} MHz",
            SensorType.Voltage => $"{x:0.000} V",
            SensorType.Current => $"{x:0.0} A",
            SensorType.Throughput => Fmt.Rate(x),
            SensorType.Data => $"{x:0.0} GB",
            SensorType.SmallData => x >= 1024 ? $"{x / 1024:0.0} GB" : $"{x:0} MB",
            SensorType.TimeSpan => Fmt.Duration(x),
            SensorType.Frequency => $"{x:0} Hz",
            _ => Fmt.Sensor(t, x),
        };
    }

    /// <summary>Strings at least as wide as any value <see cref="Short"/> gives for the type, so items don't jiggle.</summary>
    public static string[] Templates(SensorType t) => t switch
    {
        SensorType.Temperature => new[] { "100°C" },
        _ when IsPercent(t) => new[] { "100%" },
        SensorType.Power => new[] { "99.9 W", "999 W" },
        SensorType.Fan => new[] { "9999 RPM" },
        SensorType.Clock => new[] { "9.99 GHz", "999 MHz" },
        SensorType.Voltage => new[] { "1.234 V" },
        SensorType.Current => new[] { "99.9 A" },
        SensorType.Throughput => new[] { "999.9 KB/s" },
        SensorType.Data => new[] { "999.9 GB" },
        SensorType.SmallData => new[] { "99.9 GB", "999 MB" },
        SensorType.TimeSpan => new[] { "10h 59m" },
        SensorType.Frequency => new[] { "999 Hz" },
        _ => new[] { "9999" },
    };

    /// <summary>A short default taskbar label for a sensor: its name, cut at a word to 8 characters.</summary>
    public static string DefaultLabel(string sensorName)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(sensorName, @"\(.*?\)|#", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var label = "";
        foreach (var w in words)
        {
            var next = label.Length == 0 ? w : label + " " + w;
            if (next.Length > 8) break;
            label = next;
        }
        if (label.Length > 0) return label;
        var first = words.FirstOrDefault() ?? sensorName;
        return first.Length > 8 ? first[..8] : first;
    }
}
