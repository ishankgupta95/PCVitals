using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice;

namespace PCVitals;

/// <summary>An animated picture decoded once and scaled to the area it is drawn in: premultiplied frames and how long each is shown.</summary>
sealed class GifFrames : IDisposable
{
    public required Bitmap[] Frames;
    /// <summary>Milliseconds each frame stays up (frames dropped to save memory add their time to the one before).</summary>
    public required int[] DelaysMs;
    /// <summary>Frames in the file; more than <see cref="Frames"/> when some were dropped to fit the memory limit.</summary>
    public int SourceFrames;
    /// <summary>The memory limit made frames get dropped or scaled down.</summary>
    public bool Capped;
    /// <summary>Frames are this much smaller than the area (1 when not scaled down); the GPU stretches them back.</summary>
    public float Shrink = 1;

    public int TotalMs => DelaysMs.Sum();
    public long Bytes => Frames.Sum(f => (long)f.Width * f.Height * 4);

    /// <summary>The frame shown <paramref name="ms"/> into the animation (it loops).</summary>
    public int IndexAt(double ms) => IndexAt(DelaysMs, ms);

    public static int IndexAt(int[] delays, double ms)
    {
        int total = delays.Sum();
        if (total <= 0 || delays.Length < 2) return 0;
        double t = ms % total;
        for (int i = 0; i < delays.Length; i++)
        {
            if (t < delays[i]) return i;
            t -= delays[i];
        }
        return delays.Length - 1;
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
    }
}

/// <summary>Finding and decoding animated GIFs used as a panel's background picture.</summary>
static class GifBackground
{
    /// <summary>Total size of decoded frames kept, in bytes.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    const int PropertyTagFrameDelay = 0x5100;
    static readonly Dictionary<string, bool> Animated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the panel's background picture is a GIF that moves, and should be drawn as one (it is, unless motion is off).</summary>
    public static bool Wants(PanelStyle style) =>
        style.Motion != PanelMotion.Off && IsAnimated(style.BackgroundImage);

    /// <summary>Whether the named picture in the panels folder is a GIF with more than one frame. Remembered per name.</summary>
    public static bool IsAnimated(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        lock (Animated)
        {
            if (Animated.TryGetValue(name, out var known)) return known;
            bool animated = false;
            try
            {
                var file = Path.Combine(PanelImages.Dir, name);
                if (File.Exists(file))
                    using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                    using (var img = Image.FromStream(stream))
                        animated = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) && img.GetFrameCount(FrameDimension.Time) > 1;
            }
            catch (Exception ex)
            {
                Program.Log($"Couldn't inspect panel picture {name}: {ex.Message}");
            }
            return Animated[name] = animated;
        }
    }

    /// <summary>Frame delays in milliseconds. A GIF stores them in hundredths of a second; 0 and 1 are what browsers show as 100 ms.</summary>
    internal static int[] Delays(Image img, int count)
    {
        var delays = new int[count];
        Array.Fill(delays, 100);
        try
        {
            if (Array.IndexOf(img.PropertyIdList, PropertyTagFrameDelay) < 0) return delays;
            var data = img.GetPropertyItem(PropertyTagFrameDelay)?.Value;
            if (data == null) return delays;
            for (int i = 0; i < count && (i + 1) * 4 <= data.Length; i++)
            {
                int centis = BitConverter.ToInt32(data, i * 4);
                delays[i] = (centis < 2 ? 10 : centis) * 10;
            }
        }
        catch (Exception)
        {
            // Odd metadata: keep the default delays.
        }
        return delays;
    }

    static Size Scaled(Size area, float shrink) =>
        shrink >= 1 ? area : new Size(Math.Max(1, (int)Math.Round(area.Width * shrink)), Math.Max(1, (int)Math.Round(area.Height * shrink)));

    /// <summary>
    /// How much smaller than the area frames can be kept without losing detail: 1 when the picture is drawn at its own size
    /// or larger (or tiled or centred, where scale matters), less when it is stretched up to cover the area.
    /// </summary>
    internal static float NativeShrink(Size picture, Size area, PanelImageFit fit)
    {
        float kx = area.Width / (float)picture.Width, ky = area.Height / (float)picture.Height;
        float k = fit switch
        {
            PanelImageFit.Fill => Math.Max(kx, ky),
            PanelImageFit.Fit => Math.Min(kx, ky),
            PanelImageFit.Stretch => Math.Min(kx, ky), // the smaller axis: neither loses detail
            _ => 1,
        };
        return k > 1 ? 1 / k : 1;
    }

    /// <summary>
    /// Decodes every frame of a GIF, each drawn into <paramref name="area"/> (at the origin) the way <paramref name="fit"/> says.
    /// Frames beyond <paramref name="maxBytes"/> of decoded pixels are dropped evenly (their time added to the frame shown
    /// before); if even two won't fit they are scaled down. Null when the data isn't an animated GIF.
    /// </summary>
    public static GifFrames? Decode(byte[] data, Size area, PanelImageFit fit, long maxBytes, CancellationToken cancel = default)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        using var stream = new MemoryStream(data);
        using var img = Image.FromStream(stream);
        int count = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? img.GetFrameCount(FrameDimension.Time) : 1;
        if (count < 2) return null;
        var delays = Delays(img, count);

        // A picture smaller than its area is kept at its own size (the GPU stretches it): the same look for far less memory.
        float shrink = NativeShrink(img.Size, area, fit);
        var natural = Scaled(area, shrink);
        long frameBytes = (long)natural.Width * natural.Height * 4;
        long fits = Math.Max(0, maxBytes / frameBytes);
        int step = 1;
        bool capped = false;
        if (fits < count)
        {
            capped = true;
            if (fits >= 2) step = (int)Math.Ceiling(count / (double)fits);
            else
            {
                // Not even two frames at that size: scale them down, and keep two.
                step = (int)Math.Ceiling(count / 2.0);
                shrink *= (float)Math.Sqrt(maxBytes / (2.0 * frameBytes));
            }
        }
        var size = Scaled(area, shrink);

        var frames = new List<Bitmap>();
        var kept = new List<int>();
        try
        {
            for (int first = 0; first < count; first += step)
            {
                cancel.ThrowIfCancellationRequested();
                img.SelectActiveFrame(FrameDimension.Time, first);
                var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                frames.Add(bmp);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(size.Width / (float)area.Width, size.Height / (float)area.Height);
                    PanelImages.Draw(g, img, new Rectangle(Point.Empty, area), fit);
                }
                kept.Add(Enumerable.Range(first, Math.Min(step, count - first)).Sum(i => delays[i]));
            }
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }
        return new GifFrames { Frames = frames.ToArray(), DelaysMs = kept.ToArray(), SourceFrames = count, Capped = capped, Shrink = shrink };
    }
}

/// <summary>
/// The GPU side of an animated background: decodes the GIF once (off the render thread), keeps the frames as Direct2D
/// bitmaps and draws the current one under the panel's still layer. Used from the render thread only.
/// </summary>
sealed class GpuGifLayer : IDisposable
{
    /// <summary>Where this layer logs (swapped out by tests and measurements).</summary>
    internal static Action<string> Log { get; set; } = Program.Log;

    string? _key;
    int _generation; // guarded by _gate
    GifFrames? _decoded; // guarded by _gate
    bool _failed; // guarded by _gate
    ID2D1Bitmap1[]? _bitmaps;
    ID2D1BitmapBrush[]? _brushes;
    int[] _delays = Array.Empty<int>();
    int _total;
    double _started;
    System.Drawing.SizeF _frameSize;

    /// <summary>Whether something is loading or playing, so the panel needs frames at its frame rate.</summary>
    public bool Active { get; private set; }

    /// <summary>Starts (or stops) the animation to match the job; cheap when nothing changed.</summary>
    public void Update(GpuPanelRenderer.Job job)
    {
        var style = job.Layout.Style;
        if (!job.AnimatedPicture || style.BackgroundImage == null)
        {
            Release();
            return;
        }
        var area = job.PictureArea;
        var key = $"{style.BackgroundImage}|{style.BackgroundFit}|{area.Width}x{area.Height}";
        if (key == _key) return;
        Release();
        // A picture that couldn't be decoded or uploaded isn't tried again on every reading (that rebuilt the GPU
        // device and decoded again each second); a different picture, fit or size gets a fresh try.
        if (key == _badKey) return;
        _badKey = null;
        _key = key;
        int gen;
        CancellationToken cancel;
        lock (_gate)
        {
            gen = _generation;
            _cancel = new CancellationTokenSource();
            cancel = _cancel.Token;
        }
        var file = Path.Combine(PanelImages.Dir, style.BackgroundImage);
        var fit = style.BackgroundFit;
        var size = new Size((int)area.Width, (int)area.Height);
        Active = true;
        Task.Run(() =>
        {
            try
            {
                var frames = GifBackground.Decode(File.ReadAllBytes(file), size, fit, GifBackground.MaxBytes, cancel);
                if (frames == null) throw new InvalidDataException("not an animated GIF");
                if (frames.Capped)
                    Log($"The animated background has {frames.SourceFrames} frames; keeping {frames.Frames.Length}" +
                        (frames.Frames.Length < frames.SourceFrames ? "" : " at reduced size") + $" to stay within {GifBackground.MaxBytes >> 20} MB.");
                // Checked and handed over under the lock Release takes, so frames for a superseded picture never land.
                GifFrames? stale = frames;
                lock (_gate)
                {
                    if (gen == _generation)
                    {
                        stale = _decoded;
                        _decoded = frames;
                    }
                }
                stale?.Dispose();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("Couldn't load the animated background: " + ex.Message);
                lock (_gate)
                    if (gen == _generation) _failed = true;
            }
        });
    }

    readonly object _gate = new();
    CancellationTokenSource? _cancel;
    string? _badKey;

    /// <summary>Draws the current frame. Returns whether the animation keeps going (the panel wants another frame).</summary>
    public bool Draw(GpuSurface surface, GpuPanelRenderer.Job job, double now)
    {
        var dc = surface.Context;
        GifFrames? ready;
        bool failed;
        lock (_gate)
        {
            ready = _decoded;
            _decoded = null;
            failed = _failed;
            _failed = false;
        }
        if (ready != null)
        {
            try { Upload(surface, ready, job, now); }
            catch (Exception ex)
            {
                // Out of video memory, say: show the panel without the animation rather than losing the whole device.
                Log("Couldn't show the animated background: " + ex.Message);
                failed = true;
            }
            finally { ready.Dispose(); }
        }
        if (failed)
        {
            var bad = _key;
            Release();
            _badKey = bad;
            return false;
        }
        if (_bitmaps == null) return Active;

        int index = 0;
        if (_total > 0) index = GifFrames.IndexAt(_delays, (now - _started) * 1000);
        var area = job.PictureArea;
        float radius = job.PictureRadius;
        // A brush clips to the rounded corners, and stretches frames that were kept smaller than the area.
        var brush = _brushes![index];
        brush.Transform = Matrix3x2.CreateScale(area.Width / _frameSize.Width, area.Height / _frameSize.Height) * Matrix3x2.CreateTranslation(area.Left, area.Top);
        var rect = new System.Drawing.RectangleF(area.Left, area.Top, area.Width, area.Height);
        if (radius > 0) dc.FillRoundedRectangle(new RoundedRectangle(rect, radius, radius), brush);
        else dc.FillRectangle(new RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), brush);
        return true;
    }

    /// <summary>
    /// Seconds until the picture shows another frame, never less than <paramref name="floor"/> (the panel's frame time).
    /// 0 while the frames are still loading, which keeps the panel polling for them.
    /// </summary>
    public double SecondsToNextFrame(double now, double floor)
    {
        if (_bitmaps == null || _total <= 0) return 0;
        double t = (now - _started) * 1000 % _total;
        foreach (var delay in _delays)
        {
            if (t < delay) return Math.Max(floor, (delay - t) / 1000 + 0.001);
            t -= delay;
        }
        return floor;
    }

    void Upload(GpuSurface surface, GifFrames frames, GpuPanelRenderer.Job job, double now)
    {
        FreeBitmaps();
        var bitmaps = new List<ID2D1Bitmap1>();
        var brushes = new List<ID2D1BitmapBrush>();
        try
        {
            foreach (var frame in frames.Frames)
            {
                var bmp = surface.Upload(frame);
                bitmaps.Add(bmp);
                brushes.Add(surface.Context.CreateBitmapBrush(bmp, new BitmapBrushProperties(ExtendMode.Clamp, ExtendMode.Clamp,
                        frame.Width != job.PictureArea.Width || frame.Height != job.PictureArea.Height ? Vortice.Direct2D1.BitmapInterpolationMode.Linear : Vortice.Direct2D1.BitmapInterpolationMode.NearestNeighbor)));
            }
        }
        catch
        {
            foreach (var b in brushes) b.Dispose();
            foreach (var b in bitmaps) b.Dispose();
            throw;
        }
        _bitmaps = bitmaps.ToArray();
        _brushes = brushes.ToArray();
        _delays = frames.DelaysMs;
        _total = frames.TotalMs;
        _frameSize = frames.Frames[0].Size;
        _started = now;
    }

    void FreeBitmaps()
    {
        if (_brushes != null) foreach (var b in _brushes) b.Dispose();
        if (_bitmaps != null) foreach (var b in _bitmaps) b.Dispose();
        _brushes = null;
        _bitmaps = null;
    }

    /// <summary>Lets go of the frames (the picture or size changed, motion is off, or the GPU device went).</summary>
    public void Release()
    {
        GifFrames? pending;
        lock (_gate)
        {
            _generation++;
            pending = _decoded;
            _decoded = null;
            _failed = false;
            // A decode still running for the old picture stops early instead of building up to 64 MB for nothing.
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;
        }
        pending?.Dispose();
        _key = null;
        Active = false;
        FreeBitmaps();
    }

    public void Dispose() => Release();
}
