using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PCVitals.Core;

/// <summary>
/// Streams NVML readings (PCIe throughput, exact board power) from a helper process while the
/// dashboard is open. NVML commits ~20 MB on init and never releases it, so keeping it in a
/// short-lived child means the tray app's memory drops back as soon as the dashboard closes.
/// </summary>
sealed class NvmlFeed
{
    public readonly record struct Reading(float Watts, float RxBytes, float TxBytes);

    readonly Dictionary<string, Reading> _latest = new();
    Process? _child;

    public bool TryGet(string gpuName, out Reading reading)
    {
        lock (_latest) return _latest.TryGetValue(gpuName, out reading);
    }

    public void SetWanted(bool wanted, bool hasNvidia)
    {
        if (wanted && hasNvidia && _child is not { HasExited: false }) Start();
        else if (!wanted && _child != null) Stop();
    }

    void Start()
    {
        lock (_latest) _latest.Clear();
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(Environment.ProcessPath!, $"--nvml-feed {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                },
            };
            p.OutputDataReceived += (_, e) => Parse(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            _child = p;
        }
        catch
        {
            _child = null;
        }
    }

    void Stop()
    {
        try
        {
            if (_child is { HasExited: false }) _child.Kill();
        }
        catch
        {
        }
        _child?.Dispose();
        _child = null;
        lock (_latest) _latest.Clear();
    }

    void Parse(string? line)
    {
        var parts = line?.Split('\t');
        if (parts is not { Length: 4 }) return;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rxKb) ||
            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var txKb) ||
            !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var mw)) return;
        lock (_latest) _latest[parts[0]] = new Reading(mw / 1000f, rxKb * 1024, txKb * 1024);
    }

    /// <summary>The helper: "name, rx KB/s, tx KB/s, mW" per GPU per second until the parent exits.</summary>
    public static unsafe int RunChild(int parentPid)
    {
        if (!NativeLibrary.TryLoad("nvml.dll", typeof(NvmlFeed).Assembly, DllImportSearchPath.System32, out var lib)) return 1;
        var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlInit_v2");
        var count = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2");
        var byIndex = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
        var getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetName");
        var pcie = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPcieThroughput");
        var power = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPowerUsage");
        if (init() != 0) return 1;

        uint n = 0;
        if (count(&n) != 0) return 1;
        var devices = new List<(IntPtr Handle, string Name)>();
        var buf = stackalloc byte[96];
        for (uint i = 0; i < n; i++)
        {
            IntPtr dev;
            if (byIndex(i, &dev) != 0) continue;
            string name = getName(dev, buf, 96) == 0 ? Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"GPU {i}" : $"GPU {i}";
            devices.Add((dev, name));
        }

        Process? parent = null;
        try { parent = Process.GetProcessById(parentPid); } catch { return 0; }
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var sw = new Stopwatch();
        while (!parent.HasExited)
        {
            sw.Restart();
            foreach (var (dev, name) in devices)
            {
                uint tx = 0, rx = 0, mw = 0;
                pcie(dev, 0 /* TX */, &tx); // each call samples the bus for ~20 ms
                pcie(dev, 1 /* RX */, &rx);
                power(dev, &mw);
                try
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}\t{rx}\t{tx}\t{mw}"));
                }
                catch (IOException)
                {
                    return 0; // parent closed the pipe
                }
            }
            Thread.Sleep(Math.Max(100, 1000 - (int)sw.ElapsedMilliseconds));
        }
        return 0;
    }
}
