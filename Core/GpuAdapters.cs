using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PCVitals.Core;

/// <summary>
/// Which graphics adapters Windows itself calls integrated (DXCore, Windows 10 2004 and later),
/// matched to LibreHardwareMonitor's GPUs by the PCI ids in their device path. Name and memory
/// rules can't tell newer iGPUs (Radeon 780M, Arc 140V) from cards: those reserve gigabytes of RAM.
/// </summary>
static class GpuAdapters
{
    public readonly record struct Adapter(uint Vendor, uint Device, uint SubSystem, bool Integrated);

    static readonly Regex PciIds = new(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{8}))?",
                                       RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>PCI vendor id for a GPU type (0 if none).</summary>
    public static uint VendorOf(LibreHardwareMonitor.Hardware.HardwareType type) => type switch
    {
        LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia => 0x10DE,
        LibreHardwareMonitor.Hardware.HardwareType.GpuAmd => 0x1002,
        LibreHardwareMonitor.Hardware.HardwareType.GpuIntel => 0x8086,
        _ => 0,
    };

    /// <summary>
    /// Windows' answer for a device path like <c>\\?\PCI#VEN_1002&amp;DEV_15BF&amp;SUBSYS_…</c>; null when it
    /// has none (no DXCore, no matching adapter, or matching adapters disagree). Without a path (NVIDIA's
    /// is unknown while NVML is kept unloaded) all of <paramref name="vendor"/>'s adapters must agree.
    /// </summary>
    public static bool? IsIntegrated(string? devicePath, IReadOnlyList<Adapter> adapters, uint vendor = 0)
    {
        var m = devicePath == null ? Match.Empty : PciIds.Match(devicePath);
        if (!m.Success) return vendor == 0 ? null : Agreed(adapters.Where(a => a.Vendor == vendor).ToList());
        vendor = Convert.ToUInt32(m.Groups[1].Value, 16);
        uint device = Convert.ToUInt32(m.Groups[2].Value, 16);
        var same = adapters.Where(a => a.Vendor == vendor && a.Device == device).ToList();
        if (m.Groups[3].Success)
        {
            uint sub = Convert.ToUInt32(m.Groups[3].Value, 16);
            var exact = same.Where(a => a.SubSystem == sub).ToList();
            if (exact.Count > 0) same = exact;
        }
        return Agreed(same);
    }

    static bool? Agreed(List<Adapter> adapters) =>
        adapters.Count > 0 && adapters.All(a => a.Integrated == adapters[0].Integrated) ? adapters[0].Integrated : null;

    /// <summary>The hardware graphics adapters Windows knows; empty where DXCore isn't available.</summary>
    public static IReadOnlyList<Adapter> Read()
    {
        try
        {
            return ReadDxCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return Array.Empty<Adapter>();
        }
    }

    static readonly Guid FactoryIid = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    static readonly Guid ListIid = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    static readonly Guid AdapterIid = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    static readonly Guid D3D11Graphics = new("8c47866b-7583-450d-f0f0-6bada895af4b");

    // DXCoreAdapterProperty values.
    const uint HardwareId = 3, IsHardware = 11, IsIntegratedProperty = 12;

    [StructLayout(LayoutKind.Sequential)]
    struct DXCoreHardwareId
    {
        public uint VendorId, DeviceId, SubSysId, Revision;
    }

    [DllImport("dxcore.dll", ExactSpelling = true)]
    static extern int DXCoreCreateAdapterFactory(in Guid riid, out IntPtr factory);

    // COM by hand (vtable slots from dxcore_interface.h) keeps this free of runtime COM interop.
    static unsafe List<Adapter> ReadDxCore()
    {
        var result = new List<Adapter>();
        Marshal.ThrowExceptionForHR(DXCoreCreateAdapterFactory(FactoryIid, out var factory));
        IntPtr list = IntPtr.Zero;
        try
        {
            var attribute = D3D11Graphics;
            var listIid = ListIid;
            var createList = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)Slot(factory, 3);
            Marshal.ThrowExceptionForHR(createList(factory, 1, &attribute, &listIid, &list));

            var count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(list, 4))(list);
            var getAdapter = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Slot(list, 3);
            var adapterIid = AdapterIid;
            for (uint i = 0; i < count; i++)
            {
                IntPtr adapter;
                if (getAdapter(list, i, &adapterIid, &adapter) < 0) continue;
                try
                {
                    var getProperty = (delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, void*, int>)Slot(adapter, 6);
                    DXCoreHardwareId id;
                    byte hardware, integrated;
                    if (getProperty(adapter, IsHardware, 1, &hardware) < 0 || hardware == 0) continue;
                    if (getProperty(adapter, HardwareId, (nuint)sizeof(DXCoreHardwareId), &id) < 0) continue;
                    if (getProperty(adapter, IsIntegratedProperty, 1, &integrated) < 0) continue;
                    result.Add(new Adapter(id.VendorId, id.DeviceId, id.SubSysId, integrated != 0));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            if (list != IntPtr.Zero) Marshal.Release(list);
            Marshal.Release(factory);
        }
        return result;
    }

    static unsafe IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];
}
