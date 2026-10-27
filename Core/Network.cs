using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace PCVitals.Core;

/// <summary>A connected Wi-Fi interface: network name, signal and link rates (no network traffic needed).</summary>
public sealed record WifiStatus(Guid Interface, string Ssid, int SignalPercent, int RxMbps, int TxMbps)
{
    /// <summary>Whether a LibreHardwareMonitor network device id ("/nic/%7B…%7D") is this interface.</summary>
    public bool Matches(string hardwareId) => hardwareId.Contains(Interface.ToString("D"), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads the current connection of each Wi-Fi interface through the WLAN API.</summary>
public static class Wlan
{
    static IntPtr _handle;
    static bool _unavailable;

    /// <summary>Connected interfaces, or empty when there is no Wi-Fi (or its service isn't running).</summary>
    public static List<WifiStatus> Query()
    {
        var result = new List<WifiStatus>();
        if (_unavailable) return result;
        if (_handle == IntPtr.Zero && WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0)
        {
            _unavailable = true; // no WLAN service (typical on desktops without Wi-Fi)
            return result;
        }
        if (WlanEnumInterfaces(_handle, IntPtr.Zero, out var list) != 0) return result;
        try
        {
            int count = Marshal.ReadInt32(list);
            for (int i = 0; i < count; i++)
            {
                // WLAN_INTERFACE_INFO_LIST: two DWORDs, then 532-byte entries (GUID, 256 WCHARs, state).
                var entry = list + 8 + i * 532;
                var guid = Marshal.PtrToStructure<Guid>(entry);
                if (Marshal.ReadInt32(entry + 16 + 512) != 1) continue; // wlan_interface_state_connected
                if (WlanQueryInterface(_handle, ref guid, 7 /* current_connection */, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                try
                {
                    result.Add(ParseConnection(guid, data));
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }
        return result;
    }

    /// <summary>WLAN_CONNECTION_ATTRIBUTES: association attributes start at byte 520.</summary>
    static WifiStatus ParseConnection(Guid guid, IntPtr data)
    {
        const int assoc = 520;
        int ssidLength = Math.Clamp(Marshal.ReadInt32(data + assoc), 0, 32);
        var ssidBytes = new byte[ssidLength];
        Marshal.Copy(data + assoc + 4, ssidBytes, 0, ssidLength);
        int signal = Marshal.ReadInt32(data + assoc + 56);
        int rxKbps = Marshal.ReadInt32(data + assoc + 60);
        int txKbps = Marshal.ReadInt32(data + assoc + 64);
        return new WifiStatus(guid, Encoding.UTF8.GetString(ssidBytes), Math.Clamp(signal, 0, 100), rxKbps / 1000, txKbps / 1000);
    }

    [DllImport("wlanapi.dll")]
    static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(IntPtr memory);
}

/// <summary>Average, jitter and loss over a window of pings.</summary>
public readonly record struct PingStats(float? AvgMs, float? JitterMs, float LossPercent, int Samples)
{
    /// <param name="samples">Round-trip times in ms, oldest first; null for a lost ping.</param>
    public static PingStats From(IReadOnlyList<float?> samples)
    {
        if (samples.Count == 0) return new PingStats(null, null, 0, 0);
        var ok = samples.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        float loss = (samples.Count - ok.Count) * 100f / samples.Count;
        if (ok.Count == 0) return new PingStats(null, null, loss, samples.Count);
        // Jitter: mean difference between consecutive replies (RFC 3550's idea, without smoothing).
        float? jitter = ok.Count < 2 ? null : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return new PingStats(ok.Average(), jitter, loss, samples.Count);
    }
}

/// <summary>
/// Opt-in latency check: one ICMP echo every few seconds to a chosen host, or to the default
/// gateway (the router, so nothing leaves the local network). Off unless the user turns it on.
/// </summary>
public sealed class LatencyMonitor : IDisposable
{
    readonly string? _host;
    readonly int _window;
    readonly Queue<float?> _samples = new();
    readonly System.Threading.Timer _timer;
    readonly Ping _ping = new();
    int _busy;

    /// <summary>What is being pinged, for display ("192.168.1.1 (router)").</summary>
    public string Target { get; private set; } = "";

    /// <param name="host">Host name or address; empty for the default gateway.</param>
    public LatencyMonitor(string? host, int intervalMs = 5000, int window = 12)
    {
        _host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        _window = window;
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, intervalMs);
    }

    public PingStats? Stats
    {
        get
        {
            lock (_samples) return _samples.Count == 0 ? null : PingStats.From(_samples.ToList());
        }
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // previous ping still waiting
        try
        {
            float? rtt = null;
            var address = _host != null ? Resolve(_host) : DefaultGateway();
            Target = _host == null ? address != null ? $"{address} (router)" : "router (not found)" : _host;
            if (address != null)
            {
                var reply = _ping.Send(address, 1000);
                if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
            }
            lock (_samples)
            {
                _samples.Enqueue(rtt);
                while (_samples.Count > _window) _samples.Dequeue();
            }
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            lock (_samples)
            {
                _samples.Enqueue(null);
                while (_samples.Count > _window) _samples.Dequeue();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    static IPAddress? Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try { return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? Dns.GetHostAddresses(host).FirstOrDefault(); }
        catch (SocketException) { return null; }
    }

    /// <summary>The IPv4 gateway of the first connected adapter that has one.</summary>
    public static IPAddress? DefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    public void Dispose()
    {
        _timer.Dispose();
        _ping.Dispose();
    }
}
