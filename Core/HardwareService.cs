using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace PCVitals.Core;

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> and polls it on a background thread.
/// All sensor access happens on that thread; the UI only ever sees immutable <see cref="Snapshot"/>s.
/// </summary>
public sealed class HardwareService : IDisposable
{
    public const int HistoryLength = 90;
    /// <summary>Samples kept for the dashboard's longest graph range (15 minutes at 1 s; ~3.6 KB per series).</summary>
    public const int LongHistoryLength = 900;
    /// <summary>One-minute averages kept for the 1 h / 24 h graphs and CSV export (~5.6 KB per series).</summary>
    public const int MinuteHistoryLength = 1440;

    static readonly HardwareType[] DisplayOrder =
    {
        HardwareType.Cpu, HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel,
        HardwareType.Memory, HardwareType.Motherboard, HardwareType.SuperIO, HardwareType.EmbeddedController,
        HardwareType.Cooler, HardwareType.Psu, HardwareType.Storage, HardwareType.Network,
        HardwareType.Battery, HardwareType.PowerMonitor,
    };

    // "Radeon 780M / 890M / 8060S Graphics" and "Arc 140V / 140T" are integrated; "Radeon RX 7600M" and "Arc A770" are cards.
    static readonly Regex IntegratedGpuName = new(@"Radeon(\(TM\))? Graphics$|Radeon Vega|Radeon(\(TM\))? \d{3,4}[MS]\b|UHD Graphics|Iris|Intel\(R\) Graphics|Arc\(TM\) (Graphics|\d{3}[VT]\b)",
                                                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Drives, USB/HID controllers, PSUs and batteries are slow to discover and not needed for the
    // first taskbar reading, so they're enabled after it (see EnableDeferredGroups).
    readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsNetworkEnabled = true,
    };
    readonly HashSet<IHardware> _seen = new();
    bool _deferredEnabled;

    readonly HistoryStore _history = new(LongHistoryLength, MinuteHistoryLength);
    ISnapshotConsumer[] _consumers = Array.Empty<ISnapshotConsumer>();
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _running;
    volatile Snapshot? _latest;
    bool _opened;

    public Snapshot? Latest => _latest;
    public int IntervalMs { get; set; } = 1000;
    /// <summary>Hardware id of the GPU to treat as primary, or null to prefer the discrete card.</summary>
    public string? PreferredGpuId { get; set; }
    /// <summary>True while the dashboard is open: poll every device on every tick.</summary>
    public bool DetailedMode { get; set; }
    /// <summary>True when a visible item shows drive stats, so drives are polled more often.</summary>
    public bool NeedStorage { get; set; }
    /// <summary>True when the board item is on the taskbar, so the sensor chip is polled every tick.</summary>
    public bool NeedBoard { get; set; }
    /// <summary>True when the second-GPU item is on the taskbar, so the non-primary GPU is polled every tick.</summary>
    public bool NeedSecondGpu { get; set; }
    /// <summary>True when the battery item is on the taskbar, so the battery is polled every tick.</summary>
    public bool NeedBattery { get; set; }
    long _tick;
    string? _primaryGpuId;

    public HistoryStore History => _history;

    /// <summary>
    /// Adds a consumer that receives every snapshot on the poll thread, after those added before it.
    /// Add consumers before <see cref="Start"/>.
    /// </summary>
    public void AddConsumer(ISnapshotConsumer consumer) => _consumers = _consumers.Append(consumer).ToArray();

    // ---- NVML-free GPU power ----------------------------------------------------------------
    // NVML commits ~20 MB the moment it initialises and never gives it back, yet the only thing the
    // taskbar needs from it is board power. NVAPI (already loaded) reports power as a percentage of
    // the card's power target, so with a per-card watts-per-percent ratio we can show watts without
    // NVML. The ratio is learned from real NVML readings: by a short-lived calibration process on
    // first run, and continuously whenever NVML is loaded (the dashboard loads it for PCIe stats).

    readonly bool _lean;
    bool _nvmlBlocked;
    readonly NvmlFeed _nvmlFeed = new();
    readonly Dictionary<string, (float Min, float Max)> _estimatedRange = new();

    /// <summary>Watts per 1% of NVAPI's "GPU Power" reading, keyed by GPU name.</summary>
    public Dictionary<string, float> PowerPerPercent { get; } = new();
    /// <summary>Raised on the poller thread when a ratio was learned or refined.</summary>
    public event Action? PowerCalibrationChanged;
    /// <summary>True while board power is estimated (NVML not loaded).</summary>
    public bool GpuPowerEstimated => _nvmlBlocked;

    /// <param name="lean">Skip NVML until the dashboard needs it (the tray app); diagnostics load everything.</param>
    public HardwareService(bool lean = false) => _lean = lean;

    /// <summary>
    /// Whether the PawnIO driver (needed for CPU temperature/power and motherboard sensors) is
    /// installed, and its version. Null when it can't be determined.
    /// </summary>
    public static (bool? Installed, string? Version) PawnIoStatus()
    {
        try
        {
            bool installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
            string? version = null;
            if (installed)
            {
                try { version = LibreHardwareMonitor.PawnIo.PawnIo.Version?.ToString(); }
                catch { }
            }
            return (installed, version);
        }
        catch
        {
            return (null, null);
        }
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sensor poller" };
        _thread.Start();
    }

    /// <summary>Wakes the poller early, e.g. after a setting changed.</summary>
    public void Poke() => _wake.Set();

    // ---- Commands and tracked sensors -------------------------------------------------------

    readonly System.Collections.Concurrent.ConcurrentQueue<Action<IComputer>> _commands = new();

    /// <summary>
    /// Runs <paramref name="command"/> on the poll thread at the start of the next poll (the library
    /// isn't thread-safe), and wakes the poller so it happens promptly.
    /// </summary>
    public void Enqueue(Action<IComputer> command)
    {
        _commands.Enqueue(command);
        Poke();
    }

    /// <summary>Clears a sensor's min, max and session average.</summary>
    public void ResetMinMax(string sensorId) => Enqueue(computer =>
    {
        _averages.Remove(sensorId);
        if (sensorId.StartsWith("/pcvitals/", StringComparison.Ordinal))
            _estimatedRange.Remove(SensorKey.HardwarePart(sensorId));
        foreach (var sensor in AllSensors(computer.Hardware))
        {
            if (IdOf(sensor) != sensorId) continue;
            sensor.ResetMin();
            sensor.ResetMax();
        }
    });

    static IEnumerable<ISensor> AllSensors(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            foreach (var s in hw.Sensors) yield return s;
            foreach (var s in AllSensors(hw.SubHardware)) yield return s;
        }
    }

    void RunCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try { command(_computer); }
            catch (Exception ex) { LogOnce("command:" + ex.GetType().Name, "Sensor command failed: " + ex); }
        }
    }

    SensorKey[] _tracked = Array.Empty<SensorKey>();
    readonly Dictionary<SensorKey, string> _trackedIds = new();
    readonly HashSet<string> _trackedDevices = new();
    long _trackedRetryTick;

    /// <summary>
    /// Sensors to keep history for and poll as often as the headline ones (pinned to the taskbar,
    /// favourites). Settable from any thread.
    /// </summary>
    public IReadOnlyList<SensorKey> TrackedSensors
    {
        get => Volatile.Read(ref _tracked);
        set
        {
            Volatile.Write(ref _tracked, value.ToArray());
            Poke();
        }
    }

    /// <summary>Records history for tracked sensors and notes which top-level devices they live on.</summary>
    void RecordTracked(List<HardwareReading> all, Summary summary)
    {
        var tracked = Volatile.Read(ref _tracked);
        _trackedDevices.Clear();
        if (tracked.Length == 0) return;
        SensorCatalog? catalog = null;
        foreach (var key in tracked)
        {
            var id = _trackedIds.GetValueOrDefault(key, key.Id);
            var reading = all.FirstOrDefault(h => h.Id == SensorKey.HardwarePart(id))?.Sensors
                             .FirstOrDefault(s => s.Id == id && s.Type == key.SensorType);
            // Gone or moved (index shift after an update): look it up again, at most every 30 polls.
            if (reading == null && (_trackedRetryTick == 0 || _tick - _trackedRetryTick >= 30 || !_trackedIds.ContainsKey(key)))
            {
                _trackedRetryTick = _tick;
                catalog ??= new SensorCatalog(all);
                if (catalog.Find(key) is { } entry)
                {
                    _trackedIds[key] = entry.Id;
                    reading = entry.Sensor;
                }
                else
                {
                    _trackedIds[key] = key.Id;
                }
            }
            if (reading == null) continue;
            if (_topOf.TryGetValue(SensorKey.HardwarePart(reading.Id), out var top)) _trackedDevices.Add(top);
            // Headline series already record their source sensor.
            if (!summary.Sources.Values.Contains(reading.Id)) _history.Push(reading.Id, reading.Value);
        }
    }

    void Loop()
    {
        while (_running)
        {
            var started = Environment.TickCount64;
            Snapshot? snap = null;
            try
            {
                snap = UpdateOnce();
            }
            catch (Exception ex)
            {
                // A misbehaving sensor driver must never take the tray app down, but leave a trace.
                LogOnce("poll:" + ex.GetType().Name + ex.TargetSite?.Name, "Sensor poll failed (continuing): " + ex);
            }
            if (snap != null)
                foreach (var consumer in _consumers)
                {
                    // One failing consumer must not starve the others.
                    try { consumer.OnSnapshot(snap); }
                    catch (Exception ex) { LogOnce("consumer:" + consumer.GetType().Name + ex.GetType().Name, $"{consumer.GetType().Name} failed (continuing): {ex}"); }
                }
            var elapsed = (int)(Environment.TickCount64 - started);
            _wake.WaitOne(Math.Max(50, IntervalMs - elapsed));
        }
    }

    static readonly HashSet<string> Logged = new();

    /// <summary>Writes a diagnostic line to the app log the first time <paramref name="key"/> is seen.</summary>
    internal static void LogOnce(string key, string message)
    {
        lock (Logged)
            if (!Logged.Add(key)) return;
        Program.Log(message);
    }

    public Snapshot UpdateOnce()
    {
        if (!_opened)
        {
            if (_lean) _nvmlBlocked = NvmlBlocker.Block();
            _computer.Open();
            _opened = true;
            // The memory/CPU optimisations reach into LibreHardwareMonitor internals. If an update
            // changed them the app still works, just heavier; make that visible in the log.
            if (_lean && _computer.Hardware.Any(h => h.HardwareType == HardwareType.GpuNvidia))
            {
                if (!_nvmlBlocked) LogOnce("nvml-block", "Couldn't keep NVML unloaded (LibreHardwareMonitor internals changed?); expect ~20 MB more memory.");
                if (!NvmlGate.IsAvailable) LogOnce("nvml-gate", "NVML power shortcut unavailable; background GPU polls will be slower.");
            }
        }
        else if (!_deferredEnabled)
        {
            EnableDeferredGroups();
        }
        // The dashboard shows everything, including NVML-only PCIe throughput: stream it from a
        // helper process while the dashboard is open, so NVML's memory leaves with the helper.
        if (_nvmlBlocked) _nvmlFeed.SetWanted(DetailedMode, _computer.Hardware.Any(h => h.HardwareType == HardwareType.GpuNvidia));
        RunCommands();
        UpdateHardware();

        ReadWifi();
        var all = new List<HardwareReading>();
        foreach (var hw in _computer.Hardware) Collect(hw, all);

        // A card without NVAPI's power percentage can't be estimated: fall back to NVML for it.
        // Checked once at startup only, so a single missed reading can't trigger it later.
        if (_nvmlBlocked && _tick == 1 && all.Any(h => h.Type == HardwareType.GpuNvidia && h.Find(SensorType.Load, "GPU Power") == null))
            LoadNvml("GPU reports no power percentage");

        var latency = Latency;
        var summary = Summarize(all, PreferredGpuId, _drives, _windowsIntegrated, _wifi.FirstOrDefault(), latency?.Stats, latency?.Target);
        _primaryGpuId = summary.Gpu?.Id;

        var display = all
            .Where(r => r.Sensors.Count > 0 && !IsIdleNetwork(r))
            .Select(r => r.Type == HardwareType.SuperIO && summary.BoardName != null ? r with { Name = $"{summary.BoardName} · {r.Name}" } : r)
            .OrderBy(r => Rank(r.Type))
            .ToList();

        // Close out the previous minute before this sample starts accumulating into the new one.
        _history.RollMinute(DateTime.Now);
        void Push(string alias, float? value) => _history.Push(alias, value, summary.Sources.GetValueOrDefault(alias));
        Push("cpu", summary.CpuLoad);
        Push("cputemp", summary.CpuTemp);
        Push("ram", summary.RamLoad);
        Push("power", summary.TotalPower);
        Push("net", summary.NetDown + summary.NetUp);
        Push("netdown", summary.NetDown);
        Push("netup", summary.NetUp);
        Push("cpupower", summary.CpuPower);
        Push("chipset", summary.ChipsetTemp ?? summary.SystemTemp);
        if (summary.Battery != null)
        {
            Push("battery", summary.Battery.Charge);
            Push("batteryrate", summary.Battery.RateW);
        }
        Push("disk", summary.Disks.Select(d => d.Temp).DefaultIfEmpty(null).Max());
        foreach (var d in summary.Disks)
        {
            Push("disk:" + d.Id, d.Temp);
            Push("diskio:" + d.Id, (d.ReadRate ?? 0) + (d.WriteRate ?? 0));
        }
        foreach (var g in summary.Gpus)
        {
            Push("gpu:" + g.Id, g.Load);
            Push("gputemp:" + g.Id, g.Temp);
            Push("gpupower:" + g.Id, g.Power);
        }
        if (summary.Ping is { } ping)
        {
            Push("ping", ping.AvgMs);
            Push("pingjitter", ping.JitterMs);
            Push("pingloss", ping.LossPercent);
        }
        RecordTracked(all, summary);

        // Snapshots carry the short window the taskbar/tiles draw; the dashboard asks for longer ranges.
        var snap = new Snapshot(DateTime.Now, display, summary, _history.Recent(HistoryLength), all);
        _latest = snap;
        return snap;
    }

    /// <summary>
    /// Polls each device as often as anything on screen needs it. Drive SMART queries and DIMM SPD
    /// reads over SMBus are by far the most expensive and change slowly, so they're throttled unless
    /// the dashboard is open.
    /// </summary>
    void UpdateHardware()
    {
        _tick++;
        foreach (var hw in _computer.Hardware)
        {
            // Always read a device the first time we see it so it never shows up empty.
            bool first = _seen.Add(hw);
            int everyMs = hw.HardwareType switch
            {
                HardwareType.Storage => DetailedMode ? 0 : NeedStorage ? 5000 : 30000,
                HardwareType.Memory when hw.Sensors.Any(s => s.SensorType == SensorType.Timing) => DetailedMode ? 5000 : 15000,
                HardwareType.Motherboard => DetailedMode || NeedBoard ? 0 : 3000,
                HardwareType.Battery => DetailedMode || NeedBattery ? 0 : 5000,
                HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
                    when _primaryGpuId != null && IdOf(hw) != _primaryGpuId => DetailedMode || NeedSecondGpu ? 0 : 5000,
                _ => 0,
            };
            // A sensor pinned to the taskbar should update like the built-in items (drives stay at 5 s).
            if (everyMs > 0 && _trackedDevices.Contains(IdOf(hw)))
                everyMs = hw.HardwareType == HardwareType.Storage ? Math.Min(everyMs, 5000) : 0;
            if (!first && everyMs > 0)
            {
                int ticks = Math.Max(1, everyMs / Math.Max(1, IntervalMs));
                if (_tick % ticks != 0) continue;
            }
            UpdateDevice(hw, DetailedMode);
            if (hw.HardwareType == HardwareType.Storage) ReadDriveDetails(hw);
        }
    }

    /// <summary>Volumes and health per drive id, refreshed whenever the drive itself is read (poll thread only).</summary>
    readonly Dictionary<string, DriveDetails> _drives = new();

    /// <summary>Whether Windows calls each GPU integrated (null: it doesn't say), asked once per GPU (poll thread only).</summary>
    readonly Dictionary<string, bool?> _windowsIntegrated = new();

    void AskWindowsIfIntegrated(IHardware hw, string id)
    {
        if (_windowsIntegrated.ContainsKey(id)) return;
        string? path = null;
        try { path = (hw as LibreHardwareMonitor.Hardware.Gpu.GenericGpu)?.DeviceId; }
        catch (Exception) { }
        // Read again for each new GPU, so one plugged in later (eGPU) is found too.
        var adapters = GpuAdapters.Read();
        var answer = _windowsIntegrated[id] = GpuAdapters.IsIntegrated(path, adapters, GpuAdapters.VendorOf(hw.HardwareType));
        LogOnce("igpu:" + id, $"{hw.Name}: Windows says {(answer is { } a ? a ? "integrated" : "discrete" : "nothing")} " +
                              $"({adapters.Count} adapters, device {path ?? "unknown"}).");
    }

    List<WifiStatus> _wifi = new();
    long _wifiTick = long.MinValue / 2;

    /// <summary>Opt-in ping (set by the app when the user turns it on).</summary>
    public LatencyMonitor? Latency { get; set; }

    /// <summary>Wi-Fi status every 5 s (every poll while the dashboard is open): a local query, no network traffic.</summary>
    void ReadWifi()
    {
        if (!DetailedMode && _tick - _wifiTick < Math.Max(1, 5000 / Math.Max(1, IntervalMs))) return;
        _wifiTick = _tick;
        try
        {
            _wifi = Wlan.Query();
        }
        catch (Exception ex)
        {
            LogOnce("wifi:" + ex.GetType().Name, "Couldn't read Wi-Fi status: " + ex.Message);
            _wifi = new();
        }
    }

    void ReadDriveDetails(IHardware hw)
    {
        try
        {
            _drives[IdOf(hw)] = DriveDetails.Read(hw);
        }
        catch (Exception ex)
        {
            LogOnce("drive-details:" + ex.GetType().Name, $"Couldn't read volumes or health of {hw.Name}: {ex.Message}");
        }
    }

    /// <summary>Updates one device, skipping NVML's slow PCIe sampling unless the dashboard is open.</summary>
    internal static void UpdateDevice(IHardware hw, bool detailed)
    {
        if (detailed) hw.Update();
        else D3DGate.Update(hw);
        foreach (var sub in hw.SubHardware) UpdateDevice(sub, detailed);
    }

    /// <summary>
    /// The per-engine Direct3D counters (D3D 3D / Copy / Video…, ~20 per GPU) cost several ms per GPU
    /// per poll and are only shown with "advanced sensors" in the dashboard; the headline GPU load
    /// comes from NVAPI/ADL instead. Background polls hide the D3D device id so the library skips them.
    /// </summary>
    static class D3DGate
    {
        static readonly Dictionary<Type, FieldInfo?> Fields = new();
        static readonly HashSet<Type> Broken = new();

        public static void Update(IHardware hw)
        {
            var type = hw.GetType();
            if (!Fields.TryGetValue(type, out var field))
            {
                Fields[type] = field = type.GetField("_d3dDeviceId", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null && hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                    LogOnce("d3d:" + type.Name, $"D3D counter shortcut unavailable for {type.Name}; background GPU polls will be slower.");
            }
            if (field == null || Broken.Contains(type) || field.GetValue(hw) is not string id)
            {
                Inner(hw);
                return;
            }
            field.SetValue(hw, null);
            try
            {
                Inner(hw);
            }
            catch (Exception ex)
            {
                Broken.Add(type); // library changed shape; stop gating this type
                LogOnce("d3d-broken:" + type.Name, $"D3D counter shortcut disabled for {type.Name}: {ex.Message}");
                field.SetValue(hw, id);
                Inner(hw);
                return;
            }
            finally
            {
                field.SetValue(hw, id);
            }
        }

        static void Inner(IHardware hw)
        {
            if (NvmlGate.Applies(hw)) NvmlGate.UpdateWithoutPcie(hw);
            else hw.Update();
        }
    }

    /// <summary>
    /// NVML's PCIe throughput query blocks for ~20 ms per direction while it samples the bus, which
    /// made the NVIDIA card ~80% of every poll. The library makes all NVML calls behind one device
    /// handle, so for background polls we hide that handle during Update() (skipping the sampling)
    /// and read the one NVML value we still need, board power, ourselves. PCIe throughput keeps
    /// updating normally whenever the dashboard is open.
    /// </summary>
    static class NvmlGate
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly Type? GpuType = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Gpu.NvidiaGpu");
        static readonly FieldInfo? DeviceField = GpuType?.GetField("_nvmlDevice", Private);
        static readonly FieldInfo? PowerField = GpuType?.GetField("_powerUsage", Private);
        static readonly FieldInfo? HandleField = DeviceField == null ? null
            : Nullable.GetUnderlyingType(DeviceField.FieldType)?.GetField("Handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly bool Available = DeviceField != null && PowerField != null && HandleField != null && LoadNvml();
        public static bool IsAvailable => Available;

        public static bool Applies(IHardware hw) => Available && hw.GetType() == GpuType;

        public static void UpdateWithoutPcie(IHardware hw)
        {
            var device = DeviceField!.GetValue(hw);
            if (device == null)
            {
                hw.Update();
                return;
            }
            DeviceField.SetValue(hw, null);
            try
            {
                hw.Update();
            }
            finally
            {
                DeviceField.SetValue(hw, device);
            }

            if (HandleField!.GetValue(device) is IntPtr handle && handle != IntPtr.Zero &&
                nvmlDeviceGetPowerUsage(handle, out uint milliwatts) == 0 &&
                PowerField!.GetValue(hw) is ISensor sensor)
            {
                sensor.GetType().GetProperty("Value")?.SetValue(sensor, (float?)(milliwatts / 1000f));
            }
        }

        static bool LoadNvml()
        {
            try
            {
                return NativeLibrary.TryLoad("nvml.dll", typeof(NvmlGate).Assembly, DllImportSearchPath.System32, out _);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    }

    /// <summary>Public so diagnostics can force full discovery up front.</summary>
    public void EnableDeferredGroups()
    {
        _deferredEnabled = true;
        _computer.IsStorageEnabled = true;
        _computer.IsControllerEnabled = true;
        _computer.IsPsuEnabled = true;
        _computer.IsBatteryEnabled = true;
    }

    static int Rank(HardwareType type)
    {
        var i = Array.IndexOf(DisplayOrder, type);
        return i < 0 ? DisplayOrder.Length : i;
    }

    /// <summary>The newest <paramref name="count"/> samples of a series (oldest first), for the dashboard graphs.</summary>
    public float[] GetHistory(string key, int count) => _history.Get(key, count);

    /// <summary>The newest <paramref name="count"/> one-minute averages of a series (oldest first).</summary>
    public float[] GetMinuteHistory(string key, int count) => _history.GetMinutes(key, count);

    /// <summary>
    /// Writes the one-minute averages (up to 24 hours) as CSV: a timestamp column, then one column
    /// per headline series, named for the device. Returns the number of rows written.
    /// </summary>
    public int ExportCsv(string path)
    {
        var s = _latest?.Summary;
        var columns = new List<(string Key, string Title)>
        {
            ("cpu", "CPU usage (%)"), ("cputemp", "CPU temperature (°C)"), ("cpupower", "CPU power (W)"),
            ("ram", "Memory usage (%)"), ("power", "CPU + GPU power (W)"),
            ("netdown", "Download (B/s)"), ("netup", "Upload (B/s)"), ("chipset", "Chipset temperature (°C)"),
        };
        foreach (var g in s?.Gpus ?? Array.Empty<GpuSummary>())
        {
            columns.Add(("gpu:" + g.Id, $"{g.ShortName} usage (%)"));
            columns.Add(("gputemp:" + g.Id, $"{g.ShortName} temperature (°C)"));
            columns.Add(("gpupower:" + g.Id, $"{g.ShortName} power (W)"));
        }
        foreach (var d in s?.Disks ?? Array.Empty<DiskSummary>())
            columns.Add(("disk:" + d.Id, $"{d.Name} temperature (°C)"));

        var (stamps, series) = _history.MinuteTable(columns.Select(c => c.Key));
        var titles = columns.ToDictionary(c => c.Key, c => c.Title);
        var data = series.Select(x => (Title: titles[x.Key], x.Values)).ToList();

        static string Quote(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        w.WriteLine(string.Join(",", new[] { "Time" }.Concat(data.Select(d => Quote(d.Title)))));
        for (int i = 0; i < stamps.Length; i++)
        {
            var cells = new List<string> { stamps[i].ToString("yyyy-MM-dd HH:mm", inv) };
            foreach (var (_, values) in data)
            {
                // Series that appeared later are shorter; align them to the newest minutes.
                int j = values.Length - (stamps.Length - i);
                cells.Add(j >= 0 && !float.IsNaN(values[j]) ? values[j].ToString("0.##", inv) : "");
            }
            w.WriteLine(string.Join(",", cells));
        }
        return stamps.Length;
    }

    // Identifier.ToString() builds a new string each call; sensors keep their identifier for life.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISensor, string> SensorIds = new();
    static string IdOf(ISensor s) => SensorIds.GetValue(s, x => x.Identifier.ToString());
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IHardware, string> HardwareIds = new();
    static string IdOf(IHardware h) => HardwareIds.GetValue(h, x => x.Identifier.ToString());

    /// <summary>Running sum and count per sensor id, for the session average (poll thread only).</summary>
    readonly Dictionary<string, (double Sum, long Count)> _averages = new();
    /// <summary>Device id → id of the top-level device it is polled through (sub-hardware like Super I/O chips).</summary>
    readonly Dictionary<string, string> _topOf = new();

    float? Average(string id, float? value)
    {
        ref var acc = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_averages, id, out _);
        if (value is { } v)
        {
            acc.Sum += v;
            acc.Count++;
        }
        return acc.Count > 0 ? (float)(acc.Sum / acc.Count) : null;
    }

    SensorReading Synthetic(string hardwareId, string sensor, string name, SensorType type, float? value, float? min = null, float? max = null)
    {
        var id = SyntheticIds.For(hardwareId, sensor);
        return new SensorReading(id, name, type, value, min, max, Average(id, value));
    }

    void Collect(IHardware hw, List<HardwareReading> into, string? top = null)
    {
        var sensors = hw.Sensors
            .Where(s => s.Value.HasValue)
            .OrderBy(s => (int)s.SensorType).ThenBy(s => s.Index)
            .Select(s =>
            {
                var id = IdOf(s);
                var value = Clean(s.Value);
                return new SensorReading(id, s.Name, s.SensorType, value, Clean(s.Min), Clean(s.Max), Average(id, value));
            })
            .ToList();
        var reading = new HardwareReading(IdOf(hw), hw.Name, hw.HardwareType, sensors);
        top ??= reading.Id;
        _topOf.TryAdd(reading.Id, top);
        // Free space per lettered volume, as readings of their own (pin, alert and history like any sensor).
        if (hw.HardwareType == HardwareType.Storage && _drives.TryGetValue(reading.Id, out var drive))
            foreach (var v in drive.Volumes)
                sensors.Add(Synthetic(reading.Id, $"level/free-{char.ToLowerInvariant(v.Letter)}", $"{v.Letter}: free space", SensorType.Level, v.FreePercent));
        if (hw.HardwareType == HardwareType.Network && _wifi.FirstOrDefault(w => w.Matches(reading.Id)) is { } wifi)
            sensors.Add(Synthetic(reading.Id, "level/wifi-signal", "Wi-Fi signal", SensorType.Level, wifi.SignalPercent));
        if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel) AskWindowsIfIntegrated(hw, reading.Id);
        if (hw.HardwareType == HardwareType.GpuNvidia)
        {
            if (_nvmlFeed.TryGet(hw.Name, out var feed))
            {
                // Same names LibreHardwareMonitor uses when it has NVML itself.
                sensors.Add(Synthetic(reading.Id, "power/package", "GPU Package", SensorType.Power, feed.Watts));
                sensors.Add(Synthetic(reading.Id, "throughput/pcie-rx", "GPU PCIe Rx", SensorType.Throughput, feed.RxBytes));
                sensors.Add(Synthetic(reading.Id, "throughput/pcie-tx", "GPU PCIe Tx", SensorType.Throughput, feed.TxBytes));
            }
            CalibrateOrEstimatePower(reading, sensors);
        }
        into.Add(reading);
        foreach (var sub in hw.SubHardware) Collect(sub, into, top);
    }

    void CalibrateOrEstimatePower(HardwareReading gpu, List<SensorReading> sensors)
    {
        var pct = gpu.Value(SensorType.Load, "GPU Power");
        if (pct is not > 0) return;
        var real = gpu.Find(SensorType.Power, "GPU Package")?.Value;

        if (real is > 0)
        {
            // Learn from real readings; ignore near-idle samples where rounding dominates.
            if (pct < 3) return;
            float sample = real.Value / pct.Value;
            // Sanity: real cards land around 1–10 W per percent; anything else is a bad reading.
            if (!float.IsFinite(sample) || sample < 0.2f || sample > 50f) return;
            bool changed;
            lock (PowerPerPercent)
            {
                PowerPerPercent.TryGetValue(gpu.Name, out var old);
                float next = old <= 0 ? sample : old * 0.9f + sample * 0.1f;
                changed = old <= 0 || Math.Abs(next - old) / old > 0.01f;
                PowerPerPercent[gpu.Name] = next;
            }
            if (changed) PowerCalibrationChanged?.Invoke();
            return;
        }

        float ratio;
        lock (PowerPerPercent)
            if (!PowerPerPercent.TryGetValue(gpu.Name, out ratio) || ratio <= 0) return;
        float watts = pct.Value * ratio;
        var range = _estimatedRange.TryGetValue(gpu.Id, out var r) ? (Math.Min(r.Min, watts), Math.Max(r.Max, watts)) : (watts, watts);
        _estimatedRange[gpu.Id] = range;
        sensors.Add(Synthetic(gpu.Id, "power/board", "GPU Board Power", SensorType.Power, watts, range.Item1, range.Item2));
    }

    /// <summary>Loads NVML (permanently) by re-creating the GPU devices with it available.</summary>
    void LoadNvml(string reason)
    {
        if (_lean) Program.Log($"Loading NVML ({reason}); +~20 MB until restart.");
        _nvmlBlocked = false;
        NvmlBlocker.Unblock();
        _computer.IsGpuEnabled = false;
        _computer.IsGpuEnabled = true;
    }

    /// <summary>Keeps LibreHardwareMonitor from initialising NVML by marking it as already initialised.</summary>
    static class NvmlBlocker
    {
        static readonly PropertyInfo? IsAvailable = typeof(Computer).Assembly
            .GetType("LibreHardwareMonitor.Interop.NvidiaML")
            ?.GetProperty("IsAvailable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        public static bool Block()
        {
            try
            {
                if (IsAvailable?.SetMethod == null || IsAvailable.GetValue(null) is true) return false;
                IsAvailable.SetValue(null, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Unblock()
        {
            try { IsAvailable?.SetValue(null, false); }
            catch { }
        }
    }

    static float? Clean(float? v) => v is { } x && (float.IsNaN(x) || float.IsInfinity(x)) ? null : v;

    static float? Positive(float? v) => v > 0 ? v : null;

    static BatterySummary? SummarizeBattery(HardwareReading? b, Dictionary<string, string> sources)
    {
        if (b == null) return null;
        // LibreHardwareMonitor reports whichever of these the battery driver supports.
        float? rate = b.Value(SensorType.Power, "Charge Rate") is { } c && c > 0 ? c
                    : b.Value(SensorType.Power, "Discharge Rate") is { } d && d > 0 ? -d
                    : b.Value(SensorType.Power, "Charge/Discharge Rate");
        bool? plugged = null;
        try { plugged = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online; }
        catch { }
        return new BatterySummary
        {
            Name = b.Name,
            Charge = Track(sources, "battery", b.Find(SensorType.Level, "Charge Level")),
            RateW = rate,
            RemainingSeconds = Positive(b.Value(SensorType.TimeSpan, "Remaining Time")),
            Wear = b.Value(SensorType.Level, "Degradation Level"),
            // Capacities are reported in mWh.
            DesignedWh = b.Value(SensorType.Energy, "Designed Capacity") / 1000,
            FullWh = b.Value(SensorType.Energy, "Full Charged Capacity", "Fully-Charged Capacity", "Fully Charged Capacity") / 1000,
            Voltage = b.Value(SensorType.Voltage, "Voltage"),
            PluggedIn = plugged,
        };
    }

    // Windows exposes a pile of virtual/disconnected adapters; only show ones that have moved data.
    static bool IsIdleNetwork(HardwareReading r) =>
        r.Type == HardwareType.Network &&
        r.Sensors.Where(s => s.Type == SensorType.Data).All(s => (s.Value ?? 0) <= 0.001f);

    /// <summary>"NVIDIA GeForce RTX 5080" → "RTX 5080", "AMD Ryzen 7 9800X3D" → "Ryzen 7 9800X3D".</summary>
    public static string ShortName(string name)
    {
        var s = Regex.Replace(name, @"\((TM|R|tm|r)\)", "");
        s = Regex.Replace(s, @"^(NVIDIA GeForce|NVIDIA|AMD|Intel Core|Intel)\s+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+\d+-Core Processor$|\s+Processor$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// Windows' answer when it has one (<see cref="GpuAdapters"/>); otherwise the name, then less than
    /// 2 GB of GPU memory. Newer iGPUs often reserve more than that, so their names are listed too.
    /// </summary>
    internal static bool IsIntegrated(HardwareReading g, bool? windowsIntegrated = null) =>
        windowsIntegrated ?? (g.Type != HardwareType.GpuNvidia &&
                              (IntegratedGpuName.IsMatch(g.Name.Trim()) || (g.Value(SensorType.SmallData, "GPU Memory Total") ?? 0) is > 0 and < 2048));

    /// <summary>The sensor's value, noting it as the source of history series <paramref name="alias"/>.</summary>
    static float? Track(Dictionary<string, string> sources, string alias, SensorReading? sensor)
    {
        if (sensor == null) return null;
        sources[alias] = sensor.Id;
        return sensor.Value;
    }

    static GpuSummary SummarizeGpu(HardwareReading g, bool? windowsIntegrated, Dictionary<string, string> sources) => new()
    {
        Id = g.Id,
        Name = g.Name,
        ShortName = ShortName(g.Name),
        Integrated = IsIntegrated(g, windowsIntegrated),
        Load = Track(sources, "gpu:" + g.Id, g.Find(SensorType.Load, "GPU Core", "D3D 3D")),
        Temp = Positive(Track(sources, "gputemp:" + g.Id, g.Find(SensorType.Temperature, "GPU Core", "GPU Edge", "GPU VR SoC") ?? g.FirstSensor(SensorType.Temperature))),
        HotSpot = Positive(g.Value(SensorType.Temperature, "GPU Hot Spot")),
        MemJunction = Positive(g.Value(SensorType.Temperature, "GPU Memory Junction")),
        Power = Track(sources, "gpupower:" + g.Id, g.Find(SensorType.Power, "GPU Package", "GPU Board Power", "GPU Power", "GPU Core") ??
                (g.Type == HardwareType.GpuNvidia ? null : g.FirstSensor(SensorType.Power))),
        PowerLimitPct = g.Value(SensorType.Load, "GPU Power"),
        CoreClock = g.Value(SensorType.Clock, "GPU Core"),
        MemClock = g.Value(SensorType.Clock, "GPU Memory"),
        VramUsedMb = g.Value(SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
        VramTotalMb = g.Value(SensorType.SmallData, "GPU Memory Total"),
        FanRpm = g.Value(SensorType.Fan, "GPU Fan"),
        FanPct = g.Value(SensorType.Control, "GPU Fan"),
    };

    static Summary Summarize(IReadOnlyList<HardwareReading> all, string? preferredGpu, IReadOnlyDictionary<string, DriveDetails> drives,
                             IReadOnlyDictionary<string, bool?> windowsIntegrated, WifiStatus? wifi = null, PingStats? ping = null, string? pingTarget = null)
    {
        var cpu = all.FirstOrDefault(h => h.Type == HardwareType.Cpu);
        var memories = all.Where(h => h.Type == HardwareType.Memory).ToList();
        var mem = memories.FirstOrDefault(h => h.Find(SensorType.Data, "Memory Used") != null &&
                                               !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        var virt = memories.FirstOrDefault(h => h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        var board = all.FirstOrDefault(h => h.Type == HardwareType.Motherboard);
        var boardSensors = all.Where(h => h.Type is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController)
                              .SelectMany(h => h.Sensors).ToList();

        var sources = new Dictionary<string, string>();
        SensorReading? BoardTemp(params string[] keys) => boardSensors.FirstOrDefault(s => s.Type == SensorType.Temperature && s.Value > 0 &&
            keys.Any(k => s.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));

        // CPU
        float? cpuClock = Positive(cpu?.Value(SensorType.Clock, "Cores (Average)"));
        if (cpu != null && cpuClock == null)
        {
            var cores = cpu.Sensors.Where(s => s.Type == SensorType.Clock && s.Value > 0 &&
                                               s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) &&
                                               !s.Name.Contains("Effective")).ToList();
            if (cores.Count > 0) cpuClock = cores.Average(s => s.Value!.Value);
        }
        float? cpuTemp = Track(sources, "cputemp", cpu?.Find(SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Tctl", "Package", "Core Max"))
                         ?? cpu?.Sensors.Where(s => s.Type == SensorType.Temperature).Select(s => s.Value).DefaultIfEmpty(null).Max();

        // GPUs
        var gpus = all.Where(h => h.Type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                      .Select(g => SummarizeGpu(g, windowsIntegrated.GetValueOrDefault(g.Id), sources)).ToList();
        var primary = gpus.FirstOrDefault(g => g.Id == preferredGpu)
                      ?? gpus.FirstOrDefault(g => !g.Integrated)
                      ?? gpus.FirstOrDefault();
        var second = gpus.FirstOrDefault(g => g != primary && g.Integrated) ?? gpus.FirstOrDefault(g => g != primary);

        // Memory
        float? ramUsed = mem?.Value(SensorType.Data, "Memory Used");
        float? ramAvail = mem?.Value(SensorType.Data, "Memory Available");
        float? virtUsed = virt?.Value(SensorType.Data, "Memory Used");
        float? virtAvail = virt?.Value(SensorType.Data, "Memory Available");
        float? dimmTemp = memories.SelectMany(m => m.Sensors)
            .Where(s => s.Type == SensorType.Temperature && s.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase) && s.Value > 0)
            .Select(s => s.Value).DefaultIfEmpty(null).Max();

        // Network
        var nets = all.Where(h => h.Type == HardwareType.Network && !IsIdleNetwork(h)).ToList();
        float? down = nets.Count == 0 ? null : nets.Sum(n => n.Value(SensorType.Throughput, "Download Speed") ?? 0);
        float? up = nets.Count == 0 ? null : nets.Sum(n => n.Value(SensorType.Throughput, "Upload Speed") ?? 0);
        var busiest = nets.OrderByDescending(n => (n.Value(SensorType.Data, "Data Downloaded") ?? 0) + (n.Value(SensorType.Data, "Data Uploaded") ?? 0))
                          .FirstOrDefault();

        // Board
        var chipset = BoardTemp("Chipset", "PCH");
        var system = BoardTemp("System", "Motherboard");

        // Storage
        var battery = SummarizeBattery(all.FirstOrDefault(h => h.Type == HardwareType.Battery), sources);

        var disks = all.Where(h => h.Type == HardwareType.Storage).Select(d => new DiskSummary
        {
            Id = d.Id,
            Name = d.Name,
            Temp = Positive(Track(sources, "disk:" + d.Id, d.Find(SensorType.Temperature, "Composite Temperature", "Temperature") ?? d.FirstSensor(SensorType.Temperature))),
            UsedPct = d.Value(SensorType.Load, "Used Space"),
            ReadRate = d.Value(SensorType.Throughput, "Read Rate"),
            WriteRate = d.Value(SensorType.Throughput, "Write Rate"),
            Volumes = drives.GetValueOrDefault(d.Id)?.Volumes ?? Array.Empty<VolumeInfo>(),
            Health = drives.GetValueOrDefault(d.Id)?.Health ?? DriveHealth.Unknown,
        }).ToList();

        return new Summary
        {
            CpuName = cpu?.Name,
            CpuShortName = cpu == null ? null : ShortName(cpu.Name),
            CpuLoad = Track(sources, "cpu", cpu?.Find(SensorType.Load, "CPU Total")),
            CpuMaxCoreLoad = cpu?.Value(SensorType.Load, "CPU Core Max"),
            // Without ring-0 access these read as 0 rather than missing.
            CpuTemp = Positive(cpuTemp),
            CpuCcdTemp = Positive(cpu?.Value(SensorType.Temperature, "CCD1 (Tdie)", "CCD")),
            CpuPower = Positive(Track(sources, "cpupower", cpu?.Find(SensorType.Power, "Package", "CPU Package") ?? cpu?.FirstSensor(SensorType.Power))),
            CpuClock = cpuClock,
            CpuEffectiveClock = Positive(cpu?.Value(SensorType.Clock, "Cores (Average Effective)")),
            CpuFanRpm = boardSensors.FirstOrDefault(s => s.Type == SensorType.Fan && s.Name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))?.Value,

            Gpus = gpus,
            Gpu = primary,
            SecondGpu = second,

            RamUsedGb = ramUsed,
            RamTotalGb = ramUsed + ramAvail,
            RamLoad = Track(sources, "ram", mem?.Find(SensorType.Load, "Memory")),
            VirtualUsedGb = virtUsed,
            VirtualTotalGb = virtUsed + virtAvail,
            DimmTemp = dimmTemp,

            BoardName = board?.Name,
            // The "chipset" series falls back to the system temperature (see UpdateOnce).
            ChipsetTemp = Track(sources, "chipset", chipset),
            VrmTemp = BoardTemp("VRM", "MOS")?.Value,
            SystemTemp = chipset == null ? Track(sources, "chipset", system) : system?.Value,
            SocketTemp = BoardTemp("CPU")?.Value,

            NetDown = down,
            NetUp = up,
            NetAdapter = busiest?.Name,

            Disks = disks,
            Battery = battery,
            Sources = sources,
            Wifi = wifi,
            Ping = ping,
            PingTarget = pingTarget,
        };
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread?.Join(3000);
        _nvmlFeed.SetWanted(false, false);
        if (_nvmlBlocked) NvmlBlocker.Unblock(); // otherwise Close() tries to shut down an NVML that never started
        try
        {
            if (_opened) _computer.Close();
        }
        catch
        {
            // Exiting anyway; a driver that fails to close cleanly shouldn't surface an error.
        }
    }
}
