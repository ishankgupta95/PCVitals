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
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
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
        return bmp.GetHicon();
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
