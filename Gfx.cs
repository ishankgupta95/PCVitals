using System.Drawing.Drawing2D;

namespace PCVitals;

/// <summary>Fonts for one DPI, shared by the dashboard and hover cards and created once per DPI.</summary>
sealed class UiFonts
{
    static readonly Dictionary<int, UiFonts> Cache = new();

    public required Font Title, Sub, Big, Mid, Body, BodyBold, Small, Label, Icon, Heading, IconLarge;

    public static UiFonts For(int dpi)
    {
        if (Cache.TryGetValue(dpi, out var f)) return f;
        Font Px(string family, float px, FontStyle style = FontStyle.Regular) =>
            new(family, (float)Math.Round(px * dpi / 96f), style, GraphicsUnit.Pixel);
        f = new UiFonts
        {
            Title = Px("Segoe UI Semibold", 16),
            Sub = Px("Segoe UI", 12),
            Big = Px("Segoe UI Semibold", 27),
            Mid = Px("Segoe UI Semibold", 17),
            Body = Px("Segoe UI", 12.5f),
            BodyBold = Px("Segoe UI Semibold", 12.5f),
            Small = Px("Segoe UI", 11.5f),
            Label = Px("Segoe UI", 10.5f, FontStyle.Bold),
            Icon = Px("Segoe Fluent Icons", 14),
            Heading = Px("Segoe UI Semibold", 21),
            IconLarge = Px("Segoe Fluent Icons", 18),
        };
        Cache[dpi] = f;
        return f;
    }
}

/// <summary>Drawing helpers shared by the owner-drawn surfaces.</summary>
static class Gfx
{
    public const TextFormatFlags BaseFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                             TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;

    public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags = 0) =>
        TextRenderer.DrawText(g, s, f, r, c, BaseFlags | flags);

    public static int TextWidth(Graphics g, string s, Font f) => TextRenderer.MeasureText(g, s, f, Size.Empty, BaseFlags).Width;

    public static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = RoundedPath(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static GraphicsPath RoundedPath(Rectangle r, int radius, bool roundBottom = true)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        if (roundBottom)
        {
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
        }
        path.CloseFigure();
        return path;
    }

    /// <summary>Area sparkline, right-aligned so the newest sample sits on the right edge.</summary>
    public static void Sparkline(Graphics g, Rectangle r, float[]? values, float? fixedMax, Color color, float lineWidth)
    {
        if (values == null || values.Length < 2) return;
        float max = fixedMax ?? 0;
        if (max <= 0)
        {
            foreach (var v in values) if (!float.IsNaN(v)) max = Math.Max(max, v);
            max = Math.Max(max * 1.15f, 1);
        }

        int n = HardwareService.HistoryLength;
        float step = r.Width / (float)(n - 1);
        int offset = n - values.Length;
        var pts = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float v = float.IsNaN(values[i]) ? 0 : Math.Clamp(values[i] / max, 0, 1);
            pts[i] = new PointF(r.Left + (offset + i) * step, r.Bottom - 2 - v * (r.Height - 4));
        }

        using var path = new GraphicsPath();
        path.AddLines(pts);
        path.AddLine(pts[^1], new PointF(pts[^1].X, r.Bottom));
        path.AddLine(new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
        path.CloseFigure();
        using (var fill = new LinearGradientBrush(r, Theme.WithAlpha(color, 70), Theme.WithAlpha(color, 4), LinearGradientMode.Vertical))
            g.FillPath(fill, path);
        using (var pen = new Pen(Theme.WithAlpha(color, 220), lineWidth) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts);
    }

    /// <summary>iStat-style history bars; returns nothing if there is no data yet.</summary>
    public static void Bars(Graphics g, Rectangle r, float[]? values, float? fixedMax, Color color, Color track, float scale)
    {
        FillRounded(g, r, (int)Math.Round(3 * scale), track);
        if (values == null || values.Length == 0) return;
        float max = fixedMax ?? 0;
        if (max <= 0)
        {
            foreach (var v in values) if (!float.IsNaN(v)) max = Math.Max(max, v);
            max = Math.Max(max * 1.1f, 1);
        }
        int step = Math.Max(2, (int)Math.Round(2 * scale));
        int barW = Math.Max(1, step - 1);
        int inner = r.Height - 2;
        int count = Math.Min(values.Length, (r.Width - 2) / step);
        using var brush = new SolidBrush(color);
        for (int k = 0; k < count; k++)
        {
            float v = values[values.Length - count + k];
            if (float.IsNaN(v)) continue;
            int h = (int)Math.Round(Math.Clamp(v / max, 0, 1) * inner);
            if (h <= 0) h = v > 0 ? 1 : 0;
            if (h == 0) continue;
            int x = r.Right - 1 - (count - k) * step;
            g.FillRectangle(brush, x, r.Bottom - 1 - h, barW, h);
        }
    }
}
