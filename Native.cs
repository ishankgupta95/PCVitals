using System.Runtime.InteropServices;
using System.Text;

namespace PCVitals;

sealed record TaskbarInfo(IntPtr Handle, Rectangle Bounds, Rectangle Tray, Rectangle Tasks, Rectangle Start, int Dpi)
{
    public bool Horizontal => Bounds.Width > Bounds.Height;

    /// <summary>The Start button plus pinned/running app icons (centred or left-aligned).</summary>
    public Rectangle Icons
    {
        get
        {
            var parts = new[] { Start, Tasks }.Where(r => r.Width > 0 && r.Height > 0).ToList();
            if (parts.Count == 0) return Rectangle.Empty;
            return parts.Aggregate(Rectangle.Union);
        }
    }
}

static class Native
{
    public const int GWLP_HWNDPARENT = -8;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE
    {
        public int Cx, Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    public static TaskbarInfo? FindTaskbar()
    {
        var tb = FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !IsWindowVisible(tb)) return null;
        GetWindowRect(tb, out var r);
        var dpi = GetDpiForWindow(tb);
        return new TaskbarInfo(tb, r.ToRectangle(), ChildRect(tb, "TrayNotifyWnd"), ChildRect(tb, "ReBarWindow32"),
                               ChildRect(tb, "Start"), dpi == 0 ? 96 : (int)dpi);
    }

    static Rectangle ChildRect(IntPtr parent, string cls)
    {
        var child = FindWindowEx(parent, IntPtr.Zero, cls, null);
        return child != IntPtr.Zero && GetWindowRect(child, out var r) ? r.ToRectangle() : Rectangle.Empty;
    }

    /// <summary>Rect of the first descendant (any depth) whose class is one of <paramref name="classes"/>.</summary>
    static Rectangle DescendantRect(IntPtr parent, params string[] classes)
    {
        var found = Rectangle.Empty;
        var sb = new StringBuilder(64);
        EnumChildWindows(parent, (h, _) =>
        {
            sb.Clear();
            GetClassName(h, sb, sb.Capacity);
            if (Array.IndexOf(classes, sb.ToString()) < 0 || !IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
            found = r.ToRectangle();
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Taskbars on other displays (when "Show my taskbar on all displays" is on). They have no
    /// notification area; the clock, if shown, sits at the right edge.
    /// </summary>
    public static List<TaskbarInfo> FindSecondaryTaskbars()
    {
        var handles = new List<IntPtr>();
        var sb = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            sb.Clear();
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "Shell_SecondaryTrayWnd" && IsWindowVisible(h)) handles.Add(h);
            return true;
        }, IntPtr.Zero);
        return handles.Select(ReadSecondary).Where(t => t != null).Select(t => t!).ToList();
    }

    /// <summary>Re-reads one secondary taskbar's geometry; null if it went away.</summary>
    public static TaskbarInfo? ReadSecondary(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || !GetWindowRect(handle, out var r)) return null;
        var bounds = r.ToRectangle();
        var dpi = GetDpiForWindow(handle);
        var clock = DescendantRect(handle, "ClockButton", "TrayClockWClass", "TrayNotifyWnd");
        if (clock.IsEmpty)
        {
            // No clock found: keep a margin where Windows 11 may draw one.
            int reserve = (int)(170 * (dpi == 0 ? 1 : dpi / 96f));
            clock = new Rectangle(bounds.Right - reserve, bounds.Top, reserve, bounds.Height);
        }
        return new TaskbarInfo(handle, bounds, clock, DescendantRect(handle, "MSTaskListWClass", "ReBarWindow32"),
                               DescendantRect(handle, "Start"), dpi == 0 ? 96 : (int)dpi);
    }

    /// <summary>True while a game/video covers the whole monitor this taskbar is on, so the widget gets out of the way.</summary>
    public static bool FullscreenAppActive(IntPtr taskbar)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == taskbar) return false;
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var screen = Screen.FromHandle(fg);
        var b = screen.Bounds;
        return screen.DeviceName == Screen.FromHandle(taskbar).DeviceName &&
               r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    /// <summary>Pushes a 32-bit ARGB bitmap to a layered window, giving per-pixel transparency.</summary>
    public static void SetLayeredBitmap(IntPtr hwnd, Bitmap bmp, Point pos)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hBmp);
        try
        {
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = pos.X, Y = pos.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hBmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int CbSize;
        public IntPtr HWnd;
        public uint CallbackMessage;
        public uint Edge;
        public RECT Rc;
        public IntPtr LParam;
    }

    [DllImport("shell32.dll")]
    static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    /// <summary>True when "Automatically hide the taskbar" is on.</summary>
    public static bool TaskbarAutoHides()
    {
        var data = new APPBARDATA { CbSize = Marshal.SizeOf<APPBARDATA>() };
        return ((ulong)SHAppBarMessage(4 /* ABM_GETSTATE */, ref data) & 1 /* ABS_AUTOHIDE */) != 0;
    }

    /// <summary>The screen area not covered by a bottom/top taskbar, even an auto-hiding one that is currently shown.</summary>
    public static Rectangle UsableArea(Screen screen)
    {
        var wa = screen.WorkingArea;
        var tb = FindTaskbar();
        if (tb == null || !tb.Horizontal || !Rectangle.Intersect(tb.Bounds, screen.Bounds).IntersectsWith(wa)) return wa;
        var shown = Rectangle.Intersect(tb.Bounds, wa);
        if (shown.Height < tb.Bounds.Height * 0.9) return wa;
        return shown.Top > wa.Top + wa.Height / 2
            ? Rectangle.FromLTRB(wa.Left, wa.Top, wa.Right, shown.Top)
            : Rectangle.FromLTRB(wa.Left, shown.Bottom, wa.Right, wa.Bottom);
    }

    /// <summary>Rounded Windows 11 corners and a title bar matching the app theme.</summary>
    public static void ApplyRoundedDarkFrame(IntPtr hwnd)
    {
        int dark = Theme.IsLight ? 0 : 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    /// <summary>
    /// Hands unused pages back to the OS. The poller touches little memory between ticks, so after
    /// startup (and after closing the dashboard) most of the working set is cold.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            // Aggressive also decommits the GC's free regions, which is where drive discovery's
            // ~40 MB of transient allocations otherwise stays parked.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                           ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
}
