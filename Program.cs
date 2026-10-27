using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace PCVitals;

static class Program
{
    const string MutexName = @"Local\PCVitals.SingleInstance";
    const string ExitEventName = @"Local\PCVitals.Exit";
    static Mutex? _mutex;

    [STAThread]
    static int Main(string[] args)
    {
        // Diagnostics / dev aids. None of these touch the running instance.
        if (args.Length >= 2 && args[0] == "--dump") return Dump(args[1]);
        if (args.Length >= 2 && args[0] == "--preview") return Preview(args[1]);
        if (args.Length >= 2 && args[0] == "--calibrate-gpu-power") return CalibrateGpuPower(args[1]);
        if (args.Length >= 2 && args[0] == "--taskbar-report") return TaskbarReport(args[1]);
        if (args.Length >= 2 && args[0] == "--nvml-feed" && int.TryParse(args[1], out var parentPid)) return NvmlFeed.RunChild(parentPid);
        if (args.Length >= 2 && args[0] == "--memprofile")
        {
            try { return MemProfile(args[1], pipeline: args.Length >= 3 && args[2] == "pipeline"); }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.ToString());
                return 1;
            }
        }
        if (args.Length >= 2 && args[0] == "--alert-selftest")
        {
            try { return AlertSelfTest(args[1]); }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.ToString());
                return 1;
            }
        }
        if (args.Length >= 2 && args[0] == "--bench-history")
        {
            try { return BenchHistory(args[1]); }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.ToString());
                return 1;
            }
        }
        if (args.Length >= 2 && args[0] == "--bench")
        {
            try { return Bench(args[1]); }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.ToString());
                return 1;
            }
        }

        bool admin = IsAdmin();
        bool replace = args.Contains("--replace");

        if (!AcquireSingleInstance())
        {
            if (!replace) return 0;
            // An elevated instance's kernel objects can only be signalled from another elevated process.
            if (!admin) return Relaunch(elevated: true, args) ? 0 : 1;
            SignalExit();
            if (!WaitForSingleInstance(TimeSpan.FromSeconds(8))) return 1;
        }

        // CPU temperatures, power and motherboard sensors need ring-0 access, i.e. admin rights.
        // Ask once; if the user declines UAC we carry on in a limited mode.
        if (!admin && !args.Contains("--no-elevate"))
        {
            ReleaseSingleInstance();
            if (Relaunch(elevated: true, Array.Empty<string>())) return 0;
            if (!AcquireSingleInstance()) return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        var settings = AppSettings.Load();
        Theme.Apply(settings.ColorTheme);
        Fmt.Fahrenheit = settings.Fahrenheit;
        Application.Run(new TrayContext(settings, admin));
        ReleaseSingleInstance();
        return 0;
    }

    static bool Relaunch(bool elevated, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (elevated) psi.Verb = "runas";
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined
        }
    }

    public static bool AcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool created);
            if (created) return true;
            _mutex.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            // Held by an elevated instance we aren't allowed to open: it's running.
        }
        catch (AbandonedMutexException)
        {
            return true; // previous instance crashed; we own it now
        }
        _mutex = null;
        return false;
    }

    static bool WaitForSingleInstance(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (AcquireSingleInstance()) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    public static void ReleaseSingleInstance()
    {
        if (_mutex == null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _mutex = null;
    }

    public static EventWaitHandle? CreateExitSignal()
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName); }
        catch { return null; }
    }

    static void SignalExit()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev))
                using (ev) ev.Set();
        }
        catch
        {
        }
    }

    static void LogError(Exception? ex) => Log(ex?.ToString() ?? "unknown error");

    public static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCVitals");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n\n");
        }
        catch
        {
        }
    }

    internal static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---- Diagnostics ------------------------------------------------------------------------

    static int Dump(string path)
    {
        using var hw = new HardwareService { DetailedMode = true };
        // Like the app, the report only pings when the user turned ping on (Settings → General).
        var settings = AppSettings.Load();
        using var latency = settings.PingEnabled ? new LatencyMonitor(settings.PingHost, intervalMs: 1000) : null;
        hw.Latency = latency;
        hw.EnableDeferredGroups();
        hw.UpdateOnce();
        Thread.Sleep(3000); // load and throughput sensors need two samples; a few pings
        var snap = hw.UpdateOnce();

        var sb = new StringBuilder();
        var s = snap.Summary;
        sb.AppendLine($"Admin: {IsAdmin()}");
        sb.AppendLine($"CPU  {s.CpuName} ({s.CpuShortName}): load {s.CpuLoad} temp {s.CpuTemp} power {s.CpuPower} clock {s.CpuClock}");
        foreach (var g in s.Gpus)
            sb.AppendLine($"GPU  {g.Name} ({g.ShortName}) integrated={g.Integrated} primary={g == s.Gpu}: load {g.Load} temp {g.Temp} power {g.Power} vram {g.VramUsedMb}/{g.VramTotalMb}");
        sb.AppendLine($"RAM  {s.RamUsedGb}/{s.RamTotalGb} GB ({s.RamLoad}%)  board {s.BoardName} chipset {s.ChipsetTemp}  net {s.NetDown}/{s.NetUp}");
        foreach (var d in s.Disks)
            sb.AppendLine($"DISK {d.Name} [{d.Letters}] health {d.Health.Word}{(d.Health.Notes.Count > 0 ? ": " + string.Join(" ", d.Health.Notes) : "")}"
                          + string.Concat(d.Volumes.Select(v => $"  {v.Letter}: {v.FreePercent:0}% free")));
        sb.AppendLine(s.Wifi is { } w ? $"WIFI {w.Ssid} signal {w.SignalPercent}% link {w.RxMbps}/{w.TxMbps} Mbps" : "WIFI not connected");
        sb.AppendLine(s.Ping is { } p ? $"PING {s.PingTarget}: avg {p.AvgMs:0.0} ms jitter {p.JitterMs:0.0} ms loss {p.LossPercent:0}% ({p.Samples} pings)" : latency == null ? "PING off" : "PING no replies yet");
        foreach (var h in snap.Hardware)
        {
            sb.AppendLine();
            sb.AppendLine($"[{h.Type}] {h.Name}");
            foreach (var r in h.Sensors)
                sb.AppendLine($"  {r.Type,-12} {r.Name,-36} {Fmt.Sensor(r.Type, r.Value)}");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return 0;
    }

    /// <summary>
    /// Child process for the tray app: loads NVML, samples board power against NVAPI's power
    /// percentage for a few seconds, and writes "gpu name &lt;tab&gt; watts-per-percent" lines.
    /// </summary>
    static int CalibrateGpuPower(string path)
    {
        try
        {
            using var hw = new HardwareService();
            for (int i = 0; i < 12; i++)
            {
                hw.UpdateOnce();
                Thread.Sleep(250);
            }
            var lines = new List<string>();
            lock (hw.PowerPerPercent)
                foreach (var (gpu, ratio) in hw.PowerPerPercent)
                    lines.Add($"{gpu}\t{ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            File.WriteAllLines(path, lines);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Runs one alert rule against live readings: memory usage above 1% for 3 seconds (always true),
    /// whose action runs cmd.exe to write a marker file. Also starts the same command through the
    /// desktop shell (the path used when PC Vitals is elevated). Reports what happened.
    /// </summary>
    static int AlertSelfTest(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Admin: {IsAdmin()}");
        var marker = Path.Combine(Path.GetTempPath(), $"pcvitals-alert-{Guid.NewGuid():N}.txt");
        var shellMarker = marker.Replace(".txt", "-shell.txt");
        var rule = new AlertRule
        {
            Series = "ram", Threshold = 1, DurationSeconds = 3, CooldownMinutes = 15, Notify = false,
            RunProgram = Path.Combine(Environment.SystemDirectory, "cmd.exe"), RunArguments = $"/c echo fired > \"{marker}\"",
        };
        using var hw = new HardwareService(lean: true);
        var engine = new AlertEngine();
        var start = DateTime.Now;
        for (int i = 0; i < 8; i++)
        {
            var snap = hw.UpdateOnce();
            foreach (var e in engine.Evaluate(new[] { rule }, snap.Time, r => r.ValueIn(snap)))
            {
                sb.AppendLine($"{(snap.Time - start).TotalSeconds:0.0} s: {(e.Fired ? "fired" : "recovered")} at {e.Value:0.0}% ({AlertText.Condition(rule)})");
                if (e.Fired) sb.AppendLine("  run program: " + (AlertActions.RunProgram(rule) ?? "started"));
            }
            Thread.Sleep(1000);
        }
        try
        {
            DesktopShell.Run(rule.RunProgram, $"/c echo fired > \"{shellMarker}\"", Environment.SystemDirectory);
            sb.AppendLine("Desktop shell launch: started");
        }
        catch (Exception ex)
        {
            sb.AppendLine("Desktop shell launch failed: " + ex.Message);
        }
        Thread.Sleep(2000);
        foreach (var (name, file) in new[] { ("Program action", marker), ("Desktop shell", shellMarker) })
        {
            sb.AppendLine($"{name} wrote its marker: {File.Exists(file)}");
            try { File.Delete(file); }
            catch { }
        }
        File.WriteAllText(path, sb.ToString());
        return 0;
    }

    /// <summary>
    /// Builds a month of synthetic saved history (60 series, as on a busy desktop) in a temporary
    /// folder and times what the app does with it: a minute's append, daily clean-up, and loading
    /// the 7 d / 30 d graphs.
    /// </summary>
    static int BenchHistory(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcvitals-bench-history-" + Guid.NewGuid().ToString("N"));
        var sb = new StringBuilder();
        try
        {
            var archive = new HistoryArchive(dir);
            var keys = Enumerable.Range(0, 60).Select(i => i < 20 ? $"series{i}" : $"/bench/0/temperature/{i}").ToArray();
            static (string, string) Describe(string k) => (k, "°C");
            var now = DateTime.Now;
            var start = now.Date.AddDays(-29);
            var sw = Stopwatch.StartNew();
            var rng = new Random(1);
            var values = new MinuteSummary[keys.Length];
            int minutes = 0;
            // Write whole days at once (the app appends one minute at a time; that is timed below).
            for (var day = start; day < now; day = day.AddDays(1))
            {
                var records = new List<HistoryRecord>();
                for (var m = day; m < day.AddDays(1) && m < now; m = m.AddMinutes(1), minutes++)
                    for (int s = 0; s < keys.Length; s++)
                    {
                        float v = 40 + 20 * MathF.Sin((minutes + s * 37) / 300f) + rng.NextSingle() * 5;
                        records.Add(new HistoryRecord(HistoryFile.ToMinute(m), archive.Series.IndexOf(keys[s], Describe), v, v - 3, v + 3));
                    }
                HistoryFile.Append(HistoryFile.PathFor(dir, DateOnly.FromDateTime(day)), records);
            }
            archive.Series.Save();
            sb.AppendLine($"Wrote {minutes:N0} minutes × {keys.Length} series in {sw.ElapsedMilliseconds} ms ({archive.SizeBytes() / 1048576.0:0.0} MB)");

            sw.Restart();
            archive.Maintain(DateOnly.FromDateTime(now), 30);
            sb.AppendLine($"Daily clean-up (compact days older than a week): {sw.ElapsedMilliseconds} ms, now {archive.SizeBytes() / 1048576.0:0.0} MB");

            for (int s = 0; s < keys.Length; s++) values[s] = new MinuteSummary(keys[s], 50, 49, 51);
            sw.Restart();
            for (int i = 0; i < 100; i++) archive.Append(now.AddMinutes(i), values, Describe);
            sb.AppendLine($"Append one minute (60 series): {sw.Elapsed.TotalMilliseconds / 100:0.00} ms");

            foreach (var (days, bucket) in new[] { (1, 1), (7, 5), (30, 60) })
            {
                archive.Load(now.AddDays(-days), now, bucket); // warm the file cache, as a second view would be
                sw.Restart();
                var range = archive.Load(now.AddDays(-days), now, bucket);
                sb.AppendLine($"Load {days} d at {bucket}-minute buckets: {sw.ElapsedMilliseconds} ms ({range.Series.Count} series × {range.Count} points)");
            }
            File.WriteAllText(path, sb.ToString());
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    /// <summary>Measures startup, per-device poll cost, CPU and memory, as the tray app would see them.</summary>
    static int Bench(string path)
    {
        var sb = new StringBuilder();
        var proc = Process.GetCurrentProcess();
        sb.AppendLine($"Admin: {IsAdmin()}");
        sb.AppendLine($"Process start â†’ Main: {(DateTime.Now - proc.StartTime).TotalMilliseconds:0} ms");

        var sw = Stopwatch.StartNew();
        using var hw = new HardwareService();
        hw.UpdateOnce();
        sb.AppendLine($"First reading (taskbar can draw): {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        hw.UpdateOnce();
        sb.AppendLine($"Deferred discovery (drives, controllers, PSU) in background: {sw.ElapsedMilliseconds} ms");

        // Per-device update cost, via reflection-free timing of the public library objects.
        var computer = (LibreHardwareMonitor.Hardware.Computer)typeof(HardwareService)
            .GetField("_computer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hw)!;
        const int N = 20;
        var cost = new Dictionary<string, double>();
        for (int i = 0; i < N; i++)
        {
            foreach (var h in computer.Hardware)
            {
                foreach (var detailed in new[] { false, true })
                {
                    var t = Stopwatch.StartNew();
                    HardwareService.UpdateDevice(h, detailed);
                    var key = $"{h.HardwareType,-12} {h.Name}{(detailed ? "  [dashboard open]" : "")}";
                    cost[key] = cost.GetValueOrDefault(key) + t.Elapsed.TotalMilliseconds;
                }
            }
            Thread.Sleep(50);
        }
        sb.AppendLine();
        sb.AppendLine("Average Update() cost per device:");
        foreach (var (k, v) in cost.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {v / N,8:0.00} ms  {k}");

        // Steady state the way the app runs: throttled polling at 1 s for 30 s.
        GC.Collect();
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        long alloc0 = GC.GetTotalAllocatedBytes();
        var wall = Stopwatch.StartNew();
        double pollMs = 0, errSum = 0, errMax = 0;
        int errN = 0;
        for (int i = 0; i < 30; i++)
        {
            var t = Stopwatch.StartNew();
            var snap = hw.UpdateOnce();
            // Accuracy of the NVML-free estimate the tray app uses: NVAPI % Ã— learned ratio vs NVML watts.
            foreach (var g in snap.Hardware.Where(h => h.Type == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia))
            {
                var real = g.Value(LibreHardwareMonitor.Hardware.SensorType.Power, "GPU Package");
                var pct = g.Value(LibreHardwareMonitor.Hardware.SensorType.Load, "GPU Power");
                float ratio;
                lock (hw.PowerPerPercent) hw.PowerPerPercent.TryGetValue(g.Name, out ratio);
                if (real is > 0 && pct is > 0 && ratio > 0 && i >= 5)
                {
                    double err = Math.Abs(pct.Value * ratio - real.Value);
                    errSum += err;
                    errMax = Math.Max(errMax, err);
                    errN++;
                }
            }
            foreach (var k in new[] { WidgetKind.Cpu, WidgetKind.Gpu, WidgetKind.Memory, WidgetKind.Network })
                Vitals.Build(k, "X", snap);
            pollMs += t.Elapsed.TotalMilliseconds;
            Thread.Sleep(Math.Max(0, 1000 - (int)t.ElapsedMilliseconds));
        }
        proc.Refresh();
        var cpuUsed = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        sb.AppendLine();
        sb.AppendLine($"Steady state (30 polls at 1 s, widget items built):");
        sb.AppendLine($"  avg poll+build: {pollMs / 30:0.0} ms");
        if (errN > 0) sb.AppendLine($"  NVML-free GPU power estimate: mean error {errSum / errN:0.00} W, max {errMax:0.00} W over {errN} samples");
        var last = hw.Latest!.Summary;
        sb.AppendLine($"  background readings still live: GPU {last.Gpu?.ShortName} load {last.Gpu?.Load:0.0}% power {last.Gpu?.Power:0.0} W temp {last.Gpu?.Temp:0.0} Â°C, CPU {last.CpuLoad:0.0}%");
        sb.AppendLine($"  CPU: {cpuUsed / wall.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount:0.00}% of total machine ({cpuUsed / wall.Elapsed.TotalMilliseconds * 100:0.0}% of one core)");
        sb.AppendLine($"  allocations: {(GC.GetTotalAllocatedBytes() - alloc0) / 30 / 1024.0:0} KB per poll");
        var gcInfo = GC.GetGCMemoryInfo();
        sb.AppendLine($"  GC heap: {GC.GetTotalMemory(false) / 1048576.0:0.0} MB (committed {gcInfo.TotalCommittedBytes / 1048576.0:0.0} MB, gen0 GCs {GC.CollectionCount(0)}), " +
                      $"working set: {proc.WorkingSet64 / 1048576.0:0.0} MB, private: {proc.PrivateMemorySize64 / 1048576.0:0.0} MB");
        Native.TrimWorkingSet();
        proc.Refresh();
        sb.AppendLine($"  after trim: working set {proc.WorkingSet64 / 1048576.0:0.0} MB");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return 0;
    }

    /// <summary>
    /// Attributes private (committed) memory, which is what Task Manager's "Memory" column shows.
    /// Default: each library and sensor group as it loads (NVML included). With "pipeline": the
    /// tray app's real lean path, including the dashboard's NVML helper process.
    /// </summary>
    static int MemProfile(string path, bool pipeline)
    {
        var sb = new StringBuilder();
        var proc = Process.GetCurrentProcess();
        long last = 0;
        void Mark(string stage)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            proc.Refresh();
            long priv = proc.PrivateMemorySize64;
            var gc = GC.GetGCMemoryInfo();
            sb.AppendLine($"{priv / 1048576.0,7:0.0} MB  (+{(priv - last) / 1048576.0,6:0.0})  heap {GC.GetTotalMemory(false) / 1048576.0,5:0.0} / committed {gc.TotalCommittedBytes / 1048576.0,5:0.0}  {stage}");
            last = priv;
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        sb.AppendLine($"Admin: {IsAdmin()}");
        Mark("runtime started");
        ApplicationConfiguration.Initialize();
        using (var f = new Form()) { _ = f.Handle; }
        Mark("WinForms initialised");

        if (pipeline)
        {
            using var lean = new HardwareService(lean: true);
            var calibration = Path.Combine(Path.GetTempPath(), $"pcvitals-memprofile-{Environment.ProcessId}.txt");
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--calibrate-gpu-power \"{calibration}\"") { UseShellExecute = false })!.WaitForExit();
            foreach (var line in File.Exists(calibration) ? File.ReadAllLines(calibration) : Array.Empty<string>())
                if (line.Split('\t') is [var gpu, var ratio]) lean.PowerPerPercent[gpu] = float.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture);
            File.Delete(calibration);

            for (int i = 1; i <= 30; i++)
            {
                var s = lean.UpdateOnce();
                if (i == 1) Mark("first poll");
                if (i == 2) Mark("deferred discovery (drives, controllers)");
                if (i == 3) { Native.TrimWorkingSet(); Mark("aggressive trim"); }
                if (i == 30) sb.AppendLine($"GPU power shown: {s.Summary.Gpu?.Power:0.0} W (estimated without NVML: {lean.GpuPowerEstimated})");
                Thread.Sleep(200);
            }
            Mark("after 30 polls");

            lean.DetailedMode = true;
            Snapshot d = lean.UpdateOnce();
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(500);
                d = lean.UpdateOnce();
            }
            Mark("dashboard open 4 s (NVML in helper process)");
            var nvidia = d.Hardware.FirstOrDefault(h => h.Type == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia);
            if (nvidia != null)
                sb.AppendLine($"PCIe sensors: {string.Join(", ", nvidia.Sensors.Where(x => x.Name.Contains("PCIe")).Select(x => $"{x.Name}={Fmt.Sensor(x.Type, x.Value)}"))}; " +
                              $"power {d.Summary.Gpu?.Power:0.0} W");
            lean.DetailedMode = false;
            lean.UpdateOnce();
            Thread.Sleep(500);
            Mark("dashboard closed (helper exited)");
            return 0;
        }

        var computer = new LibreHardwareMonitor.Hardware.Computer();
        computer.Open();
        Mark("Computer.Open (no groups)");
        if (NativeLibrary.TryLoad("nvml.dll", typeof(Program).Assembly, DllImportSearchPath.System32, out var nvml) &&
            NativeLibrary.TryGetExport(nvml, "nvmlInit_v2", out var init))
        {
            unsafe { ((delegate* unmanaged<int>)init)(); }
            Mark("NVML initialised");
        }
        if (NativeLibrary.TryLoad("nvapi64.dll", out var nvapi) && NativeLibrary.TryGetExport(nvapi, "nvapi_QueryInterface", out var qi))
        {
            unsafe
            {
                var initPtr = ((delegate* unmanaged<uint, IntPtr>)qi)(0x0150E828 /* NvAPI_Initialize */);
                if (initPtr != IntPtr.Zero) ((delegate* unmanaged<int>)initPtr)();
            }
            Mark("NVAPI initialised");
        }
        void Group(string name, Action enable)
        {
            enable();
            foreach (var h in computer.Hardware) HardwareService.UpdateDevice(h, detailed: false);
            Mark($"+ {name} (opened and polled)");
        }
        Group("CPU", () => computer.IsCpuEnabled = true);
        Group("GPU", () => computer.IsGpuEnabled = true);
        Group("Memory (incl. DIMM SPD)", () => computer.IsMemoryEnabled = true);
        Group("Motherboard / SuperIO", () => computer.IsMotherboardEnabled = true);
        Group("Network", () => computer.IsNetworkEnabled = true);
        Group("Storage", () => computer.IsStorageEnabled = true);
        Group("Controller (HID)", () => computer.IsControllerEnabled = true);
        Group("PSU", () => computer.IsPsuEnabled = true);
        Group("Battery", () => computer.IsBatteryEnabled = true);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Mark("aggressive GC (decommit)");

        sb.AppendLine();
        sb.AppendLine("Largest loaded modules (image size, mostly shared, not private):");
        foreach (ProcessModule m in proc.Modules.Cast<ProcessModule>().OrderByDescending(m => m.ModuleMemorySize).Take(15))
            sb.AppendLine($"  {m.ModuleMemorySize / 1048576.0,6:0.0} MB  {m.FileName}");
        try { computer.Close(); } catch { }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return 0;
    }


    /// <summary>Taskbar geometry and how wide the strip would be, to diagnose overlap with app icons.</summary>
    static int TaskbarReport(string path)
    {
        ApplicationConfiguration.Initialize();
        var sb = new StringBuilder();
        var tb = Native.FindTaskbar();
        if (tb == null)
        {
            File.WriteAllText(path, "No taskbar found");
            return 1;
        }
        sb.AppendLine($"Taskbar {tb.Bounds}, dpi {tb.Dpi}, auto-hide {Native.TaskbarAutoHides()}");
        sb.AppendLine($"Start {tb.Start}  Tasks {tb.Tasks}  Icons {tb.Icons}  Tray {tb.Tray}");
        int free = (tb.Tray.Left - 8 * tb.Dpi / 96) - (tb.Icons.Right + 8 * tb.Dpi / 96);
        sb.AppendLine($"Free gap between icons and tray: {free}px");

        using var hw = new HardwareService();
        hw.UpdateOnce();
        Thread.Sleep(500);
        var snap = hw.UpdateOnce();
        var all = AppSettings.DefaultWidgetItems().Select(i => Vitals.Build(i.Kind, i.Label, snap)).Where(v => v != null).Select(v => v!).ToList();
        float s = tb.Dpi / 96f;
        foreach (var (name, scale, graphs) in new[] { ("100% + graphs", s, true), ("100%, no graphs", s, false), ("70%, no graphs", s * 0.7f, false) })
            sb.AppendLine($"All {all.Count} items at {name}: {TaskbarWidget.MeasureStrip(all, scale, graphs)}px");
        var defaults = all.Take(4).ToList();
        sb.AppendLine($"Default 4 items at 100% + graphs: {TaskbarWidget.MeasureStrip(defaults, s, true)}px");
        File.WriteAllText(path, sb.ToString());
        return 0;
    }

    /// <summary>Renders the taskbar strip, hover cards and dashboard to PNGs (used for the README).</summary>
    static int Preview(string dir)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(dir);
        using var hw = new HardwareService { DetailedMode = true };
        hw.EnableDeferredGroups();
        var settings = new AppSettings();

        // Show a pinned sensor and a couple of favourites, as a user would set them up.
        var first = hw.UpdateOnce();
        var pinned = PreviewSensor(first, SensorType.Temperature, "Hot Spot", "Memory Junction", "Composite", "Temperature");
        if (pinned != null) settings.PinSensor(pinned.Key, "HOT SPOT");
        foreach (var fav in new[]
                 {
                     PreviewSensor(first, SensorType.Temperature, "CCD1", "Tctl"), PreviewSensor(first, SensorType.Fan, "CPU", "Fan"),
                     PreviewSensor(first, SensorType.Power, "Package"),
                 })
            if (fav != null) settings.UpdateSensor(fav.Id, p => p.Favorite = true);
        // An alert on the pinned sensor next to the built-in ones, with the CPU temperature one switched on.
        if (pinned != null) settings.AlertRules.Add(new AlertRule { Sensor = pinned.Key, Threshold = 80, DurationSeconds = 30, Sound = "Exclamation" });
        settings.AlertRules[0].Enabled = true;
        hw.TrackedSensors = settings.TrackedSensors(first);

        for (int i = 0; i < 40; i++)
        {
            hw.UpdateOnce();
            Thread.Sleep(250);
        }
        var snap = hw.UpdateOnce();
        int dpi = 144;
        float scale = dpi / 96f;

        // Taskbar strip on dark and light taskbar backgrounds.
        foreach (var light in new[] { false, true })
        {
            var views = settings.WidgetItems.Where(i => i.Enabled || i.Kind is WidgetKind.Power)
                .Select(i => Vitals.ForItem(i, snap, settings)).Where(v => v != null).Select(v => v!).ToList();
            using var strip = TaskbarWidget.RenderStrip(views, (int)(48 * scale), scale, light, true, 0, out _);
            using var bmp = new Bitmap(strip.Width + 40, strip.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(light ? Color.FromArgb(238, 238, 242) : Color.FromArgb(28, 30, 36));
                g.DrawImage(strip, 20, 0);
            }
            bmp.Save(Path.Combine(dir, light ? "taskbar-light.png" : "taskbar.png"), ImageFormat.Png);
        }

        // Hover cards for CPU and GPU side by side.
        var cards = new[] { WidgetKind.Cpu, WidgetKind.Gpu, WidgetKind.Memory }
            .Select(k => Vitals.Build(k, null, snap)).Where(v => v != null).Select(v => v!).ToList();
        if (pinned != null) cards.Add(Vitals.ForSensor(snap, pinned.Key, null, settings));
        var sizes = cards.Select(c => HoverCard.Measure(c, dpi)).ToList();
        int gap = 24;
        using (var bmp = new Bitmap(sizes.Sum(s => s.Width) + gap * (cards.Count + 1), sizes.Max(s => s.Height) + gap * 2))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(40, 42, 50));
                int x = gap;
                for (int i = 0; i < cards.Count; i++)
                {
                    using var card = new Bitmap(sizes[i].Width, sizes[i].Height);
                    using (var cg = Graphics.FromImage(card)) HoverCard.PaintCard(cg, cards[i], dpi, sizes[i]);
                    g.DrawImage(card, x, gap);
                    x += sizes[i].Width + gap;
                }
            }
            bmp.Save(Path.Combine(dir, "hover-cards.png"), ImageFormat.Png);
        }

        // Dashboard pages in each theme.
        var gpuPage = snap.Summary.Gpu != null ? "gpu:" + snap.Summary.Gpu.Id : "cpu";
        foreach (var (file, page, mode) in new[]
                 {
                     ("dashboard.png", "overview", ThemeMode.Dark), ("dashboard-cpu.png", "cpu", ThemeMode.Dark),
                     ("dashboard-gpu-light.png", gpuPage, ThemeMode.Light), ("dashboard-memory-amoled.png", "memory", ThemeMode.Amoled),
                 })
        {
            Theme.Apply(mode);
            settings.DashboardPage = page;
            using var view = new DashboardView(settings, IsAdmin()) { MachineLabel = "My PC", HistoryProvider = hw.GetHistory };
            view.SampleProcessesNow();
            Thread.Sleep(1000);
            view.SampleProcessesNow();
            int w = (int)(920 * view.DeviceDpi / 96f), h = (int)(740 * view.DeviceDpi / 96f);
            view.Size = new Size(w, h);
            view.SetSnapshot(hw.UpdateOnce());
            using var bmp = new Bitmap(w, h);
            view.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
            bmp.Save(Path.Combine(dir, file), ImageFormat.Png);
        }

        // A week of saved history on the CPU page. Uses the real archive when it has a week, else a made-up one.
        Theme.Apply(ThemeMode.Dark);
        var weekDir = Path.Combine(Path.GetTempPath(), "pcvitals-preview-history");
        try
        {
            var real = new HistoryArchive(HistoryArchive.DefaultDir);
            var archive = real.Days().Count() >= 7 ? real : SyntheticWeek(weekDir);
            settings.DashboardPage = "cpu";
            settings.GraphSeconds = 7 * 86400;
            var week = archive.Load(DateTime.Now.AddDays(-7), DateTime.Now, 5);
            using var view = new DashboardView(settings, IsAdmin()) { MachineLabel = "My PC", HistoryProvider = hw.GetHistory, LongHistoryProvider = _ => week };
            int w = (int)(920 * view.DeviceDpi / 96f), h = (int)(740 * view.DeviceDpi / 96f);
            view.Size = new Size(w, h);
            view.SetSnapshot(snap);
            using var bmp = new Bitmap(w, h);
            view.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
            bmp.Save(Path.Combine(dir, "dashboard-cpu-week.png"), ImageFormat.Png);
            settings.GraphSeconds = 60;
        }
        finally
        {
            try { Directory.Delete(weekDir, recursive: true); }
            catch { }
        }

        // Settings pages.
        foreach (var (file, page, mode) in new[]
                 {
                     ("settings-taskbar.png", "taskbar", ThemeMode.Dark), ("settings-appearance.png", "appearance", ThemeMode.Dark),
                     ("settings-graphics-light.png", "graphics", ThemeMode.Light), ("settings-alerts.png", "alerts", ThemeMode.Dark),
                 })
        {
            Theme.Apply(mode);
            // On screen (so it scales to the monitor's DPI) but practically invisible.
            using var form = new SettingsForm(new PreviewHost(settings, snap), page)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
                ShowInTaskbar = false,
                Opacity = 0.01,
            };
            form.Show();
            Application.DoEvents();
            var client = form.ClientRectangle;
            using var bmp = new Bitmap(client.Width, client.Height);
            using (var full = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(full, new Rectangle(0, 0, form.Width, form.Height));
                // Crop the classic frame WM_PRINT draws; the real window has a themed title bar.
                var origin = form.PointToScreen(Point.Empty);
                using var g = Graphics.FromImage(bmp);
                g.DrawImage(full, new Rectangle(0, 0, client.Width, client.Height),
                            new Rectangle(origin.X - form.Left, origin.Y - form.Top, client.Width, client.Height), GraphicsUnit.Pixel);
            }
            bmp.Save(Path.Combine(dir, file), ImageFormat.Png);
            form.Close();
        }

        // The alert editor, on the sensor rule.
        Theme.Apply(ThemeMode.Dark);
        using (var editor = new AlertEditor(settings.AlertRules[^1], () => snap, settings, _ => { })
               { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), ShowInTaskbar = false, Opacity = 0.01 })
        {
            editor.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(editor.Width, editor.Height);
            editor.DrawToBitmap(bmp, new Rectangle(0, 0, editor.Width, editor.Height));
            bmp.Save(Path.Combine(dir, "alert-editor.png"), ImageFormat.Png);
            editor.Close();
        }
        return 0;
    }

    /// <summary>A plausible week of CPU usage and temperature (busy days, quiet nights), for previews.</summary>
    static HistoryArchive SyntheticWeek(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        var archive = new HistoryArchive(dir);
        var rng = new Random(7);
        var now = DateTime.Now;
        for (var day = now.Date.AddDays(-7); day < now; day = day.AddDays(1))
        {
            var records = new List<HistoryRecord>();
            for (var m = day; m < day.AddDays(1) && m < now; m = m.AddMinutes(1))
            {
                // Off overnight (no records), working by day with bursts.
                if (m.Hour < 8) continue;
                float load = 8 + 25 * MathF.Max(0, MathF.Sin((m.Hour - 8) / 16f * MathF.PI)) + (rng.NextSingle() < 0.03f ? 50 : 0) + rng.NextSingle() * 6;
                float temp = 42 + load * 0.45f + rng.NextSingle() * 2;
                records.Add(new HistoryRecord(HistoryFile.ToMinute(m), archive.Series.IndexOf("cpu", _ => ("CPU usage", "%")), load, load * 0.4f, Math.Min(100, load * 1.8f + 5)));
                records.Add(new HistoryRecord(HistoryFile.ToMinute(m), archive.Series.IndexOf("cputemp", _ => ("CPU temperature", "°C")), temp, temp - 2, temp + 6));
            }
            HistoryFile.Append(HistoryFile.PathFor(dir, DateOnly.FromDateTime(day)), records);
        }
        archive.Series.Save();
        return archive;
    }

    /// <summary>The first sensor of a type whose name contains one of <paramref name="names"/> (in that order of preference).</summary>
    static SensorEntry? PreviewSensor(Snapshot snap, SensorType type, params string[] names)
    {
        foreach (var name in names)
            if (snap.Catalog.All.FirstOrDefault(e => e.Sensor.Type == type && e.Sensor.Value > 0 &&
                                                     e.Sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) is { } found)
                return found;
        return null;
    }

    sealed class PreviewHost : ISettingsHost
    {
        public PreviewHost(AppSettings settings, Snapshot snap)
        {
            Settings = settings;
            Latest = snap;
        }

        public AppSettings Settings { get; }
        public bool IsAdmin => Program.IsAdmin();
        public Snapshot? Latest { get; }
        public string? WidgetFitNote => null;
        public bool GpuPowerEstimated => true;
        public IReadOnlyDictionary<string, float> PowerRatios => new Dictionary<string, float> { ["NVIDIA GeForce RTX 5080"] = 3.98f };
        public void SettingsChanged() { }
        public void RecalibrateGpuPower() { }
        public int ExportHistory(string path) => 0;
        public HistoryArchive? Archive => null;
        public long HistorySizeBytes => 1_600_000;
        public Task DeleteHistoryAsync() => Task.CompletedTask;
        public AlertLog AlertLog { get; } = new(Path.Combine(Path.GetTempPath(), "pcvitals-preview-alerts.json"));
        public DateTime? LastFired(string ruleId) => null;
        public void TestAlert(AlertRule rule) { }
        public void ReopenSettings(string page) { }
    }
}