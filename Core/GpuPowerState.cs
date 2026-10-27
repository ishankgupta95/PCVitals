using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PCVitals.Core;

/// <summary>
/// A GPU's device power state as Plug and Play last recorded it. Reading it doesn't touch the device,
/// unlike a driver query (NVAPI, ADL), which on hybrid-graphics laptops keeps a sleeping discrete GPU
/// out of D3cold and costs several watts on battery.
/// </summary>
static class GpuPowerState
{
    static readonly Regex PciIds = new(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{8}))?",
                                       RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The display adapter's device instance id (<c>PCI\VEN_10DE&amp;DEV_2860&amp;…\4&amp;…</c>) for a GPU whose
    /// device path may be unknown: matched by PCI ids, else the only adapter of <paramref name="vendor"/>.
    /// Null when that's ambiguous (two identical cards without a path).
    /// </summary>
    public static string? InstanceFor(string? devicePath, uint vendor, IReadOnlyList<string> displayInstances)
    {
        var m = devicePath == null ? Match.Empty : PciIds.Match(devicePath);
        List<string> candidates;
        if (m.Success)
        {
            string venDev = $"VEN_{m.Groups[1].Value}&DEV_{m.Groups[2].Value}";
            candidates = displayInstances.Where(i => i.Contains(venDev, StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.Groups[3].Success && candidates.Count > 1)
                candidates = candidates.Where(i => i.Contains("SUBSYS_" + m.Groups[3].Value, StringComparison.OrdinalIgnoreCase)).ToList();
            // Two identical cards: the device path ends with the instance's own suffix.
            if (candidates.Count > 1)
            {
                string path = devicePath!.Replace('#', '\\');
                candidates = candidates.Where(i => path.Contains(i, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        else
        {
            string ven = $"VEN_{vendor:X4}&";
            candidates = vendor == 0 ? new() : displayInstances.Where(i => i.Contains(ven, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>D3 (hot or cold) means powered down; D1/D2 aren't used by GPUs in practice.</summary>
    public static bool IsAsleep(int devicePowerState) => devicePowerState >= D3;

    const int D3 = 4; // DEVICE_POWER_STATE: Unspecified 0, D0 1, D1 2, D2 3, D3 4

    /// <summary>Present display adapters' device instance ids (empty if the query fails).</summary>
    public static IReadOnlyList<string> DisplayInstances()
    {
        const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        const uint Flags = 0x200 /* FILTER_CLASS */ | 0x100 /* FILTER_PRESENT */;
        try
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, DisplayClass, Flags) != 0 || length == 0) return Array.Empty<string>();
            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW(DisplayClass, buffer, length, Flags) != 0) return Array.Empty<string>();
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The device's most recent power state (DEVICE_POWER_STATE), or null if Windows doesn't say.</summary>
    public static unsafe int? Read(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0) return null;
            var key = PowerDataKey;
            CmPowerData data;
            uint size = (uint)sizeof(CmPowerData);
            if (CM_Get_DevNode_PropertyW(node, ref key, out _, &data, ref size, 0) != 0 || size < 8) return null;
            return data.MostRecentPowerState;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>DEVPKEY_Device_PowerData.</summary>
    static readonly DevPropKey PowerDataKey = new() { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 32 };

    /// <summary>CM_POWER_DATA; only the first two fields are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct CmPowerData
    {
        public uint Size;
        public int MostRecentPowerState;
        public uint Capabilities, D1Latency, D2Latency, D3Latency;
        public fixed int PowerStateMapping[7];
        public int DeepestSystemWake;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern unsafe int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, void* buffer, ref uint size, uint flags);
}
