using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PCVitals;

/// <summary>
/// Draws a <see cref="PanelLayout"/>. Everything that doesn't change between readings (backgrounds,
/// labels, bar and ring tracks, text tiles) is drawn once into a cached layer; each reading only adds
/// the numbers, bar and ring fills, graphs and clocks on top. Used by the panel window, the Settings
/// preview and the preset thumbnails.
/// </summary>
sealed class PanelRenderer : IDisposable
{
    readonly PanelLayout _layout;
    readonly AppSettings? _settings;
    // By tile, not index: the editor adds and removes tiles while the renderer lives.
    readonly Dictionary<PanelTile, float> _seenMax = new(ReferenceEqualityComparer.Instance);
    Bitmap? _static;
    string? _staticKey;

    /// <summary>Longer history for graphs than the snapshot carries (key, sample count); optional.</summary>
    public Func<string, int, float[]>? HistoryProvider { get; init; }
    public int IntervalMs { get; init; } = 1000;

    public PanelRenderer(PanelLayout layout, AppSettings? settings)
    {
        _layout = layout;
        _settings = settings;
    }

    public PanelLayout Layout => _layout;

    /// <summary>The layout was edited in place: redraw the cached backgrounds and labels next time.</summary>
    public void Invalidate() => _staticKey = null;

    /// <summary>What one tile shows for one reading.</summary>
    internal sealed class TileState
    {
        public PanelReading? Reading;
        public string Label = "";
        public string Number = "", Unit = "";
        public float? Fraction;
        public Color Accent, ValueColor;
        public float[]? Graph;
        public float? GraphMax;
    }

    /// <summary>Everything a frame shows, and a signature of it so unchanged frames can be skipped.</summary>
    public sealed class Frame
    {
        internal readonly List<TileState> Tiles = new();
        public string Signature = "";
    }

    public Frame Prepare(Snapshot? snap, DateTime now)
    {
        var frame = new Frame();
        var sig = new System.Text.StringBuilder(256);
        sig.Append(Theme.Bg.ToArgb()).Append(Fmt.Fahrenheit);
        var valueColor = PanelColor.Parse(_layout.Style.ValueColor) ?? Theme.Text;
        for (int i = 0; i < _layout.Tiles.Count; i++)
        {
            var tile = _layout.Tiles[i];
            var st = new TileState { ValueColor = PanelColor.Parse(tile.ValueColor) ?? valueColor, Accent = Theme.TextDim };
            frame.Tiles.Add(st);
            switch (tile.Kind)
            {
                case PanelTileKind.Clock:
                    st.Number = FormatClock(tile.Text, now);
                    sig.Append('|').Append(st.Number);
                    continue;
                case PanelTileKind.Text when PanelText.IsLive(tile.Text):
                    // Text with readings in it changes like a value, so it is drawn with the values.
                    st.Number = PanelText.Expand(tile.Text!, snap, _settings);
                    sig.Append('|').Append(st.Number);
                    continue;
                case PanelTileKind.Text:
                    st.Label = tile.Text ?? "";
                    continue;
                case PanelTileKind.Divider or PanelTileKind.Image:
                    st.Label = tile.Image ?? "";
                    continue;
            }

            var r = snap != null ? PanelSources.Read(tile, snap, _settings) : null;
            st.Reading = r;
            st.Label = tile.Label ?? r?.Label ?? Fallback(tile);
            st.Accent = PanelColor.Parse(tile.AccentColor) ?? PanelColor.Parse(_layout.Style.AccentColor) ?? r?.Accent ?? Theme.TextDim;
            if (r == null)
            {
                st.Number = "--";
                sig.Append("|--");
                continue;
            }
            var (number, unit) = r.Text != null && tile.Decimals == null ? (r.Text, "") : PanelSources.Format(r.Value, r.Unit, tile.Decimals);
            if (r.Text != null && r.Value == null) number = "--";
            st.Number = number;
            st.Unit = tile.ShowUnit ? unit : "";

            if (r.Value is { } v && !float.IsNaN(v)) _seenMax[tile] = Math.Max(_seenMax.GetValueOrDefault(tile), v);
            float max = tile.Max ?? r.Max ?? AutoMax(r.Unit, _seenMax.GetValueOrDefault(tile));
            if (r.Value is { } value && max > 0) st.Fraction = Math.Clamp(value / max, 0, 1);

            var thresholds = tile.Thresholds ?? (r.Unit == PanelUnit.Temp ? PanelColor.TemperatureDefaults() : null);
            if (thresholds != null && PanelColor.ForValue(thresholds, r.Value) is { } hot)
            {
                st.ValueColor = hot;
                st.Accent = hot;
            }

            if ((tile.Visuals & PanelVisual.Graph) != 0 && r.HistoryKey != null && snap != null)
            {
                st.Graph = History(snap, r.HistoryKey, tile.GraphSeconds);
                st.GraphMax = tile.Max ?? r.Max;
            }
            sig.Append('|').Append(st.Number).Append(st.Unit).Append(st.ValueColor.ToArgb())
               .Append(st.Fraction is { } f ? (int)(f * 400) : -1)
               .Append(st.Graph is { Length: > 0 } g ? g.Length + ":" + g[^1] : "");
        }
        frame.Signature = sig.ToString();
        return frame;
    }

    static string Fallback(PanelTile t) => t.Sensor?.SensorName.ToUpperInvariant() ?? t.Source?.ToUpperInvariant() ?? "";

    static string FormatClock(string? format, DateTime now)
    {
        try { return now.ToString(string.IsNullOrWhiteSpace(format) ? "HH:mm" : format); }
        catch (FormatException) { return now.ToString("HH:mm"); }
    }

    /// <summary>Whether any tile shows a clock (the window then redraws on its own between readings).</summary>
    public bool HasClock => _layout.Tiles.Any(t => t.Kind == PanelTileKind.Clock);

    /// <summary>When the scale isn't known, it grows with what has been seen, from a floor that keeps idle readings low on the bar.</summary>
    static float AutoMax(PanelUnit unit, float seen)
    {
        float floor = unit switch
        {
            PanelUnit.Watts => 50,
            PanelUnit.Rate => 1024 * 1024,
            PanelUnit.Rpm => 1500,
            PanelUnit.Clock => 1000,
            PanelUnit.Ms => 50,
            PanelUnit.Gigabytes => 8,
            PanelUnit.Volts => 1.5f,
            _ => 1,
        };
        return Math.Max(floor, seen * 1.1f);
    }

    float[]? History(Snapshot snap, string key, int seconds)
    {
        int count = Math.Clamp(seconds * 1000 / Math.Max(250, IntervalMs), 10, HardwareService.LongHistoryLength);
        if (snap.History.TryGetValue(key, out var recent) && (count <= recent.Length || HistoryProvider == null))
            return recent.Length <= count ? recent : recent[^count..];
        return HistoryProvider?.Invoke(key, count);
    }

    int GraphSlots(PanelTile t) => Math.Clamp(t.GraphSeconds * 1000 / Math.Max(250, IntervalMs), 10, HardwareService.LongHistoryLength);

    // ---- Drawing ----------------------------------------------------------------------------

    /// <summary>
    /// Draws a frame onto <paramref name="g"/>, whose surface is <paramref name="canvas"/> in size. The grid is drawn at
    /// <paramref name="scale"/> from <paramref name="origin"/>; <paramref name="fill"/> means the background covers the
    /// whole canvas with square corners (a whole display).
    /// </summary>
    public void Draw(Graphics g, Frame frame, Size canvas, float scale, Point origin, bool fill)
    {
        var key = $"{canvas}|{scale}|{origin}|{fill}|{Theme.Bg.ToArgb()}|{Theme.Text.ToArgb()}|" + string.Join("\u0001", frame.Tiles.Select(t => t.Label));
        if (_static == null || _staticKey != key)
        {
            _static?.Dispose();
            _static = new Bitmap(Math.Max(1, canvas.Width), Math.Max(1, canvas.Height), PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(_static))
            {
                Setup(sg);
                sg.Clear(Color.Transparent);
                DrawStatic(sg, frame, canvas, scale, origin, fill);
            }
            _staticKey = key;
        }
        g.CompositingMode = CompositingMode.SourceOver;
        g.DrawImageUnscaled(_static, 0, 0);
        Setup(g);
        DrawDynamic(g, frame, scale, origin);
    }

    static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        // Grayscale antialiasing: ClearType needs an opaque background, and the panel may be see-through.
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    Rectangle TileRect(int i, float scale, Point origin)
    {
        var r = PanelGrid.TileRect(_layout, _layout.Tiles[i], scale);
        r.Offset(origin);
        return r;
    }

    string FamilyOf(PanelTile t) => string.IsNullOrWhiteSpace(t.FontFamily) ? _layout.Style.FontFamily : t.FontFamily!;

    void DrawStatic(Graphics g, Frame frame, Size canvas, float scale, Point origin, bool fill)
    {
        var style = _layout.Style;
        int alpha = Math.Clamp(style.BackgroundOpacity, 0, 100) * 255 / 100;
        int radius = (int)Math.Round(style.CornerRadius * scale);
        if (alpha > 0)
        {
            var bg = Theme.WithAlpha(PanelColor.Parse(style.Background) ?? Theme.Bg, alpha);
            if (fill)
            {
                using var b = new SolidBrush(bg);
                g.FillRectangle(b, 0, 0, canvas.Width, canvas.Height);
            }
            else
            {
                var size = PanelGrid.PanelSize(_layout, scale);
                Gfx.FillRounded(g, new Rectangle(origin.X, origin.Y, size.Width - 1, size.Height - 1), radius, bg);
            }
        }
        if (PanelImages.Get(style.BackgroundImage) is { } picture)
        {
            var size = PanelGrid.PanelSize(_layout, scale);
            var area = fill ? new Rectangle(Point.Empty, canvas) : new Rectangle(origin, size);
            var state = g.Save();
            if (!fill)
                using (var clip = Gfx.RoundedPath(area, radius))
                    g.SetClip(clip);
            PanelImages.Draw(g, picture, area, style.BackgroundFit);
            g.Restore(state);
        }

        var labelColor = PanelColor.Parse(style.LabelColor) ?? Theme.TextDim;
        var tileBg = PanelColor.Parse(style.TileBackground) ?? Theme.Surface;
        // Tiles keep a little body even on a see-through panel, so they stay readable over wallpaper.
        int tileAlpha = alpha == 0 ? 0 : Math.Min(255, alpha + 20);
        for (int i = 0; i < _layout.Tiles.Count; i++)
        {
            var tile = _layout.Tiles[i];
            var st = frame.Tiles[i];
            var r = TileRect(i, scale, origin);
            if (style.TileBackgrounds && tileAlpha > 0 && tile.Kind is not (PanelTileKind.Divider or PanelTileKind.Image))
                Gfx.FillRounded(g, r, Math.Max(2, radius * 2 / 3), Theme.WithAlpha(tileBg, tileAlpha));

            switch (tile.Kind)
            {
                case PanelTileKind.Divider:
                {
                    using var pen = new Pen(Theme.Border, Math.Max(1, scale));
                    if (r.Width >= r.Height) g.DrawLine(pen, r.Left + 4 * scale, r.Top + r.Height / 2f, r.Right - 4 * scale, r.Top + r.Height / 2f);
                    else g.DrawLine(pen, r.Left + r.Width / 2f, r.Top + 4 * scale, r.Left + r.Width / 2f, r.Bottom - 4 * scale);
                    continue;
                }
                case PanelTileKind.Image:
                    if (PanelImages.Get(tile.Image) is { } img)
                    {
                        var state = g.Save();
                        using (var clip = Gfx.RoundedPath(r, Math.Max(2, radius * 2 / 3)))
                            g.SetClip(clip);
                        PanelImages.Draw(g, img, r, tile.ImageFit);
                        g.Restore(state);
                    }
                    continue;
                case PanelTileKind.Text when !PanelText.IsLive(tile.Text):
                    DrawCentered(g, st.Label, r, scale, tile, PanelColor.Parse(tile.ValueColor) ?? Theme.Text, 0.42f);
                    continue;
                case PanelTileKind.Text or PanelTileKind.Clock:
                    continue;
            }

            var m = Measure(tile, r, scale, st.Label);
            if (m.Label is { } lr)
                DrawText(g, st.Label.ToUpperInvariant(), Font(FamilyOf(tile), m.LabelPx, FontStyle.Bold), labelColor, lr,
                         (tile.Visuals & PanelVisual.Ring) != 0 ? StringAlignment.Center : StringAlignment.Near);
            if (m.Bar is { } bar)
                DrawBar(g, tile, bar, 1, Theme.Track, scale);
            if (m.Ring is { } ring)
                DrawGauge(g, tile.GaugeStyle, ring, m.RingWidth, 1, Theme.Track);
        }
    }

    /// <summary>Where a gauge starts and how far it goes, in degrees clockwise from 3 o'clock.</summary>
    static (float Start, float Sweep) Arc(PanelGaugeStyle style) => style switch
    {
        PanelGaugeStyle.Semicircle => (180, 180),
        PanelGaugeStyle.Circle => (-90, 360),
        _ => (135, 270),
    };

    /// <summary>A gauge's track (<paramref name="fraction"/> 1) or its filled part.</summary>
    static void DrawGauge(Graphics g, PanelGaugeStyle style, Rectangle ring, float width, float fraction, Color color)
    {
        var (start, sweep) = Arc(style);
        if (style == PanelGaugeStyle.Segmented)
        {
            const int segments = 18;
            float step = sweep / segments, gapDeg = step * 0.28f;
            int lit = (int)Math.Round(fraction * segments);
            using var pen = new Pen(color, width);
            for (int k = 0; k < lit; k++) g.DrawArc(pen, ring, start + k * step + gapDeg / 2, step - gapDeg);
            return;
        }
        using var round = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(round, ring, start, sweep * fraction);
    }

    /// <summary>A bar's track (<paramref name="fraction"/> 1) or its filled part.</summary>
    static void DrawBar(Graphics g, PanelTile tile, Rectangle bar, float fraction, Color color, float scale)
    {
        bool vertical = tile.BarDirection == PanelBarDirection.Vertical;
        if (tile.BarStyle == PanelBarStyle.Segmented)
        {
            int length = vertical ? bar.Height : bar.Width;
            int thickness = vertical ? bar.Width : bar.Height;
            int gap = Math.Max(1, (int)Math.Round(2 * scale));
            int count = Math.Clamp(length / Math.Max(4, thickness + gap), 4, 40);
            float pitch = (length + gap) / (float)count;
            int lit = (int)Math.Round(fraction * count);
            using var brush = new SolidBrush(color);
            for (int k = 0; k < lit; k++)
            {
                float a = k * pitch, b = a + pitch - gap;
                var block = vertical
                    ? RectangleF.FromLTRB(bar.Left, bar.Bottom - b, bar.Right, bar.Bottom - a)
                    : RectangleF.FromLTRB(bar.Left + a, bar.Top, bar.Left + b, bar.Bottom);
                g.FillRectangle(brush, block);
            }
            return;
        }
        var fillRect = vertical
            ? Rectangle.FromLTRB(bar.Left, bar.Bottom - Math.Max(bar.Width, (int)(bar.Height * fraction)), bar.Right, bar.Bottom)
            : bar with { Width = Math.Max(bar.Height, (int)(bar.Width * fraction)) };
        Gfx.FillRounded(g, fillRect, Math.Min(bar.Width, bar.Height) / 2, color);
    }

    void DrawDynamic(Graphics g, Frame frame, float scale, Point origin)
    {
        var style = _layout.Style;
        for (int i = 0; i < _layout.Tiles.Count; i++)
        {
            var tile = _layout.Tiles[i];
            var st = frame.Tiles[i];
            var r = TileRect(i, scale, origin);
            if (tile.Kind == PanelTileKind.Clock)
            {
                DrawCentered(g, st.Number, r, scale, tile, st.ValueColor, 0.5f);
                continue;
            }
            if (tile.Kind == PanelTileKind.Text)
            {
                if (PanelText.IsLive(tile.Text)) DrawCentered(g, st.Number, r, scale, tile, st.ValueColor, 0.42f);
                continue;
            }
            if (tile.Kind != PanelTileKind.Value) continue;

            var m = Measure(tile, r, scale, st.Label);
            if (m.Graph is { } gr && st.Graph is { Length: > 1 } values)
                Gfx.Sparkline(g, gr, values, st.GraphMax, st.Accent, Math.Max(1.2f, 1.5f * scale), slots: GraphSlots(tile));
            if (m.Bar is { } bar && st.Fraction is { } f && f > 0)
                DrawBar(g, tile, bar, f, st.Accent, scale);
            if (m.Ring is { } ring && st.Fraction is { } rf && rf > 0.002f)
                DrawGauge(g, tile.GaugeStyle, ring, m.RingWidth, rf, st.Accent);
            if (m.Number is { } nr)
                DrawValue(g, st.Number, st.Unit, FamilyOf(tile), m.NumberPx, st.ValueColor, nr, m.NumberAlign);
        }
    }

    /// <summary>Where each part of a value tile goes.</summary>
    record struct TileMetrics(Rectangle? Label, float LabelPx, Rectangle? Number, float NumberPx, StringAlignment NumberAlign,
                              Rectangle? Bar, Rectangle? Ring, float RingWidth, Rectangle? Graph);

    TileMetrics Measure(PanelTile tile, Rectangle r, float s, string label)
    {
        float fs = Math.Clamp(tile.FontSize, 50, 250) / 100f;
        var v = tile.Visuals;
        bool hasLabel = (v & PanelVisual.Label) != 0 && label.Length > 0;
        bool number = (v & PanelVisual.Number) != 0;
        int pad = (int)Math.Round(Math.Min(9 * s, Math.Min(r.Width, r.Height) * 0.14f));
        var inner = Rectangle.Inflate(r, -pad, -pad);
        float labelPx = Math.Min(10.5f * s * fs, inner.Height * 0.28f);
        int labelH = hasLabel ? (int)Math.Ceiling(labelPx * 1.3f) : 0;

        if ((v & PanelVisual.Ring) != 0)
        {
            int gap = hasLabel ? (int)(4 * s) : 0;
            int room = inner.Height - labelH - gap;
            if (tile.GaugeStyle == PanelGaugeStyle.Semicircle)
            {
                // Only the top half of the circle is drawn, so it can be twice as wide as the room is tall.
                int sd = Math.Max(8, Math.Min(inner.Width, room * 2));
                float sw = Math.Max(3 * s, sd * 0.09f);
                int stop = inner.Top + (room - sd / 2) / 2;
                var semi = new RectangleF(inner.Left + (inner.Width - sd) / 2f, stop, sd, sd);
                var semiBox = Rectangle.Round(RectangleF.Inflate(semi, -sw / 2, -sw / 2));
                // The number sits in the bowl, on the gauge's baseline.
                var semiNumber = Rectangle.Round(new RectangleF(semi.Left + sw * 1.6f, semi.Top + sd * 0.22f, sd - sw * 3.2f, sd * 0.28f));
                return new TileMetrics(
                    hasLabel ? new Rectangle(inner.Left, stop + sd / 2 + gap, inner.Width, labelH) : null, labelPx,
                    number ? semiNumber : null, sd * 0.2f * fs, StringAlignment.Center,
                    null, semiBox, sw, null);
            }
            int d = Math.Max(8, Math.Min(inner.Width, room));
            float width = Math.Max(3 * s, d * 0.1f);
            int top = inner.Top + (room - d) / 2;
            var ring = new RectangleF(inner.Left + (inner.Width - d) / 2f, top, d, d);
            var ringBox = Rectangle.Round(RectangleF.Inflate(ring, -width / 2, -width / 2));
            var numberBox = Rectangle.Round(RectangleF.Inflate(ring, -width * 1.6f, -width * 1.6f));
            return new TileMetrics(
                hasLabel ? new Rectangle(inner.Left, top + d + gap, inner.Width, labelH) : null, labelPx,
                number ? numberBox : null, d * 0.27f * fs, StringAlignment.Center,
                null, ringBox, width, null);
        }

        Rectangle? vbar = null;
        if ((v & PanelVisual.Bar) != 0 && tile.BarDirection == PanelBarDirection.Vertical)
        {
            int bw = (int)Math.Round(Math.Max(4 * s, Math.Min(10 * s, inner.Width * 0.12f)));
            vbar = new Rectangle(inner.Right - bw, inner.Top, bw, inner.Height);
            inner.Width -= bw + (int)(7 * s);
        }

        int y = inner.Top;
        Rectangle? labelRect = hasLabel ? new Rectangle(inner.Left, y, inner.Width, labelH) : null;
        y += labelH;

        bool hbar = (v & PanelVisual.Bar) != 0 && vbar == null;
        bool graph = (v & PanelVisual.Graph) != 0;
        int barH = hbar ? (int)Math.Round(Math.Clamp(inner.Height * 0.12f, 4 * s, 8 * s)) : 0;
        Rectangle? barRect = hbar ? new Rectangle(inner.Left, inner.Bottom - barH, inner.Width, barH) : vbar;
        int bottom = inner.Bottom - (hbar ? barH + (int)(5 * s) : 0);

        Rectangle? numberRect = null;
        float numberPx = 0;
        var align = StringAlignment.Near;
        // Short tiles with a bar or graph put the number on the label's line, at the right.
        bool inline = number && hasLabel && (hbar || graph) && bottom - y < 24 * s;
        if (inline)
        {
            numberRect = labelRect;
            numberPx = labelPx * 1.6f;
            align = StringAlignment.Far;
        }
        else if (number)
        {
            int avail = Math.Max(0, bottom - y);
            int h = graph ? (int)Math.Min(avail * 0.6f, 30 * s * fs) : avail;
            numberRect = new Rectangle(inner.Left, y, inner.Width, h);
            numberPx = Math.Clamp(h * 0.95f, 10 * s, 96 * s) * (graph ? 1 : fs);
            y += h;
        }

        Rectangle? graphRect = null;
        if (graph)
        {
            var below = Rectangle.FromLTRB(inner.Left, y + (int)(2 * s), inner.Right, bottom);
            // No room under the number: draw the graph behind the whole tile instead.
            graphRect = below.Height >= 12 * s ? below : Rectangle.Inflate(r, -(int)(2 * s), -(int)(2 * s));
        }
        return new TileMetrics(labelRect, labelPx, numberRect, numberPx, align, barRect, null, 0, graphRect);
    }

    // ---- Text ---------------------------------------------------------------------------------

    static readonly StringFormat Typo = CreateFormat();

    static StringFormat CreateFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
        return f;
    }

    static readonly Dictionary<(string Family, int Px, FontStyle Style), Font> Fonts = new();

    /// <summary>Fonts by family and size (to a quarter pixel); panels only use a handful.</summary>
    static Font Font(string family, float px, FontStyle style = FontStyle.Regular)
    {
        var key = (family, (int)Math.Round(Math.Max(4, px) * 4), style);
        lock (Fonts)
        {
            if (Fonts.TryGetValue(key, out var f)) return f;
            if (Fonts.Count > 256)
            {
                foreach (var old in Fonts.Values) old.Dispose();
                Fonts.Clear();
            }
            try { f = new Font(family, key.Item2 / 4f, style, GraphicsUnit.Pixel); }
            catch (ArgumentException) { f = new Font("Segoe UI", key.Item2 / 4f, style, GraphicsUnit.Pixel); }
            return Fonts[key] = f;
        }
    }

    static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    static float Width(Graphics g, string s, Font f) => s.Length == 0 ? 0 : g.MeasureString(s, f, int.MaxValue, Typo).Width;

    static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, StringAlignment align)
    {
        float w = Width(g, s, f);
        if (w > r.Width && s.Length > 1)
        {
            // Cut long labels with an ellipsis rather than drawing past the tile.
            while (s.Length > 1 && Width(g, s + "…", f) > r.Width) s = s[..^1];
            s += "…";
            w = Width(g, s, f);
        }
        float x = align switch { StringAlignment.Center => r.Left + (r.Width - w) / 2, StringAlignment.Far => r.Right - w, _ => r.Left };
        // Centre the capital/digit height in the box: about 0.72 of the em.
        float baseline = r.Top + r.Height / 2f + f.Size * 0.36f;
        using var brush = new SolidBrush(c);
        g.DrawString(s, f, brush, x, baseline - Ascent(f), Typo);
    }

    /// <summary>A number with a smaller unit after it, shrunk to fit the box.</summary>
    static void DrawValue(Graphics g, string number, string unit, string family, float px, Color color, Rectangle r, StringAlignment align)
    {
        if (r.Height <= 0 || r.Width <= 0) return;
        px = Math.Min(px, r.Height * 1.25f);
        var nf = Font(family, px, FontStyle.Bold);
        var uf = Font(family, Math.Max(7, px * 0.48f), FontStyle.Bold);
        float gap = unit.Length > 0 && unit != "%" && !unit.StartsWith('°') ? px * 0.12f : px * 0.03f;
        float wn = Width(g, number, nf), wu = Width(g, unit, uf);
        float total = wn + (unit.Length > 0 ? gap + wu : 0);
        if (total > r.Width && total > 0)
        {
            float k = r.Width / total;
            px *= k;
            nf = Font(family, px, FontStyle.Bold);
            uf = Font(family, Math.Max(6, px * 0.48f), FontStyle.Bold);
            gap *= k;
            wn = Width(g, number, nf);
            wu = Width(g, unit, uf);
            total = wn + (unit.Length > 0 ? gap + wu : 0);
        }
        float x = align switch { StringAlignment.Center => r.Left + (r.Width - total) / 2, StringAlignment.Far => r.Right - total, _ => r.Left };
        float baseline = r.Top + r.Height / 2f + px * 0.36f;
        using var brush = new SolidBrush(color);
        g.DrawString(number, nf, brush, x, baseline - Ascent(nf), Typo);
        if (unit.Length > 0)
        {
            using var dim = new SolidBrush(Theme.WithAlpha(color, 190));
            g.DrawString(unit, uf, dim, x + wn + gap, baseline - Ascent(uf), Typo);
        }
    }

    void DrawCentered(Graphics g, string text, Rectangle r, float scale, PanelTile tile, Color color, float sizeOfHeight)
    {
        if (text.Length == 0) return;
        float fs = Math.Clamp(tile.FontSize, 50, 250) / 100f;
        float px = Math.Min(r.Height * sizeOfHeight * fs, 120 * scale);
        var inner = Rectangle.Inflate(r, -(int)(8 * scale), 0);
        DrawValue(g, text, "", FamilyOf(tile), px, color, inner, StringAlignment.Center);
    }

    // ---- Whole-panel helpers ------------------------------------------------------------------

    /// <summary>Renders a panel into a new bitmap at its natural size (previews, thumbnails, README).</summary>
    public static Bitmap Render(PanelLayout layout, Snapshot? snap, float scale, AppSettings? settings, Func<string, int, float[]>? history = null)
    {
        using var renderer = new PanelRenderer(layout, settings) { HistoryProvider = history, IntervalMs = settings?.IntervalMs ?? 1000 };
        var size = PanelGrid.PanelSize(layout, scale);
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        renderer.Draw(g, renderer.Prepare(snap, DateTime.Now), size, scale, Point.Empty, fill: false);
        return bmp;
    }

    public void Dispose()
    {
        _static?.Dispose();
        _static = null;
    }
}
