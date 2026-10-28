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
        var tray = ChildRect(tb, "TrayNotifyWnd");
        return new TaskbarInfo(tb, r.ToRectangle(), tray, AppButtons(tb, ChildRect(tb, "ReBarWindow32"), tray),
                               ChildRect(tb, "Start"), dpi == 0 ? 96 : (int)dpi);
    }

    static readonly Dictionary<IntPtr, (Rectangle Tasks, Rectangle Trimmed, long At)> ButtonCache = new();

    /// <summary>
    /// The app-button area, trimmed to the last button where it stretches to the tray (Windows 10).
    /// The buttons come from the task list's accessibility tree; that's a few cross-process calls,
    /// so the answer is reused for a second. If it can't be read, the area is left as it is.
    /// </summary>
    static Rectangle AppButtons(IntPtr taskbar, Rectangle tasks, Rectangle tray)
    {
        if (!StripFit.StretchesToTray(tasks, tray)) return tasks;
        long now = Environment.TickCount64;
        if (ButtonCache.TryGetValue(taskbar, out var cached) && cached.Tasks == tasks && now - cached.At < 1000) return cached.Trimmed;
        var list = DescendantHandle(taskbar, "MSTaskListWClass");
        var buttons = list == IntPtr.Zero ? null : AccessibleChildRects(list);
        var trimmed = buttons == null ? tasks : StripFit.TrimTasks(tasks, buttons);
        ButtonCache[taskbar] = (tasks, trimmed, now);
        return trimmed;
    }

    /// <summary>"6 buttons, last ends at x=1434", for the taskbar report (hidden task lists too, as on Windows 11).</summary>
    public static string AppButtonSummary(IntPtr taskbar)
    {
        var list = DescendantHandle(taskbar, "MSTaskListWClass", visibleOnly: false);
        if (list == IntPtr.Zero) return "no task list window";
        var buttons = AccessibleChildRects(list);
        if (buttons == null) return "couldn't read the accessibility tree";
        return buttons.Count == 0 ? "none" : $"{buttons.Count} buttons, last ends at x={buttons.Max(b => b.Right)}";
    }

    /// <summary>Screen rects of a window's accessible children (the task list's app buttons); null if unavailable.</summary>
    internal static List<Rectangle>? AccessibleChildRects(IntPtr hwnd)
    {
        var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"); // IID_IAccessible
        object? obj = null;
        try
        {
            if (AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC /* OBJID_CLIENT */, ref iid, out obj) != 0 || obj is not Accessibility.IAccessible acc)
                return null;
            int count = acc.accChildCount;
            var rects = new List<Rectangle>();
            if (count <= 0) return rects;
            var children = new object[count];
            if (AccessibleChildren(acc, 0, count, children, out int got) < 0) return null;
            foreach (var child in children.Take(got))
            {
                int l, t, w, h;
                if (child is Accessibility.IAccessible a)
                {
                    a.accLocation(out l, out t, out w, out h, 0 /* CHILDID_SELF */);
                    Marshal.ReleaseComObject(a);
                }
                else if (child is int id) acc.accLocation(out l, out t, out w, out h, id);
                else continue;
                rects.Add(new Rectangle(l, t, w, h));
            }
            return rects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? obj);

    [DllImport("oleacc.dll")]
    static extern int AccessibleChildren(Accessibility.IAccessible container, int start, int count,
                                         [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);

    static Rectangle ChildRect(IntPtr parent, string cls)
    {
        var child = FindWindowEx(parent, IntPtr.Zero, cls, null);
        return child != IntPtr.Zero && GetWindowRect(child, out var r) ? r.ToRectangle() : Rectangle.Empty;
    }

    /// <summary>The first visible descendant (any depth) of class <paramref name="cls"/>, or zero.</summary>
    static IntPtr DescendantHandle(IntPtr parent, string cls, bool visibleOnly = true)
    {
        var found = IntPtr.Zero;
        var sb = new StringBuilder(64);
        EnumChildWindows(parent, (h, _) =>
        {
            sb.Clear();
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != cls || (visibleOnly && !IsWindowVisible(h))) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
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
        return new TaskbarInfo(handle, bounds, clock, AppButtons(handle, DescendantRect(handle, "MSTaskListWClass", "ReBarWindow32"), clock),
                               DescendantRect(handle, "Start"), dpi == 0 ? 96 : (int)dpi);
    }

    /// <summary>True while a game/video covers the whole monitor this taskbar is on, so the widget gets out of the way.</summary>
    public static bool FullscreenAppActive(IntPtr taskbar) =>
        GetForegroundWindow() != taskbar && FullscreenAppOn(Screen.FromHandle(taskbar));

    /// <summary>True while the foreground window is a game/video covering the whole of <paramref name="monitor"/>.</summary>
    public static bool FullscreenAppOn(Screen monitor)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (ClassOf(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var screen = Screen.FromHandle(fg);
        return screen.DeviceName == monitor.DeviceName &&
               IsFullscreen(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), screen.Bounds, IsZoomed(fg) && HasFrame(fg));
    }

    static string ClassOf(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString();
    }

    /// <summary>True while the desktop itself is in front: after "show desktop" (Win+D) or a click on the wallpaper.</summary>
    public static bool DesktopInFront() => GetForegroundWindow() is var fg && fg != IntPtr.Zero && ClassOf(fg) is "Progman" or "WorkerW";

    public static readonly IntPtr HWND_BOTTOM = new(1), HWND_NOTOPMOST = new(-2);
    const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8;

    /// <summary>Sets or clears extended window style bits.</summary>
    public static void SetExStyle(IntPtr hwnd, int bits, bool on)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = on ? style | (uint)bits : style & ~(uint)bits;
        if (next != style) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    /// <summary>Calls <paramref name="proc"/> on this thread whenever another window comes to the front. Keep the delegate alive.</summary>
    public static IntPtr HookForeground(WinEventProc proc) =>
        SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, proc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    const int GWL_STYLE = -16;
    const uint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;

    /// <summary>
    /// True when the window has a title bar or resize border. Browsers going fullscreen from a
    /// maximised window (YouTube, F11) stay maximised but drop both.
    /// </summary>
    static bool HasFrame(IntPtr hwnd) => HasFrame(GetWindowLong(hwnd, GWL_STYLE));

    internal static bool HasFrame(uint style) => (style & (WS_CAPTION | WS_THICKFRAME)) != 0;

    /// <summary>
    /// A window counts as fullscreen when it covers the whole monitor and isn't simply maximised.
    /// With an auto-hiding taskbar a maximised window covers the monitor too (slightly overlapping
    /// it with its borders), yet the taskbar still slides up over it, so the stats should too.
    /// Fullscreen games, videos and browsers use borderless windows sized to the screen instead
    /// (Chrome keeps the maximised flag when going fullscreen from maximised, so the caller only
    /// passes <paramref name="framedMaximized"/> for a maximised window that still has a frame).
    /// </summary>
    internal static bool IsFullscreen(Rectangle window, Rectangle monitor, bool framedMaximized) =>
        !framedMaximized && window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    /// <summary>"Chrome_WidgetWin_1 (-8,-8)-(2568,1448) maximised", for the taskbar report.</summary>
    public static string ForegroundSummary()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "none";
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        GetWindowRect(fg, out var r);
        return $"{cls} ({r.Left},{r.Top})-({r.Right},{r.Bottom}){(IsZoomed(fg) ? " maximised" : "")}{(HasFrame(fg) ? "" : " frameless")}";
    }

    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern uint GetWindowLong(IntPtr hWnd, int index);

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

    /// <summary>
    /// A top-down 32-bit premultiplied bitmap in GDI memory: GDI+ draws straight into it (<see cref="Bitmap"/>) and
    /// <see cref="Present"/> hands it to a layered window, so redraws allocate nothing.
    /// </summary>
    public sealed class LayeredSurface : IDisposable
    {
        readonly IntPtr _dc, _dib, _old;
        public Bitmap Bitmap { get; }
        public Size Size { get; }

        public LayeredSurface(Size size)
        {
            Size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
            var info = new BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = Size.Width, Height = -Size.Height, Planes = 1, BitCount = 32,
            };
            IntPtr screen = GetDC(IntPtr.Zero);
            _dc = CreateCompatibleDC(screen);
            ReleaseDC(IntPtr.Zero, screen);
            _dib = CreateDIBSection(_dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero)
            {
                DeleteDC(_dc);
                throw new OutOfMemoryException("CreateDIBSection failed");
            }
            _old = SelectObject(_dc, _dib);
            Bitmap = new Bitmap(Size.Width, Size.Height, Size.Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits);
        }

        public void Present(IntPtr hwnd, Point pos)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                var size = new SIZE { Cx = Size.Width, Cy = Size.Height };
                var src = new POINT();
                var dst = new POINT { X = pos.X, Y = pos.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _dc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            SelectObject(_dc, _old);
            DeleteObject(_dib);
            DeleteDC(_dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

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

    /// <summary>Makes Windows redraw a window's frame (e.g. the title bar after a theme change).</summary>
    public static void RefreshFrame(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | 0x0004 /* NOZORDER */ | 0x0020 /* FRAMECHANGED */);

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
