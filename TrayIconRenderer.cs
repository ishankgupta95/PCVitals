using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>Draws tray-sized icons: a number with an optional label above it, or the app logo.</summary>
static class TrayIconRenderer
{
    static readonly string FontName = new[] { "Bahnschrift SemiBold SemiConden", "Bahnschrift", "Segoe UI Semibold" }
        .FirstOrDefault(n => FontFamily.Families.Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
        ?? "Segoe UI";

    /// <param name="label">Small caption above the number (e.g. "CPU"), or null for number-only.</param>
    /// <param name="fraction">0..1 fill of the level bar, or null to hide it.</param>
    /// <returns>An HICON the caller owns and must free with <see cref="Destroy"/>.</returns>
    public static IntPtr Render(string? label, string text, Color textColor, Color accent, float? fraction, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            int barH = label == null ? Math.Max(2, size / 8) : Math.Max(1, size / 14);
            int top = 0;
            if (label != null)
            {
                int labelH = (int)Math.Round(size * 0.38);
                DrawFitted(g, label, accent, new RectangleF(0, -1, size, labelH + 1), labelH * 1.25f);
                top = labelH;
            }
            int textH = size - barH - top - (label == null ? 1 : 0);

            if (fraction is { } f)
            {
                using var track = new SolidBrush(Theme.WithAlpha(accent, 70));
                using var fill = new SolidBrush(accent);
                g.FillRectangle(track, 0, size - barH, size, barH);
                g.FillRectangle(fill, 0, size - barH, Math.Max(1, (int)Math.Round(size * Math.Clamp(f, 0, 1))), barH);
            }

            DrawFitted(g, text, textColor, new RectangleF(0, top, size, textH + 1), textH * 1.2f);
        }
        return bmp.GetHicon();
    }

    /// <summary>Static logo (a rounded tile with a pulse line) used when no metric icons are enabled.</summary>
    public static IntPtr RenderLogo(int size)
    {
        using var bmp = LogoBitmap(size);
        return bmp.GetHicon();
    }

    static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
    static Icon? _appIcon;

    /// <summary>The app icon at every size Windows asks for (title bars, taskbar, Alt+Tab), for the app's windows.</summary>
    public static Icon AppIcon => _appIcon ??= new Icon(new MemoryStream(IcoFile()));

    /// <summary>
    /// The logo as a .ico file with every common size: 32-bit bitmaps up to 64 px, PNG at 256 px
    /// (the usual layout; Windows and System.Drawing read both). Also the source of PCVitals.ico,
    /// regenerated with --write-icon.
    /// </summary>
    public static byte[] IcoFile()
    {
        var images = IconSizes.Select(s => s >= 256 ? Png(s) : Dib(s)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1); // icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int s = IconSizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); // no palette
            w.Write((byte)0);
            w.Write((ushort)1); // planes
            w.Write((ushort)32); // bits per pixel
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        return ms.ToArray();
    }

    static byte[] Png(int size)
    {
        using var bmp = LogoBitmap(size);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>An icon-format DIB: header with doubled height, bottom-up BGRA rows, then an empty AND mask.</summary>
    static byte[] Dib(int size)
    {
        using var bmp = LogoBitmap(size);
        int maskStride = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);
        w.Write(size);
        w.Write(size * 2);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0); // BI_RGB
        w.Write(size * size * 4 + maskStride * size);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
            {
                var c = bmp.GetPixel(x, y);
                w.Write(c.B);
                w.Write(c.G);
                w.Write(c.R);
                w.Write(c.A);
            }
        w.Write(new byte[maskStride * size]); // transparency comes from the alpha channel
        return ms.ToArray();
    }

    static Bitmap LogoBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(1, 1, size - 2, size - 2);
            using (var path = Gfx.RoundedPath(r, Math.Max(2, size / 5)))
            using (var bg = new LinearGradientBrush(r, Color.FromArgb(70, 140, 255), Color.FromArgb(120, 80, 240), 45f))
                g.FillPath(bg, path);
            float s = size;
            var pts = new[]
            {
                new PointF(s * 0.16f, s * 0.56f), new PointF(s * 0.36f, s * 0.56f), new PointF(s * 0.45f, s * 0.30f),
                new PointF(s * 0.58f, s * 0.76f), new PointF(s * 0.67f, s * 0.52f), new PointF(s * 0.84f, s * 0.52f),
            };
            using var pen = new Pen(Color.White, Math.Max(1.5f, s / 11f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, pts);
        }
        return bmp;
    }

    static void DrawFitted(Graphics g, string text, Color color, RectangleF area, float startEm)
    {
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.Alignment = StringAlignment.Center;
        fmt.LineAlignment = StringAlignment.Center;
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

        // Shrink the font until the text fits; 3 digits need a smaller size than 2.
        float em = startEm;
        Font font;
        while (true)
        {
            font = new Font(FontName, em, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font, int.MaxValue, fmt);
            if ((sz.Width <= area.Width + 1 && sz.Height <= area.Height + 3) || em <= 5) break;
            font.Dispose();
            em -= 0.5f;
        }
        using (font)
        using (var brush = new SolidBrush(color))
            g.DrawString(text, font, brush, area, fmt);
    }

    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);
}
