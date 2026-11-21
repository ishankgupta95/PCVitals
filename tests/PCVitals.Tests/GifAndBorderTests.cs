using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using Xunit;

namespace PCVitals.Tests;

public sealed class GifAndBorderTests
{
    /// <summary>An animated GIF of plain colour frames (GDI+ writes the delays from the property list, in 1/100 s).</summary>
    internal static byte[] MakeGif(Color[] colours, int[] delaysCs, int w, int h)
    {
        var frames = colours.Select(c =>
        {
            var b = new Bitmap(w, h);
            using var g = Graphics.FromImage(b);
            g.Clear(c);
            return b;
        }).ToArray();
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Gif.Guid);
        var delays = new byte[delaysCs.Length * 4];
        for (int i = 0; i < delaysCs.Length; i++) BitConverter.GetBytes(delaysCs[i]).CopyTo(delays, i * 4);
        frames[0].SetPropertyItem(Item(0x5100, 4, delays));
        frames[0].SetPropertyItem(Item(0x5101, 3, new byte[] { 0, 0 })); // loop forever
        using var ms = new MemoryStream();
        using (var p = new EncoderParameters(1))
        {
            p.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            frames[0].Save(ms, encoder, p);
            for (int i = 1; i < frames.Length; i++)
            {
                p.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
                frames[0].SaveAdd(frames[i], p);
            }
            p.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
            frames[0].SaveAdd(p);
        }
        foreach (var f in frames) f.Dispose();
        return ms.ToArray();
    }

    static PropertyItem Item(int id, short type, byte[] value)
    {
        var item = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
        item.Id = id;
        item.Type = type;
        item.Len = value.Length;
        item.Value = value;
        return item;
    }

    static readonly Color[] Rgb = { Color.FromArgb(255, 0, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 0, 255) };

    [Fact]
    public void Gif_frames_and_delays_are_decoded()
    {
        // 0 and 1 hundredths are shown as 100 ms, as browsers do.
        var data = MakeGif(Rgb, new[] { 5, 20, 0 }, 40, 30);
        using var gif = GifBackground.Decode(data, new Size(80, 60), PanelImageFit.Stretch, GifBackground.MaxBytes)!;
        Assert.NotNull(gif);
        Assert.Equal(3, gif.Frames.Length);
        Assert.Equal(new[] { 50, 200, 100 }, gif.DelaysMs);
        Assert.False(gif.Capped);
        // The picture is smaller than its area, so frames stay at its own size (the GPU stretches them).
        Assert.All(gif.Frames, f => Assert.Equal(new Size(40, 30), f.Size));
        Assert.Equal(0.5f, gif.Shrink);
        Assert.True(gif.Frames[0].GetPixel(20, 15).R > 200 && gif.Frames[0].GetPixel(20, 15).G < 50);
        Assert.True(gif.Frames[1].GetPixel(20, 15).G > 200 && gif.Frames[1].GetPixel(20, 15).R < 50);
        Assert.True(gif.Frames[2].GetPixel(20, 15).B > 200 && gif.Frames[2].GetPixel(20, 15).G < 50);

        // The animation loops over its total time.
        Assert.Equal(350, gif.TotalMs);
        Assert.Equal(0, gif.IndexAt(10));
        Assert.Equal(1, gif.IndexAt(60));
        Assert.Equal(2, gif.IndexAt(260));
        Assert.Equal(0, gif.IndexAt(350 + 10));
    }

    [Fact]
    public void A_still_picture_is_not_an_animation()
    {
        using var ms = new MemoryStream();
        using (var bmp = new Bitmap(10, 10)) bmp.Save(ms, ImageFormat.Png);
        Assert.Null(GifBackground.Decode(ms.ToArray(), new Size(10, 10), PanelImageFit.Fill, GifBackground.MaxBytes));
    }

    [Fact]
    public void Too_many_frames_are_dropped_evenly_and_keep_the_total_time()
    {
        var colours = Enumerable.Range(0, 6).Select(i => Color.FromArgb(i * 40, 0, 0)).ToArray();
        var data = MakeGif(colours, new[] { 10, 10, 10, 10, 10, 10 }, 100, 100);
        long frameBytes = 100 * 100 * 4;
        using var gif = GifBackground.Decode(data, new Size(100, 100), PanelImageFit.Stretch, frameBytes * 3)!;
        Assert.True(gif.Capped);
        Assert.Equal(6, gif.SourceFrames);
        Assert.Equal(3, gif.Frames.Length);
        Assert.Equal(new[] { 200, 200, 200 }, gif.DelaysMs);
        Assert.Equal(600, gif.TotalMs);
        Assert.True(gif.Bytes <= frameBytes * 3);
        Assert.Equal(1f, gif.Shrink);
    }

    [Fact]
    public void Frames_shrink_when_not_even_two_fit()
    {
        var data = MakeGif(Rgb, new[] { 10, 10, 10 }, 100, 100);
        long frameBytes = 100 * 100 * 4;
        long limit = frameBytes + frameBytes / 4; // one full frame and a bit
        using var gif = GifBackground.Decode(data, new Size(100, 100), PanelImageFit.Stretch, limit)!;
        Assert.True(gif.Capped);
        Assert.True(gif.Shrink < 1);
        Assert.True(gif.Frames.Length >= 2);
        Assert.True(gif.Bytes <= limit, $"{gif.Bytes} > {limit}");
        Assert.Equal(300, gif.TotalMs);
    }

    [Fact]
    public void Missing_or_still_pictures_do_not_ask_for_the_animated_path()
    {
        Assert.False(GifBackground.Wants(new PanelStyle { BackgroundImage = null }));
        Assert.False(GifBackground.Wants(new PanelStyle { BackgroundImage = "missing.gif" }));
        Assert.False(GifBackground.IsAnimated("picture.png"));
    }

    [Fact]
    public void Animated_gif_is_found_by_name_and_left_out_of_the_still_layer_only_when_asked()
    {
        var old = PanelImages.Dir;
        var dir = Path.Combine(Path.GetTempPath(), "pcvitals-gif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PanelImages.Dir = dir;
            File.WriteAllBytes(Path.Combine(dir, "bg.gif"), MakeGif(Rgb, new[] { 10, 10, 10 }, 20, 20));
            Assert.True(GifBackground.IsAnimated("bg.gif"));
            Assert.True(GifBackground.Wants(new PanelStyle { BackgroundImage = "bg.gif", Motion = PanelMotion.Smooth }));
            Assert.False(GifBackground.Wants(new PanelStyle { BackgroundImage = "bg.gif", Motion = PanelMotion.Off }));

            var layout = new PanelLayout { Columns = 1, Rows = 1, CellWidth = 80, CellHeight = 60 };
            layout.Style.BackgroundOpacity = 0;
            layout.Style.BackgroundImage = "bg.gif";
            layout.Style.BackgroundFit = PanelImageFit.Stretch;
            using var renderer = new PanelRenderer(layout, null);
            var frame = renderer.Prepare(null, DateTime.Now);
            var size = PanelGrid.PanelSize(layout, 1);
            using var withPicture = renderer.RenderStatic(frame, size, 1, Point.Empty, false, background: false);
            using var without = renderer.RenderStatic(frame, size, 1, Point.Empty, false, background: false, picture: false);
            // The still layer shows the first frame (red), or nothing when the GPU draws the animation itself.
            Assert.True(withPicture.GetPixel(size.Width / 2, size.Height / 2).R > 200);
            Assert.Equal(0, without.GetPixel(size.Width / 2, size.Height / 2).A);
        }
        finally
        {
            PanelImages.Dir = old;
            Directory.Delete(dir, recursive: true);
        }
    }

    static PanelLayout OneTile(PanelTileBorder border, int width = 1, string accent = "#00FF00")
    {
        var layout = new PanelLayout { Columns = 1, Rows = 1, CellWidth = 100, CellHeight = 60 };
        layout.Tiles.Add(new PanelTile { Kind = PanelTileKind.Value, Source = "cpu", AccentColor = accent });
        layout.Style.BackgroundOpacity = 100;
        layout.Style.Background = "#000000";
        layout.Style.TileBackground = "#202020";
        layout.Style.TileBorder = border;
        layout.Style.TileBorderWidth = width;
        return layout;
    }

    static Color EdgePixel(PanelLayout layout, float scale, int inset)
    {
        using var bmp = PanelRenderer.Render(layout, null, scale, null);
        var r = PanelGrid.TileRect(layout, layout.Tiles[0], scale);
        return bmp.GetPixel(r.Left + inset, r.Top + r.Height / 2);
    }

    [Fact]
    public void Tile_borders_draw_at_the_tile_edge()
    {
        var none = EdgePixel(OneTile(PanelTileBorder.None), 1, 0);
        var subtle = EdgePixel(OneTile(PanelTileBorder.Subtle), 1, 0);
        var accent = EdgePixel(OneTile(PanelTileBorder.Accent), 1, 0);
        Assert.NotEqual(none.ToArgb(), subtle.ToArgb());
        Assert.NotEqual(subtle.ToArgb(), accent.ToArgb());
        Assert.True(accent.G > 200 && accent.R < 60, $"accent {accent}");

        // The width counts: 3 px reaches two pixels in, 1 px doesn't.
        var thin = EdgePixel(OneTile(PanelTileBorder.Accent, 1), 1, 2);
        var thick = EdgePixel(OneTile(PanelTileBorder.Accent, 3), 1, 2);
        Assert.NotEqual(thin.ToArgb(), thick.ToArgb());
        Assert.True(thick.G > 200, $"thick {thick}");

        // And it scales with the panel.
        Assert.True(EdgePixel(OneTile(PanelTileBorder.Accent, 1), 2, 1).G > 200);
    }

    [Fact]
    public void Tile_borders_follow_the_corner_radius()
    {
        // A round corner leaves the pixel just inside the tile corner uncoloured; a near-square one draws the line there.
        Color Corner(int radius)
        {
            var layout = OneTile(PanelTileBorder.Accent, 2);
            layout.Style.CornerRadius = radius;
            using var bmp = PanelRenderer.Render(layout, null, 1, null);
            var r = PanelGrid.TileRect(layout, layout.Tiles[0], 1);
            return bmp.GetPixel(r.Left + 1, r.Top + 1);
        }
        Assert.True(Corner(0).G > Corner(30).G + 50, $"{Corner(0)} vs {Corner(30)}");
        Assert.True(Corner(30).G < 100);
    }

    [Fact]
    public void Tile_border_settings_survive_a_save_and_default_to_none()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcvitals-border-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = AppSettings.CreateDefault();
            var panel = new PanelLayout();
            panel.Style.TileBorder = PanelTileBorder.Accent;
            panel.Style.TileBorderWidth = 3;
            s.Panels.Add(panel);
            var path = Path.Combine(dir, "settings.json");
            s.SaveTo(path);
            foreach (var back in new[] { AppSettings.LoadFrom(path).Panels.Single(), panel.Clone() })
            {
                Assert.Equal(PanelTileBorder.Accent, back.Style.TileBorder);
                Assert.Equal(3, back.Style.TileBorderWidth);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }

        var fresh = new PanelStyle();
        Assert.Equal(PanelTileBorder.None, fresh.TileBorder);
        Assert.Equal(1, fresh.TileBorderWidth);
    }
}
