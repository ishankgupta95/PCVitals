using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace PCVitals;

/// <summary>How alert rules read in the UI and in notifications (in the user's temperature unit).</summary>
static class AlertText
{
    /// <summary>"CPU temperature", or a sensor's (renamed) name with its device: "Pump · Nuvoton NCT6799D".</summary>
    public static string Target(AlertRule rule, Snapshot? snap, AppSettings settings)
    {
        if (rule.Series != null && AlertRule.SeriesTargets.TryGetValue(rule.Series, out var t)) return t.Name;
        if (rule.Sensor is not { } key) return "Nothing";
        var entry = snap?.FindSensor(key);
        var name = settings.SensorName(entry?.Id ?? key.Id, key.SensorName);
        return $"{name} · {HardwareService.ShortName(entry?.Hardware.Name ?? key.HardwareName)}";
    }

    public static string Value(SensorType type, float value) => type switch
    {
        SensorType.Temperature => Fmt.Temp(value),
        _ when SensorFormat.IsPercent(type) => $"{value:0.#}%",
        _ => SensorFormat.Short(type, value),
    };

    /// <summary>A value of what the rule watches, in its unit ("23 ms", "Caution").</summary>
    public static string Value(AlertRule rule, float value) =>
        rule.Series != null && AlertRule.SeriesTargets.TryGetValue(rule.Series, out var t) ? t.Unit switch
        {
            "ms" => $"{value:0} ms",
            "health" => ((HealthLevel)(int)Math.Round(value)).ToString(),
            _ => Value(t.Type, value),
        } : Value(rule.Type, value);

    public static string Duration(int seconds) => seconds < 60 ? $"{seconds} s" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60} s";

    /// <summary>"above 90°C for 15 s"</summary>
    public static string Condition(AlertRule rule) =>
        $"{(rule.Condition == AlertCondition.Above ? "above" : "below")} {Value(rule, rule.Threshold)} for {Duration(rule.DurationSeconds)}";

    /// <summary>"Notification, sound, runs backup.cmd"</summary>
    public static string Actions(AlertRule rule)
    {
        var parts = new List<string>();
        if (rule.Notify) parts.Add("notification");
        if (rule.Sound != null) parts.Add("sound");
        if (!string.IsNullOrWhiteSpace(rule.RunProgram)) parts.Add($"runs {Path.GetFileName(rule.RunProgram)}{(rule.RunAsAdmin ? " as administrator" : "")}");
        if (parts.Count == 0) return "No action (recorded in the alert history only)";
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Dashboard page that shows what the rule watches.</summary>
    public static string? Page(AlertRule rule, Snapshot? snap) => rule.Series switch
    {
        "cputemp" => "cpu",
        "gputemp" => snap?.Summary.Gpu is { } g ? "gpu:" + g.Id : null,
        "ram" => "memory",
        "ping" or "pingloss" => "network",
        "diskhealth" => snap?.Summary.Disks.OrderByDescending(d => d.Health.Level).FirstOrDefault() is { } d ? "disk:" + d.Id : null,
        _ => rule.Sensor is { } key && snap?.FindSensor(key) is { } e ? Vitals.PageFor(e.Hardware) : null,
    };
}

/// <summary>Carries out an alert's sound and program actions (the notification is shown by the tray).</summary>
static class AlertActions
{
    public static readonly (string? Value, string Name)[] Sounds =
    {
        (null, "None"), ("Asterisk", "Windows: Asterisk"), ("Exclamation", "Windows: Exclamation"), ("Hand", "Windows: Critical stop"),
        ("Beep", "Windows: Default beep"),
    };

    public static void PlaySound(string? sound)
    {
        try
        {
            switch (sound)
            {
                case null or "": return;
                case "Asterisk": SystemSounds.Asterisk.Play(); return;
                case "Exclamation": SystemSounds.Exclamation.Play(); return;
                case "Hand": SystemSounds.Hand.Play(); return;
                case "Beep": SystemSounds.Beep.Play(); return;
                default:
                    using (var player = new SoundPlayer(sound)) player.Play();
                    return;
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Couldn't play alert sound {sound}: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the rule's program. PC Vitals usually runs as administrator; unless the rule asks for
    /// that, the program is started by the desktop shell so it runs as the signed-in user without
    /// administrator rights. Returns an error message, or null when it started.
    /// </summary>
    public static string? RunProgram(AlertRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.RunProgram)) return null;
        var file = Environment.ExpandEnvironmentVariables(rule.RunProgram.Trim());
        var args = rule.RunArguments ?? "";
        var dir = Path.GetDirectoryName(file) ?? "";
        try
        {
            if (rule.RunAsAdmin || !Program.IsAdmin())
            {
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
                return null;
            }
            DesktopShell.Run(file, args, dir);
            return null;
        }
        catch (Exception ex)
        {
            // Never fall back to running it elevated.
            Program.Log($"Couldn't run alert program {file}: {ex}");
            return ex.Message;
        }
    }
}

/// <summary>
/// Starts a program through Explorer's desktop window (IShellDispatch2.ShellExecute), so it inherits
/// Explorer's normal user rights instead of this elevated process's.
/// </summary>
static class DesktopShell
{
    public static void Run(string file, string args, string dir)
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        object location = 0; // CSIDL_DESKTOP
        object? empty = null;
        var desktop = shellWindows.FindWindowSW(ref location, ref empty, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH)
                      ?? throw new InvalidOperationException("The Windows desktop (Explorer) isn't running.");
        try
        {
            var services = (IServiceProvider)desktop;
            var sid = SID_STopLevelBrowser;
            var iid = typeof(IShellBrowser).GUID;
            var browser = (IShellBrowser)services.QueryService(ref sid, ref iid);
            browser.QueryActiveShellView(out var view);
            var dispatchId = new Guid("00020400-0000-0000-C000-000000000046"); // IDispatch
            dynamic folderView = view.GetItemObject(SVGIO_BACKGROUND, ref dispatchId);
            dynamic shell = folderView.Application;
            shell.ShellExecute(file, args, dir, "open", 1 /* SW_SHOWNORMAL */);
        }
        finally
        {
            Marshal.FinalReleaseComObject(desktop);
            Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    const uint SVGIO_BACKGROUND = 0;
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    // A dual interface: called through its vtable, so the methods before FindWindowSW must be declared (in order) even though unused.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        void get_Count();
        void Item();
        void _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc, [In, MarshalAs(UnmanagedType.Struct)] ref object? pvarlocRoot,
                             int swClass, out int pHwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        void QueryActiveShellView(out IShellView ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetItemObject(uint uItem, ref Guid riid);
    }
}
