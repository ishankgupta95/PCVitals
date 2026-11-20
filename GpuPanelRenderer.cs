using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Point = System.Drawing.Point;

namespace PCVitals;

/// <summary>
/// Draws a desktop panel on the GPU (Direct2D), and animates it: numbers, bars, gauges and colours glide to
/// each new reading, graphs can scroll continuously, and the background gradient can move. The parts that
/// never change (labels, tracks, tile cards, pictures) are drawn once by <see cref="PanelRenderer"/> on the CPU
/// and kept as a GPU bitmap. Runs on the panel's render thread.
/// </summary>
sealed class GpuPanelRenderer : IDisposable
{
    /// <summary>Everything one reading needs to be drawn, made on the UI thread and handed over.</summary>
    public sealed class Job
    {
        public required PanelLayout Layout;
        public required PanelRenderer.Frame Frame;
        public Size Canvas;
        public float Scale;
        public Point Origin;
        public bool Fill;
        /// <summary>A new still layer, or null to keep the one the renderer has.</summary>
        public Bitmap? Static;
        public Color Background, GradientEnd;
        public int IntervalMs = 1000;
    }

    /// <summary>One tile's animation from the previous reading to the newest.</summary>
    struct Tween
    {
        public float FromFraction, ToFraction, FromValue, ToValue;
        public Color FromAccent, ToAccent, FromColor, ToColor;
        public bool HasFraction, HasValue;
    }

    Job? _job;
    Tween[] _tweens = Array.Empty<Tween>();
    double _arrived; // seconds, when the newest reading came in
    ID2D1Bitmap1? _static;
    ID2D1SolidColorBrush? _brush;
    ID2D1DeviceContext1? _dc1;
    ID2D1StrokeStyle? _roundCaps, _roundJoins;
    readonly Dictionary<(string Family, int Quarter, bool Bold), (IDWriteTextFormat Format, float Baseline)> _formats = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    // Kept between frames so a moving panel mostly re-issues draw calls: making text layouts, gradients and
    // geometries is most of what a frame costs. Entries not used for a while are dropped.
    readonly FrameCache<(string Text, string Family, int Quarter), TextEntry> _texts = new();
    readonly FrameCache<(RectangleF Along, bool Vertical, int From, int To), ID2D1LinearGradientBrush> _fills = new();
    readonly FrameCache<(float[] Values, Rectangle Rect, float Max, int Slots, float Width, bool Glow), SparkEntry> _sparks = new();
    readonly FrameCache<(RectangleF Box, float Start, float Sweep, float Width, bool Round, bool Glow), Stroke> _arcs = new();
    readonly FrameCache<(RectangleF Box, float Start, float Sweep), ID2D1PathGeometry> _arcPaths = new();

    sealed class TextEntry(IDWriteTextLayout layout, float baseline) : IDisposable
    {
        public readonly IDWriteTextLayout Layout = layout;
        public readonly float Baseline = baseline, Width = layout.Metrics.WidthIncludingTrailingWhitespace;
        public void Dispose() => Layout.Dispose();
    }

    /// <summary>
    /// A line tessellated once (a geometry realization), with its two glow passes: drawing it is then nearly free,
    /// where drawing the geometry itself tessellates it again every frame.
    /// </summary>
    sealed class Stroke : IDisposable
    {
        public readonly ID2D1GeometryRealization Main;
        public readonly ID2D1GeometryRealization? Wide, Wider;

        public Stroke(ID2D1DeviceContext1 dc, ID2D1Geometry geometry, float width, ID2D1StrokeStyle? style, float glowWidth)
        {
            Main = dc.CreateStrokedGeometryRealization(geometry, Tolerance, width, style);
            if (glowWidth <= 0) return;
            Wider = dc.CreateStrokedGeometryRealization(geometry, Tolerance, glowWidth * 2.2f, style);
            Wide = dc.CreateStrokedGeometryRealization(geometry, Tolerance, glowWidth * 1.5f, style);
        }

        public void Dispose()
        {
            Main.Dispose();
            Wide?.Dispose();
            Wider?.Dispose();
        }
    }

    const float Tolerance = 0.25f; // D2D1_DEFAULT_FLATTENING_TOLERANCE, for drawing at 1:1

    sealed class SparkEntry(ID2D1GeometryRealization area, Stroke line, ID2D1LinearGradientBrush shade, float step) : IDisposable
    {
        public readonly ID2D1GeometryRealization Area = area;
        public readonly Stroke Line = line;
        public readonly ID2D1LinearGradientBrush Shade = shade;
        public readonly float Step = step;
        public void Dispose()
        {
            Area.Dispose();
            Line.Dispose();
            Shade.Dispose();
        }
    }

    /// <summary>Objects reused from frame to frame, dropped after two seconds' worth of frames unused.</summary>
    sealed class FrameCache<TKey, TValue> where TKey : notnull where TValue : class, IDisposable
    {
        readonly Dictionary<TKey, (TValue Value, int Used)> _items = new();
        int _frame;

        public TValue Get(TKey key, Func<TValue> make)
        {
            if (_items.TryGetValue(key, out var hit))
            {
                _items[key] = (hit.Value, _frame);
                return hit.Value;
            }
            var value = make();
            _items[key] = (value, _frame);
            return value;
        }

        public void EndFrame()
        {
            if (++_frame % 30 != 0) return;
            List<TKey>? stale = null;
            foreach (var (key, item) in _items)
                if (_frame - item.Used > 120) (stale ??= new()).Add(key);
            if (stale == null) return;
            foreach (var key in stale)
            {
                _items[key].Value.Dispose();
                _items.Remove(key);
            }
        }

        public void Clear()
        {
            foreach (var item in _items.Values) item.Value.Dispose();
            _items.Clear();
        }
    }

    public double Now => _clock.Elapsed.TotalSeconds;

    public int FrameRate => _job?.Layout.Style.FrameRate ?? 60;

    /// <summary>Whether the renderer has a still layer for the current device (after a device loss it doesn't).</summary>
    public bool HasStatic => _static != null;

    public void Accept(Job job, GpuSurface surface)
    {
        double now = Now;
        var old = _job;
        // Carry each tile on from wherever its animation is now, so a reading that comes mid-glide doesn't jump.
        bool sameTiles = old != null && old.Layout.Id == job.Layout.Id && old.Frame.Tiles.Count == job.Frame.Tiles.Count;
        var tweens = new Tween[job.Frame.Tiles.Count];
        for (int i = 0; i < tweens.Length; i++)
        {
            var st = job.Frame.Tiles[i];
            ref var t = ref tweens[i];
            t.ToFraction = st.Fraction ?? 0;
            t.HasFraction = st.Fraction != null;
            t.ToValue = st.Value ?? 0;
            t.HasValue = st.Value != null;
            t.ToAccent = st.Accent;
            t.ToColor = st.ValueColor;
            if (sameTiles && i < _tweens.Length)
            {
                var (fraction, value, accent, color) = Current(_tweens[i], old!, now);
                bool hadFraction = _tweens[i].HasFraction, hadValue = _tweens[i].HasValue;
                t.FromFraction = hadFraction ? fraction : t.ToFraction;
                t.FromValue = hadValue ? value : t.ToValue;
                t.FromAccent = accent;
                t.FromColor = color;
            }
            else
            {
                t.FromFraction = t.ToFraction;
                t.FromValue = t.ToValue;
                t.FromAccent = t.ToAccent;
                t.FromColor = t.ToColor;
            }
        }
        _tweens = tweens;
        _arrived = now;
        _job = job;
        if (job.Static != null)
        {
            _static?.Dispose();
            _static = surface.Upload(job.Static);
            job.Static.Dispose();
            job.Static = null;
        }
    }

    /// <summary>The GPU was lost: drop everything that lived on it.</summary>
    public void ReleaseDevice()
    {
        _static?.Dispose();
        _static = null;
        _brush?.Dispose();
        _brush = null;
        // Brushes belong to the device; geometries and text don't, but go too so nothing outlives a lost device.
        _fills.Clear();
        _sparks.Clear();
        _arcs.Clear();
        _arcPaths.Clear();
        _texts.Clear();
    }

    double GlideSeconds(Job job) => Math.Min(0.45, job.IntervalMs / 1000.0 * 0.6);

    static float Ease(double p) => (float)(1 - Math.Pow(1 - Math.Clamp(p, 0, 1), 3)); // cubic ease-out

    (float Fraction, float Value, Color Accent, Color ValueColor) Current(in Tween t, Job job, double now)
    {
        float p = job.Layout.Style.Motion == PanelMotion.Off ? 1 : Ease((now - _arrived) / GlideSeconds(job));
        return (t.FromFraction + (t.ToFraction - t.FromFraction) * p,
                t.FromValue + (t.ToValue - t.FromValue) * p,
                PanelColor.Mix(t.FromAccent, t.ToAccent, p),
                PanelColor.Mix(t.FromColor, t.ToColor, p));
    }

    /// <summary>Draws the current moment. Returns whether anything is still moving (another frame is wanted).</summary>
    public bool Draw(GpuSurface surface)
    {
        var job = _job;
        var dc = surface.Context;
        _dc1 = surface.Context1;
        double now = Now;
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 0));
        if (job == null)
        {
            dc.EndDraw();
            return false;
        }
        _brush ??= dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        if (_roundCaps == null)
        {
            _roundCaps = GpuSurface.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
            _roundJoins = GpuSurface.Factory.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round });
        }

        var style = job.Layout.Style;
        DrawBackground(dc, job, now);
        if (_static != null) dc.DrawBitmap(_static, 1, Vortice.Direct2D1.BitmapInterpolationMode.NearestNeighbor);

        bool gliding = style.Motion != PanelMotion.Off && now - _arrived < GlideSeconds(job);
        bool scrolling = false;
        var tiles = job.Layout.Tiles;
        for (int i = 0; i < tiles.Count && i < job.Frame.Tiles.Count; i++)
        {
            var tile = tiles[i];
            var st = job.Frame.Tiles[i];
            var r = PanelGrid.TileRect(job.Layout, tile, job.Scale);
            r.Offset(job.Origin);
            switch (tile.Kind)
            {
                case PanelTileKind.Clock:
                    DrawCentered(dc, st.Number, r, job.Scale, tile, st.ValueColor, 0.5f, style);
                    continue;
                case PanelTileKind.Text:
                    if (PanelText.IsLive(tile.Text)) DrawCentered(dc, st.Number, r, job.Scale, tile, st.ValueColor, 0.42f, style);
                    continue;
                case not PanelTileKind.Value:
                    continue;
            }

            var (fraction, value, accent, valueColor) = Current(_tweens[i], job, now);
            var m = PanelRenderer.Measure(tile, r, job.Scale, st.Label);
            if (m.Graph is { } gr && st.Graph is { Length: > 1 } values)
            {
                double progress = style.Motion == PanelMotion.Full ? Math.Clamp((now - _arrived) / (job.IntervalMs / 1000.0), 0, 1) : 1;
                scrolling |= style.Motion == PanelMotion.Full && progress < 1;
                DrawSparkline(dc, gr, values, st.GraphMax, accent, Math.Max(1.2f, 1.5f * job.Scale), GraphSlots(tile, job), (float)progress, style.Glow);
            }
            if (m.Bar is { } bar && st.Fraction != null && fraction > 0)
                DrawBar(dc, tile, bar, fraction, accent, job.Scale, style);
            if (m.Ring is { } ring && st.Fraction != null && fraction > 0.002f)
                DrawGauge(dc, tile.GaugeStyle, ring, m.RingWidth, fraction, accent, style, gliding);
            if (m.Number is { } nr)
            {
                string number = st.Number, unit = st.Unit;
                if (st.Value != null && style.Motion != PanelMotion.Off && _tweens[i].HasValue)
                {
                    var (n, u) = PanelSources.Format(value, st.RawUnit, st.Decimals);
                    number = n;
                    if (tile.ShowUnit) unit = u;
                }
                DrawValue(dc, number, unit, Family(tile, style), m.NumberPx, valueColor, nr, m.NumberAlign);
            }
        }

        dc.EndDraw();
        _texts.EndFrame();
        _fills.EndFrame();
        _sparks.EndFrame();
        _arcs.EndFrame();
        _arcPaths.EndFrame();
        return gliding || scrolling || (style.AnimateBackground && style.Gradient != PanelGradient.None);
    }

    static int GraphSlots(PanelTile t, Job job) => Math.Clamp(t.GraphSeconds * 1000 / Math.Max(250, job.IntervalMs), 10, HardwareService.LongHistoryLength);

    static string Family(PanelTile t, PanelStyle style) => string.IsNullOrWhiteSpace(t.FontFamily) ? style.FontFamily : t.FontFamily!;

    // ---- Background --------------------------------------------------------------------------

    void DrawBackground(ID2D1DeviceContext dc, Job job, double now)
    {
        var style = job.Layout.Style;
        if (job.Background.A == 0) return;
        var size = PanelGrid.PanelSize(job.Layout, job.Scale);
        var area = job.Fill ? new RectangleF(0, 0, job.Canvas.Width, job.Canvas.Height)
                            : new RectangleF(job.Origin.X, job.Origin.Y, size.Width - 1, size.Height - 1);
        float radius = job.Fill ? 0 : (float)Math.Round(style.CornerRadius * job.Scale);

        ID2D1Brush brush;
        ID2D1GradientStopCollection? stops = null;
        if (style.Gradient == PanelGradient.None)
        {
            _brush!.Color = C(job.Background);
            brush = _brush;
        }
        else
        {
            stops = dc.CreateGradientStopCollection(new[] { new GradientStop(0, C(job.Background)), new GradientStop(1, C(job.GradientEnd)) });
            float t = style.AnimateBackground ? (float)now : 0;
            var center = new Vector2(area.Left + area.Width / 2, area.Top + area.Height / 2);
            if (style.Gradient == PanelGradient.Linear)
            {
                // A full turn every 40 s. The line spans the area's projection on the gradient's direction, so the colours reach the corners.
                double angle = (style.GradientAngle + t * 9) * Math.PI / 180;
                var dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                float half = (Math.Abs(area.Width * dir.X) + Math.Abs(area.Height * dir.Y)) / 2;
                brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(center - dir * half, center + dir * half), stops);
            }
            else
            {
                // The bright centre wanders slowly around the middle.
                var drift = style.AnimateBackground
                    ? new Vector2(area.Width * 0.2f * (float)Math.Sin(t * 0.31), area.Height * 0.2f * (float)Math.Cos(t * 0.23))
                    : Vector2.Zero;
                brush = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(center + drift, Vector2.Zero, area.Width * 0.71f, area.Height * 0.71f), stops);
            }
        }
        if (radius > 0) dc.FillRoundedRectangle(new RoundedRectangle(area, radius, radius), brush);
        else dc.FillRectangle(R(area), brush);
        if (brush != _brush) brush.Dispose();
        stops?.Dispose();
    }

    // ---- Bars, gauges, graphs ---------------------------------------------------------------

    /// <summary>The accent, or with gradient fills a shade from dim (empty end) to the accent along the whole bar or gauge.</summary>
    ID2D1Brush FillBrush(ID2D1DeviceContext dc, RectangleF along, bool vertical, Color accent, PanelStyle style)
    {
        if (!style.GradientFills)
        {
            _brush!.Color = C(accent);
            return _brush;
        }
        var from = PanelColor.FillStart(accent);
        return _fills.Get((along, vertical, from.ToArgb(), accent.ToArgb()), () =>
        {
            using var stops = dc.CreateGradientStopCollection(new[] { new GradientStop(0, C(from)), new GradientStop(1, C(accent)) });
            var start = vertical ? new Vector2(along.Left, along.Bottom) : new Vector2(along.Left, along.Top);
            var end = vertical ? new Vector2(along.Left, along.Top) : new Vector2(along.Right, along.Top);
            return dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(start, end), stops);
        });
    }

    void Glow(ID2D1DeviceContext dc, Color accent, Action<float, float> pass)
    {
        // Two soft passes, widest and faintest first: cheap, and no blur effect to allocate.
        _brush!.Color = C(Theme.WithAlpha(accent, accent.A * 20 / 255));
        pass(2.2f, 0);
        _brush.Color = C(Theme.WithAlpha(accent, accent.A * 38 / 255));
        pass(1.5f, 1);
    }

    void DrawBar(ID2D1DeviceContext dc, PanelTile tile, Rectangle bar, float fraction, Color accent, float scale, PanelStyle style)
    {
        bool vertical = tile.BarDirection == PanelBarDirection.Vertical;
        if (tile.BarStyle == PanelBarStyle.Segmented)
        {
            var blocks = PanelRenderer.BarSegments(tile, bar, fraction, scale).ToList();
            if (style.Glow)
                Glow(dc, accent, (k, _) =>
                {
                    float grow = (k - 1) * Math.Min(bar.Width, bar.Height) * 0.5f;
                    foreach (var b in blocks) dc.FillRectangle(R(RectangleF.Inflate(b, grow, grow)), _brush!);
                });
            var brush = FillBrush(dc, bar, vertical, accent, style);
            foreach (var b in blocks) dc.FillRectangle(R(b), brush);
            return;
        }
        var fill = (RectangleF)PanelRenderer.BarFill(tile, bar, fraction);
        float radius = Math.Min(bar.Width, bar.Height) / 2f;
        if (style.Glow)
            Glow(dc, accent, (k, _) =>
            {
                float grow = (k - 1) * radius * 1.2f;
                dc.FillRoundedRectangle(new RoundedRectangle(RectangleF.Inflate(fill, grow, grow), radius + grow, radius + grow), _brush!);
            });
        dc.FillRoundedRectangle(new RoundedRectangle(fill, radius, radius), FillBrush(dc, bar, vertical, accent, style));
    }

    void DrawGauge(ID2D1DeviceContext dc, PanelGaugeStyle gauge, Rectangle ring, float width, float fraction, Color accent, PanelStyle style, bool moving)
    {
        var (start, sweep) = PanelRenderer.Arc(gauge);
        var box = (RectangleF)ring;
        if (gauge == PanelGaugeStyle.Segmented)
        {
            const int segments = 18;
            float step = sweep / segments, gapDeg = step * 0.28f;
            int lit = (int)Math.Round(fraction * segments);
            var arcs = new List<Stroke>(lit);
            for (int k = 0; k < lit; k++) arcs.Add(Arc(box, start + k * step + gapDeg / 2, step - gapDeg, width, false, style.Glow));
            DrawStrokes(arcs, accent, () => FillBrush(dc, box, false, accent, style));
            return;
        }
        // Rounded to a tenth of a degree so a still gauge reuses its shape.
        float swept = (float)Math.Round(sweep * fraction, 1);
        if (!moving)
        {
            DrawStrokes(new[] { Arc(box, start, swept, width, true, style.Glow) }, accent, () => FillBrush(dc, box, false, accent, style));
            return;
        }
        // Gliding, the arc is a new shape every frame: tessellating it once into a realization would cost more than drawing it.
        var geometry = _arcPaths.Get((box, start, swept), () => ArcPath(box, start, swept));
        if (style.Glow) Glow(dc, accent, (w, _) => dc.DrawGeometry(geometry, _brush!, width * w, _roundCaps));
        dc.DrawGeometry(geometry, FillBrush(dc, box, false, accent, style), width, _roundCaps);
    }

    Stroke Arc(RectangleF box, float startDeg, float sweepDeg, float width, bool round, bool glow) =>
        _arcs.Get((box, startDeg, sweepDeg, width, round, glow), () =>
        {
            using var path = ArcPath(box, startDeg, sweepDeg);
            return new Stroke(_dc1!, path, width, round ? _roundCaps : null, glow ? width : 0);
        });

    /// <summary>Glow passes (if the strokes have them) for all, then the strokes themselves in <paramref name="brush"/>.</summary>
    void DrawStrokes(IReadOnlyList<Stroke> strokes, Color glow, Func<ID2D1Brush> brush)
    {
        if (strokes.Count > 0 && strokes[0].Wider != null)
        {
            _brush!.Color = C(Theme.WithAlpha(glow, glow.A * 20 / 255));
            foreach (var s in strokes) _dc1!.DrawGeometryRealization(s.Wider!, _brush);
            _brush.Color = C(Theme.WithAlpha(glow, glow.A * 38 / 255));
            foreach (var s in strokes) _dc1!.DrawGeometryRealization(s.Wide!, _brush);
        }
        var b = brush();
        foreach (var s in strokes) _dc1!.DrawGeometryRealization(s.Main, b);
    }

    /// <summary>An arc of the ellipse in <paramref name="box"/>, in degrees clockwise from 3 o'clock (as GDI+ draws them).</summary>
    static ID2D1PathGeometry ArcPath(RectangleF box, float startDeg, float sweepDeg)
    {
        float rx = box.Width / 2, ry = box.Height / 2;
        var c = new Vector2(box.Left + rx, box.Top + ry);
        Vector2 At(double deg) => new(c.X + rx * (float)Math.Cos(deg * Math.PI / 180), c.Y + ry * (float)Math.Sin(deg * Math.PI / 180));
        var path = GpuSurface.Factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(At(startDeg), FigureBegin.Hollow);
        // A whole circle can't be one arc segment (its ends meet), so it is drawn as two halves.
        int parts = sweepDeg >= 359.5f ? 2 : 1;
        for (int k = 1; k <= parts; k++)
            sink.AddArc(new ArcSegment
            {
                Point = At(startDeg + sweepDeg * k / parts), Size = new Vortice.Mathematics.Size(rx, ry), RotationAngle = 0,
                SweepDirection = SweepDirection.Clockwise, ArcSize = sweepDeg / parts > 180 ? ArcSize.Large : ArcSize.Small,
            });
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return path;
    }

    void DrawSparkline(ID2D1DeviceContext dc, Rectangle r, float[] values, float? fixedMax, Color color, float lineWidth, int slots, float progress, bool glow)
    {
        float max = fixedMax ?? 0;
        if (max <= 0)
        {
            foreach (var v in values) if (!float.IsNaN(v)) max = Math.Max(max, v);
            max = Math.Max(max * 1.15f, 1);
        }
        // The shapes are made once per reading; scrolling only moves them.
        var spark = _sparks.Get((values, r, max, slots, lineWidth, glow), () => MakeSpark(dc, r, values, max, slots, color, lineWidth, glow));
        dc.PushAxisAlignedClip(R(r), AntialiasMode.Aliased);
        // Scrolling: the newest point slides in from the right edge over one reading's time.
        dc.Transform = Matrix3x2.CreateTranslation(spark.Step * (1 - progress), 0);
        _dc1!.DrawGeometryRealization(spark.Area, spark.Shade);
        DrawStrokes(new[] { spark.Line }, color, () =>
        {
            _brush!.Color = C(Theme.WithAlpha(color, 220));
            return _brush;
        });
        dc.Transform = Matrix3x2.Identity;
        dc.PopAxisAlignedClip();
    }

    SparkEntry MakeSpark(ID2D1DeviceContext dc, Rectangle r, float[] values, float max, int slots, Color color, float lineWidth, bool glow)
    {
        int n = Math.Max(values.Length, slots);
        float step = r.Width / (float)(n - 1);
        int offset = n - values.Length;
        var pts = new Vector2[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float v = float.IsNaN(values[i]) ? 0 : Math.Clamp(values[i] / max, 0, 1);
            pts[i] = new Vector2(r.Left + (offset + i) * step, r.Bottom - 2 - v * (r.Height - 4));
        }
        var rest = pts[1..];
        using var area = GpuSurface.Factory.CreatePathGeometry();
        using (var sink = area.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            sink.AddLines(rest);
            sink.AddLine(new Vector2(pts[^1].X, r.Bottom));
            sink.AddLine(new Vector2(pts[0].X, r.Bottom));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        using var line = GpuSurface.Factory.CreatePathGeometry();
        using (var sink = line.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Hollow);
            sink.AddLines(rest);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        using var stops = dc.CreateGradientStopCollection(new[] { new GradientStop(0, C(Theme.WithAlpha(color, 70))), new GradientStop(1, C(Theme.WithAlpha(color, 4))) });
        var shade = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, r.Top), new Vector2(0, r.Bottom)), stops);
        return new SparkEntry(_dc1!.CreateFilledGeometryRealization(area, Tolerance),
                              new Stroke(_dc1, line, lineWidth, _roundJoins, glow ? lineWidth * 1.8f : 0), shade, step);
    }

    // ---- Text ---------------------------------------------------------------------------------

    (IDWriteTextFormat Format, float Baseline) FormatFor(string family, float px)
    {
        var key = (family, (int)Math.Round(Math.Max(4, px) * 4), true);
        if (_formats.TryGetValue(key, out var f)) return f;
        if (_formats.Count > 128)
        {
            foreach (var old in _formats.Values) old.Format.Dispose();
            _formats.Clear();
        }
        var format = GpuSurface.Text.CreateTextFormat(family, null, FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, key.Item2 / 4f, "en-us");
        format.WordWrapping = WordWrapping.NoWrap;
        // Where the baseline sits below the top of a line, to place text by its baseline as the GDI+ renderer does.
        using var probe = GpuSurface.Text.CreateTextLayout("0", format, float.MaxValue, float.MaxValue);
        var lines = new LineMetrics[1];
        probe.GetLineMetrics(lines, out _);
        return _formats[key] = (format, lines[0].Baseline);
    }

    void DrawValue(ID2D1DeviceContext dc, string number, string unit, string family, float px, Color color, Rectangle r, System.Drawing.StringAlignment align)
    {
        if (r.Height <= 0 || r.Width <= 0 || number.Length == 0) return;
        px = Math.Min(px, r.Height * 1.25f);
        float gap = unit.Length > 0 && unit != "%" && !unit.StartsWith('°') ? px * 0.12f : px * 0.03f;
        var nl = Layout(number, family, px);
        var ul = unit.Length > 0 ? Layout(unit, family, Math.Max(7, px * 0.48f)) : null;
        float total = nl.Width + (ul != null ? gap + ul.Width : 0);
        if (total > r.Width && total > 0)
        {
            float k = r.Width / total;
            px *= k;
            gap *= k;
            nl = Layout(number, family, px);
            ul = unit.Length > 0 ? Layout(unit, family, Math.Max(6, px * 0.48f)) : null;
            total = nl.Width + (ul != null ? gap + ul.Width : 0);
        }
        float x = align switch
        {
            System.Drawing.StringAlignment.Center => r.Left + (r.Width - total) / 2,
            System.Drawing.StringAlignment.Far => r.Right - total,
            _ => r.Left,
        };
        float baseline = r.Top + r.Height / 2f + px * 0.36f;
        _brush!.Color = C(color);
        dc.DrawTextLayout(new Vector2(x, baseline - nl.Baseline), nl.Layout, _brush, DrawTextOptions.None);
        if (ul != null)
        {
            _brush.Color = C(Theme.WithAlpha(color, color.A * 190 / 255));
            dc.DrawTextLayout(new Vector2(x + nl.Width + gap, baseline - ul.Baseline), ul.Layout, _brush, DrawTextOptions.None);
        }
    }

    TextEntry Layout(string s, string family, float px)
    {
        int quarter = (int)Math.Round(Math.Max(4, px) * 4);
        return _texts.Get((s, family, quarter), () =>
        {
            var (format, baseline) = FormatFor(family, quarter / 4f);
            return new TextEntry(GpuSurface.Text.CreateTextLayout(s, format, float.MaxValue, float.MaxValue), baseline);
        });
    }

    void DrawCentered(ID2D1DeviceContext dc, string text, Rectangle r, float scale, PanelTile tile, Color color, float sizeOfHeight, PanelStyle style)
    {
        if (text.Length == 0) return;
        float fs = Math.Clamp(tile.FontSize, 50, 250) / 100f;
        float px = Math.Min(r.Height * sizeOfHeight * fs, 120 * scale);
        DrawValue(dc, text, "", Family(tile, style), px, color, Rectangle.Inflate(r, -(int)(8 * scale), 0), System.Drawing.StringAlignment.Center);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    static Color4 C(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    static RawRectF R(RectangleF r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public void Dispose()
    {
        ReleaseDevice();
        _roundCaps?.Dispose();
        _roundJoins?.Dispose();
        foreach (var f in _formats.Values) f.Format.Dispose();
        _formats.Clear();
    }
}

/// <summary>
/// The panel's render thread: takes each reading from the UI thread, draws it on the GPU and keeps drawing at the
/// panel's frame rate only while something moves. A still panel costs nothing between readings.
/// </summary>
sealed class GpuPanelHost : IDisposable
{
    readonly IntPtr _hwnd;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    GpuPanelRenderer.Job? _pending;
    bool _suspend, _stop;

    /// <summary>Raised on the render thread when the still layer was lost (new GPU device) and must be sent again.</summary>
    public event Action? StaticLost;

    public GpuPanelHost(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _thread = new Thread(Run) { IsBackground = true, Name = "Panel render", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Post(GpuPanelRenderer.Job job)
    {
        lock (_gate)
        {
            // A still layer that wasn't picked up yet goes with the newer reading (unless that one has its own).
            if (_pending?.Static is { } unused)
            {
                if (job.Static == null) job.Static = unused;
                else unused.Dispose();
            }
            _pending = job;
            _suspend = false;
        }
        _wake.Set();
    }

    /// <summary>The panel was hidden: let go of the GPU surface until the next reading.</summary>
    public void Suspend()
    {
        lock (_gate) _suspend = true;
        _wake.Set();
    }

    void Run()
    {
        GpuSurface? surface = null;
        using var renderer = new GpuPanelRenderer();
        using var timer = new FrameTimer();
        bool moving = false;
        double next = 0;
        int failures = 0;
        while (true)
        {
            if (!moving) _wake.WaitOne();
            GpuPanelRenderer.Job? job;
            bool suspend;
            lock (_gate)
            {
                if (_stop) break;
                job = _pending;
                _pending = null;
                suspend = _suspend;
            }
            if (suspend)
            {
                job?.Static?.Dispose();
                renderer.ReleaseDevice();
                surface?.Dispose();
                surface = null;
                moving = false;
                continue;
            }
            try
            {
                if (job != null)
                {
                    if (surface == null)
                    {
                        surface = new GpuSurface(_hwnd, job.Canvas);
                        if (job.Static == null) StaticLost?.Invoke();
                    }
                    surface.Resize(job.Canvas);
                    renderer.Accept(job, surface);
                }
                if (surface == null)
                {
                    moving = false;
                    continue;
                }
                moving = renderer.Draw(surface);
                if (!surface.Present()) throw new InvalidOperationException("GPU device lost");
                failures = 0;
            }
            catch (Exception ex)
            {
                // Device lost (driver update, sleep, GPU reset): start again with a new device and still layer.
                Program.Log("Panel GPU drawing failed, recreating the device: " + ex.Message);
                renderer.ReleaseDevice();
                surface?.Dispose();
                surface = null;
                moving = false;
                if (++failures <= 5) StaticLost?.Invoke();
                continue;
            }

            if (moving)
            {
                // Pace to the panel's frame rate with a high-resolution timer; a new reading wakes it early.
                int fps = Math.Clamp(renderer.FrameRate, 10, 240);
                double now = renderer.Now;
                next = Math.Max(next + 1.0 / fps, now);
                if (next - now > 0.0005) timer.Wait(next - now, _wake);
            }
        }
        renderer.ReleaseDevice();
        surface?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate) _stop = true;
        _wake.Set();
        _thread.Join(2000);
        _wake.Dispose();
    }

    /// <summary>A high-resolution waitable timer (Windows 10 1803+), so 60 fps pacing doesn't round to the 15.6 ms tick.</summary>
    sealed class FrameTimer : IDisposable
    {
        readonly EventWaitHandle _handle = new(false, EventResetMode.AutoReset);

        public FrameTimer()
        {
            var h = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
            if (h == IntPtr.Zero) h = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
            _handle.SafeWaitHandle = new SafeWaitHandle(h, ownsHandle: true);
        }

        public void Wait(double seconds, WaitHandle wake)
        {
            long due = -(long)(seconds * 10_000_000); // relative, in 100 ns units
            if (!SetWaitableTimer(_handle.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                wake.WaitOne(TimeSpan.FromSeconds(seconds));
                return;
            }
            WaitHandle.WaitAny(new[] { _handle, wake });
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    }
}
