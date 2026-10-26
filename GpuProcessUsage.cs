using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>
/// Per-process GPU usage from Windows' "GPU Engine" performance counters (the source Task
/// Manager's GPU column uses): for each process, the busiest engine's utilisation, summed across
/// GPUs. Only sampled while a dashboard page shows processes.
/// </summary>
sealed class GpuProcessUsage : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    IntPtr _query, _counter;
    bool _failed;

    /// <summary>pid → GPU % (0–100). Empty on the first call (rates need two samples) or if unavailable.</summary>
    public Dictionary<int, float> Sample()
    {
        var result = new Dictionary<int, float>();
        if (_failed) return result;
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
                    PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
                {
                    Fail();
                    return result;
                }
                PdhCollectQueryData(_query);
                return result;
            }
            if (PdhCollectQueryData(_query) != 0) return result;

            uint size = 0, count = 0;
            int status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return result;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer) != 0) return result;
                // Per process: utilisation per (GPU, engine type), then the busiest engine per GPU.
                var perEngine = new Dictionary<(int Pid, string Luid, string Engine), double>();
                int itemSize = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, status + padding, double
                for (int i = 0; i < count; i++)
                {
                    var item = buffer + i * itemSize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                    if (name == null || value <= 0 || !TryParse(name, out var pid, out var luid, out var engine)) continue;
                    var key = (pid, luid, engine);
                    perEngine[key] = perEngine.GetValueOrDefault(key) + value;
                }
                foreach (var gpu in perEngine.GroupBy(kv => (kv.Key.Pid, kv.Key.Luid)))
                {
                    float busiest = (float)gpu.Max(kv => kv.Value);
                    result[gpu.Key.Pid] = Math.Min(100, result.GetValueOrDefault(gpu.Key.Pid) + busiest);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            Fail();
        }
        return result;
    }

    /// <summary>Parses "pid_1234_luid_0x0_0xD1E1_phys_0_eng_3_engtype_3D".</summary>
    internal static bool TryParse(string instance, out int pid, out string luid, out string engine)
    {
        pid = 0;
        luid = engine = "";
        var parts = instance.Split('_');
        int iPid = Array.IndexOf(parts, "pid"), iLuid = Array.IndexOf(parts, "luid"), iType = Array.IndexOf(parts, "engtype");
        if (iPid < 0 || iPid + 1 >= parts.Length || !int.TryParse(parts[iPid + 1], out pid)) return false;
        if (iLuid >= 0 && iLuid + 2 < parts.Length) luid = parts[iLuid + 1] + parts[iLuid + 2];
        engine = iType >= 0 && iType + 1 < parts.Length ? string.Join("_", parts.Skip(iType + 1)) : "";
        return true;
    }

    void Fail()
    {
        _failed = true;
        Dispose();
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    static extern int PdhCloseQuery(IntPtr query);
}
