using LibreHardwareMonitor.Hardware;
using Xunit;

namespace PCVitals.Tests;

public sealed class PanelEditorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcvitals-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _oldImages = PanelImages.Dir;

    public PanelEditorTests()
    {
        Directory.CreateDirectory(_dir);
        PanelImages.Dir = Path.Combine(_dir, "panels");
    }

    public void Dispose()
    {
        PanelImages.Dir = _oldImages;
        Directory.Delete(_dir, recursive: true);
    }

    static readonly HardwareReading Fan = new("/lpc/nct6799d/0", "Nuvoton NCT6799D", HardwareType.SuperIO, new[]
    {
        new SensorReading("/lpc/nct6799d/0/fan/1", "Fan #2", SensorType.Fan, 1234f, 900f, 1500f),
    });

    static Snapshot Snap()
    {
        var gpu = new GpuSummary { Id = "/gpu-nvidia/0", Name = "RTX", ShortName = "RTX", Load = 30, Temp = 55, Power = 120 };
        var summary = new Summary { CpuName = "x", CpuLoad = 12.4f, CpuTemp = 70.2f, RamTotalGb = 32, RamLoad = 37, Gpus = new[] { gpu }, Gpu = gpu };
        return new Snapshot(DateTime.Now, new[] { Fan }, summary, new Dictionary<string, float[]>(), new[] { Fan });
    }

    static PanelLayout Grid(int columns = 3, int rows = 2) => new() { Columns = columns, Rows = rows, CellWidth = 100, CellHeight = 50, Gap = 6, Padding = 8 };

    [Fact]
    public void Grid_moves_refuse_taken_or_outside_cells()
    {
        var l = Grid();
        l.Tiles.Add(new PanelTile { Column = 0, Row = 0 });
        l.Tiles.Add(new PanelTile { Column = 1, Row = 0 });
        Assert.False(PanelGrid.Place(l, 0, new Rectangle(1, 0, 1, 1))); // taken
        Assert.False(PanelGrid.Place(l, 0, new Rectangle(2, 1, 2, 1))); // past the right edge
        Assert.Equal((0, 0), (l.Tiles[0].Column, l.Tiles[0].Row)); // unchanged
        Assert.True(PanelGrid.Place(l, 0, new Rectangle(0, 1, 3, 1)));
        Assert.Equal(new Rectangle(0, 1, 3, 1), l.Tiles[0].Cells);
        // A tile may stay on its own cells while it grows.
        Assert.True(PanelGrid.Fits(l, 1, new Rectangle(1, 0, 2, 1)));
    }

    [Fact]
    public void Adding_to_a_full_grid_adds_a_row()
    {
        var l = Grid(columns: 2, rows: 1);
        PanelGrid.Add(l, new PanelTile());
        PanelGrid.Add(l, new PanelTile());
        Assert.Equal(1, l.Rows);
        PanelGrid.Add(l, new PanelTile { ColumnSpan = 2 });
        Assert.Equal(2, l.Rows);
        Assert.Equal((0, 1), (l.Tiles[2].Column, l.Tiles[2].Row));
        Assert.Empty(PanelGrid.Problems(l));
    }

    [Fact]
    public void Shrinking_the_grid_is_refused_while_tiles_are_in_the_way()
    {
        var l = Grid();
        l.Tiles.Add(new PanelTile { Column = 2, Row = 1 });
        Assert.False(PanelGrid.Resize(l, 2, 2));
        Assert.False(PanelGrid.Resize(l, 3, 1));
        Assert.Equal((3, 2), (l.Columns, l.Rows));
        Assert.True(PanelGrid.Resize(l, 4, 3));
    }

    [Fact]
    public void Switching_to_free_placement_keeps_tiles_where_they_are()
    {
        var l = Grid();
        l.Tiles.Add(new PanelTile { Source = "cpu", Column = 1, Row = 0, ColumnSpan = 2 });
        l.Tiles.Add(new PanelTile { Source = "gpu", Column = 0, Row = 1 });
        var before = l.Tiles.Select(t => PanelGrid.TileRect(l, t, 1.5f)).ToList();
        var size = PanelGrid.PanelSize(l, 1);

        PanelGrid.SetFreePlacement(l, true);
        Assert.True(l.FreePlacement);
        Assert.Equal(size, new Size(l.Width, l.Height));
        Assert.Equal(before, l.Tiles.Select(t => PanelGrid.TileRect(l, t, 1.5f)).ToList());
        Assert.Empty(PanelGrid.Problems(l));

        // And back: the same cells again.
        PanelGrid.SetFreePlacement(l, false);
        Assert.Equal(new Rectangle(1, 0, 2, 1), l.Tiles.Single(t => t.Source == "cpu").Cells);
        Assert.Equal(new Rectangle(0, 1, 1, 1), l.Tiles.Single(t => t.Source == "gpu").Cells);
        Assert.Empty(PanelGrid.Problems(l));
    }

    [Fact]
    public void Free_placement_hit_test_finds_the_tile_on_top()
    {
        var l = new PanelLayout { FreePlacement = true, Width = 300, Height = 200 };
        l.Tiles.Add(new PanelTile { X = 0, Y = 0, Width = 200, Height = 100 });
        l.Tiles.Add(new PanelTile { X = 100, Y = 50, Width = 100, Height = 100 });
        Assert.Equal(1, PanelGrid.HitTest(l, new Point(150, 75), 1));
        Assert.Equal(0, PanelGrid.HitTest(l, new Point(50, 25), 1));
        Assert.Equal(1, PanelGrid.HitTest(l, new Point(300, 225), 2)); // (150, 112) at 100 %
        Assert.Equal(-1, PanelGrid.HitTest(l, new Point(250, 25), 1));
        Assert.Equal(new Size(600, 400), PanelGrid.PanelSize(l, 2));
    }

    [Fact]
    public void Free_tiles_must_stay_on_the_panel()
    {
        var l = new PanelLayout { FreePlacement = true, Width = 300, Height = 200 };
        l.Tiles.Add(new PanelTile { X = 250, Y = 0, Width = 100, Height = 50 });
        l.Tiles.Add(new PanelTile { X = 0, Y = 0, Width = 8, Height = 50 });
        var problems = PanelGrid.Problems(l);
        Assert.Contains(problems, p => p.Contains("tile 0") && p.Contains("outside"));
        Assert.Contains(problems, p => p.Contains("tile 1") && p.Contains("smaller"));

        var added = PanelGrid.Add(l, new PanelTile { Width = 120, Height = 60 });
        Assert.Equal(58, l.Tiles[added].Y); // below the others, with a margin
        Assert.True(l.Height >= 58 + 60);
    }

    [Fact]
    public void Snapping_picks_the_nearest_guide_within_reach()
    {
        var (xs, ys) = PanelGrid.Guides(new PanelLayout { FreePlacement = true, Width = 300, Height = 200, Tiles = { new() { X = 40, Y = 10, Width = 100, Height = 50 } } }, -1);
        Assert.Contains(140, xs);
        Assert.Contains(150, xs); // centre
        Assert.Equal((140, 140), PanelGrid.Snap(143, xs, 6));
        Assert.Equal((147, (int?)null), PanelGrid.Snap(147, new[] { 140, 160 }, 6)); // nothing within reach
        Assert.Equal((150, 150), PanelGrid.Snap(148, xs, 6));
        Assert.Contains(60, ys);
    }

    [Fact]
    public void Text_placeholders_become_readings()
    {
        Fmt.Fahrenheit = false;
        var snap = Snap();
        Assert.Equal("CPU 70°C, 12%", PanelText.Expand("CPU {cputemp}, {cpu}", snap, null));
        Assert.Equal("Fan 1234 RPM", PanelText.Expand("Fan {sensor:/lpc/nct6799d/0/fan/1}", snap, null));
        Assert.Equal("{nonsense} stays", PanelText.Expand("{nonsense} stays", snap, null));
        Assert.Equal("{cpu}", PanelText.Expand("{cpu}", null, null));
        Assert.True(PanelText.IsLive("a {gpu}"));
        Assert.False(PanelText.IsLive("plain text"));
    }

    [Fact]
    public void Layouts_export_and_import_with_their_pictures()
    {
        // A tiny picture as the background and in an image tile.
        var source = Path.Combine(_dir, "wallpaper.png");
        using (var bmp = new Bitmap(4, 3)) bmp.Save(source, System.Drawing.Imaging.ImageFormat.Png);
        var picture = PanelImages.Import(source);
        Assert.NotNull(picture);
        Assert.NotNull(PanelImages.Get(picture));

        var layout = PanelPresets.Build("gauges", Snap());
        layout.Style.BackgroundImage = picture;
        layout.Monitor = @"\\.\DISPLAY3";
        layout.Position = new Point(10, 10);
        layout.Tiles.Add(new PanelTile { Kind = PanelTileKind.Image, Image = picture, Column = 0, Row = layout.Rows });
        layout.Tiles.Add(new PanelTile { Sensor = SensorKey.For(Fan, Fan.Sensors[0]), Column = 1, Row = layout.Rows });
        layout.Rows++;
        var file = Path.Combine(_dir, "mine.pcvpanel.json");
        PanelExchange.Export(layout, file);

        var back = PanelExchange.Import(file);
        Assert.NotEqual(layout.Id, back.Id);
        Assert.Null(back.Monitor);       // this PC's monitors mean nothing elsewhere
        Assert.Null(back.Position);
        Assert.NotNull(back.Style.BackgroundImage);
        Assert.NotEqual(picture, back.Style.BackgroundImage); // its own copy
        Assert.Equal(back.Style.BackgroundImage, back.Tiles.Single(t => t.Kind == PanelTileKind.Image).Image);
        Assert.NotNull(PanelImages.Get(back.Style.BackgroundImage));
        Assert.Equal("/lpc/nct6799d/0/fan/1", back.Tiles.Single(t => t.Sensor != null).Sensor!.Id);
        Assert.Equal(layout.Tiles.Count, back.Tiles.Count);
        Assert.Empty(PanelGrid.Problems(back));
    }

    [Fact]
    public void Importing_something_else_says_why()
    {
        var file = Path.Combine(_dir, "nope.json");
        File.WriteAllText(file, """{ "Hello": "world" }""");
        Assert.Throws<InvalidDataException>(() => PanelExchange.Import(file));
        File.WriteAllText(file, "not json at all");
        Assert.Throws<InvalidDataException>(() => PanelExchange.Import(file));
    }

    [Fact]
    public void Pictures_no_panel_uses_are_deleted()
    {
        var source = Path.Combine(_dir, "a.png");
        using (var bmp = new Bitmap(2, 2)) bmp.Save(source, System.Drawing.Imaging.ImageFormat.Png);
        var kept = PanelImages.Import(source)!;
        var dropped = PanelImages.Import(source)!;
        Assert.Null(PanelImages.Import(Path.Combine(_dir, "missing.txt")));
        PanelImages.Prune(new[] { new PanelLayout { Style = { BackgroundImage = kept } } });
        Assert.True(File.Exists(Path.Combine(PanelImages.Dir, kept)));
        Assert.False(File.Exists(Path.Combine(PanelImages.Dir, dropped)));
    }

    [Fact]
    public void Every_visual_style_draws()
    {
        var snap = Snap();
        var source = Path.Combine(_dir, "b.png");
        using (var bmp = new Bitmap(8, 8)) bmp.Save(source, System.Drawing.Imaging.ImageFormat.Png);
        var picture = PanelImages.Import(source);
        var l = new PanelLayout { FreePlacement = true, Width = 600, Height = 400, Style = { BackgroundImage = picture, BackgroundFit = PanelImageFit.Tile } };
        int x = 0;
        foreach (var gauge in Enum.GetValues<PanelGaugeStyle>())
            l.Tiles.Add(new PanelTile { Source = "cputemp", Visuals = PanelVisual.Label | PanelVisual.Number | PanelVisual.Ring, GaugeStyle = gauge, X = (x++) * 100, Y = 0, Width = 100, Height = 120 });
        foreach (var bar in Enum.GetValues<PanelBarStyle>())
            foreach (var dir in Enum.GetValues<PanelBarDirection>())
                l.Tiles.Add(new PanelTile { Source = "cpu", Visuals = PanelVisual.Number | PanelVisual.Bar, BarStyle = bar, BarDirection = dir, X = (x++ % 6) * 100, Y = 130, Width = 100, Height = 80 });
        l.Tiles.Add(new PanelTile { Kind = PanelTileKind.Text, Text = "GPU {gputemp}", X = 0, Y = 220, Width = 200, Height = 50, FontFamily = "Consolas" });
        l.Tiles.Add(new PanelTile { Kind = PanelTileKind.Image, Image = picture, ImageFit = PanelImageFit.Fill, X = 200, Y = 220, Width = 80, Height = 80 });
        l.Tiles.Add(new PanelTile { Kind = PanelTileKind.Image, Image = "gone.png", X = 300, Y = 220, Width = 80, Height = 80 });
        l.Tiles.Add(new PanelTile { Kind = PanelTileKind.Divider, X = 0, Y = 320, Width = 600, Height = 16 });
        Assert.Empty(PanelGrid.Problems(l));
        using var image = PanelRenderer.Render(l, snap, 1.25f, null);
        Assert.Equal(new Size(750, 500), image.Size);

        // Live text changes the frame; plain text doesn't.
        using var renderer = new PanelRenderer(l, null);
        var now = DateTime.Now;
        var hotter = snap with { Summary = new Summary { CpuName = "x", CpuLoad = 12.4f, CpuTemp = 70.2f, RamTotalGb = 32, Gpus = snap.Summary.Gpus,
                                                          Gpu = new GpuSummary { Id = "/gpu-nvidia/0", Name = "RTX", ShortName = "RTX", Temp = 80 } } };
        Assert.NotEqual(renderer.Prepare(snap, now).Signature, renderer.Prepare(hotter, now).Signature);
    }

    [Fact]
    public void Editor_edits_a_copy_and_undoes()
    {
        var settings = AppSettings.CreateDefault();
        var layout = PanelPresets.Build("compact", Snap());
        int tiles = layout.Tiles.Count;
        PanelLayout? saved = null;
        using var editor = new PanelEditorForm(settings, layout, Snap, null, l => saved = l) { ShowInTaskbar = false, Opacity = 0 };
        _ = editor.Handle;

        editor.AddTile(new PanelTile { Kind = PanelTileKind.Clock, Text = "HH:mm" }, null);
        Assert.Equal(tiles + 1, editor.Working.Tiles.Count);
        Assert.Equal(tiles, layout.Tiles.Count); // the original isn't touched until saved
        Assert.Equal(tiles, editor.Selected);
        Assert.Null(editor.Working.Preset);

        editor.CanvasKey(new KeyEventArgs(Keys.Delete));
        Assert.Equal(tiles, editor.Working.Tiles.Count);
        editor.CanvasKey(new KeyEventArgs(Keys.Control | Keys.Z)); // canvas keys don't undo
        Assert.Equal(tiles, editor.Working.Tiles.Count);

        editor.Duplicate();
        Assert.Equal(tiles + 1, editor.Working.Tiles.Count);
        Assert.Empty(PanelGrid.Problems(editor.Working));
        Assert.Null(saved);
    }
}
