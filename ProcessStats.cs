using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>
/// Top processes by CPU and memory, grouped by app. Uses one
/// NtQuerySystemInformation call per sample (no per-process handles), and only runs while a
/// dashboard page that shows processes is open.
/// </summary>
sealed class ProcessStats
{
    public sealed record Entry(string Name, int Count, float CpuPercent, long PrivateBytes, float GpuPercent);

    readonly GpuProcessUsage _gpu = new();
    /// <summary>True once per-process GPU usage has been read at least once.</summary>
    public bool HasGpu { get; private set; }

    Dictionary<int, long> _lastCpu = new();
    long _lastTimestamp;
    // Pinned: the kernel writes absolute pointers into this buffer (process image names), so it
    // must not move between the call and parsing.
    byte[] _buffer = GC.AllocateUninitializedArray<byte>(512 * 1024, pinned: true);

    public IReadOnlyList<Entry> Apps { get; private set; } = Array.Empty<Entry>();
    public int Processes { get; private set; }
    public int Threads { get; private set; }
    public int Handles { get; private set; }

    /// <param name="includeGpu">Also read per-process GPU usage (performance counters; a bit costlier).</param>
    public void Sample(bool includeGpu = false)
    {
        var gpu = includeGpu ? _gpu.Sample() : null;
        if (gpu is { Count: > 0 }) HasGpu = true;
        int length;
        while (true)
        {
            int status = NtQuerySystemInformation(5 /* SystemProcessInformation */, _buffer, _buffer.Length, out length);
            if (status == 0) break;
            if (status != unchecked((int)0xC0000004) /* STATUS_INFO_LENGTH_MISMATCH */) return;
            _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(_buffer.Length * 2, length + 64 * 1024), pinned: true);
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed100ns = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) * 1e7 / System.Diagnostics.Stopwatch.Frequency;
        double capacity = elapsed100ns * Environment.ProcessorCount;

        var cpu = new Dictionary<int, long>(_lastCpu.Count + 16);
        var groups = new Dictionary<string, (int Count, double Cpu, long Mem, float Gpu)>(StringComparer.OrdinalIgnoreCase);
        int processes = 0, threads = 0, handles = 0;

        unsafe
        {
            fixed (byte* basePtr = _buffer)
            {
                int offset = 0;
                while (true)
                {
                    byte* p = basePtr + offset;
                    int next = *(int*)p;
                    int pid = (int)*(long*)(p + 80);
                    if (pid != 0) // skip the Idle "process"
                    {
                        processes++;
                        threads += *(int*)(p + 4);
                        handles += *(int*)(p + 96);
                        long cpuTime = *(long*)(p + 40) + *(long*)(p + 48); // user + kernel, 100 ns units
                        long privateWs = *(long*)(p + 8);                    // Task Manager's "Memory" column
                        cpu[pid] = cpuTime;

                        ushort nameBytes = *(ushort*)(p + 56);
                        IntPtr namePtr = *(IntPtr*)(p + 64);
                        string name = nameBytes > 0 && namePtr != IntPtr.Zero
                            ? new string((char*)namePtr, 0, nameBytes / 2)
                            : "System";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

                        double used = capacity > 0 && _lastCpu.TryGetValue(pid, out var prev) && cpuTime >= prev
                            ? (cpuTime - prev) / capacity * 100
                            : 0;
                        var g = groups.GetValueOrDefault(name);
                        float gpuUsed = gpu != null && gpu.TryGetValue(pid, out var gu) ? gu : 0;
                        groups[name] = (g.Count + 1, g.Cpu + used, g.Mem + privateWs, Math.Min(100, g.Gpu + gpuUsed));
                    }
                    if (next == 0) break;
                    offset += next;
                }
            }
        }

        _lastCpu = cpu;
        _lastTimestamp = now;
        Processes = processes;
        Threads = threads;
        Handles = handles;
        Apps = groups.Select(kv => new Entry(kv.Key, kv.Value.Count, (float)kv.Value.Cpu, kv.Value.Mem, kv.Value.Gpu)).ToList();
    }

    public IEnumerable<Entry> TopByCpu(int n) => Apps.Where(a => a.Name != "System Idle Process").OrderByDescending(a => a.CpuPercent).ThenByDescending(a => a.PrivateBytes).Take(n);
    public IEnumerable<Entry> TopByMemory(int n) => Apps.OrderByDescending(a => a.PrivateBytes).Take(n);
    public IEnumerable<Entry> TopByGpu(int n) => Apps.OrderByDescending(a => a.GpuPercent).ThenByDescending(a => a.CpuPercent).Take(n);

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int returnLength);
}
