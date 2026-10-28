using LibreHardwareMonitor.Hardware;
using Xunit;

namespace PCVitals.Tests;

public sealed class PanelTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcvitals-tests-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "settings.json");

    public PanelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static GpuSummary Gpu(string id, bool integrated = false) => new()
    {
        Id = id, Name = integrated ? "AMD Radeon(TM) Graphics" : "NVIDIA GeForce RTX 5080", ShortName = integrated ? "Radeon" : "RTX 5080",
        Integrated = integrated, Load = 30, Temp = 55, Power = 120, PowerLimitPct = 40, CoreClock = 2500, VramUsedMb = 4096, VramTotalMb = 16384, FanRpm = 1200,
    };

    /// <summary>A desktop with two GPUs, drives and fans, or a bare one with no GPU at all.</summary>
    static Snapshot Snap(bool full, bool battery = false)
    {
        var gpus = full ? new[] { Gpu("/gpu-nvidia/0"), Gpu("/gpu-amd/0", integrated: true) } : Array.Empty<GpuSummary>();
        var summary = new Summary
        {
            CpuName = "AMD Ryzen 7 9800X3D", CpuLoad = 12, CpuTemp = 70, CpuPower = 60, CpuClock = full ? 4800 : null, CpuFanRpm = full ? 900 : null,
            RamUsedGb = 12, RamTotalGb = 32, RamLoad = 37, NetDown = 2048, NetUp = 512,
            ChipsetTemp = full ? 45 : null,
            Gpus = gpus, Gpu = gpus.FirstOrDefault(), SecondGpu = gpus.Skip(1).FirstOrDefault(),
            Disks = full ? new[] { new DiskSummary { Id = "/nvme/0", Name = "Samsung SSD 990 PRO 2TB", Temp = 41 } } : Array.Empty<DiskSummary>(),
            Battery = battery ? new BatterySummary { Name = "Battery", Charge = 80 } : null,
        };
        return new Snapshot(DateTime.Now, Array.Empty<HardwareReading>(), summary, new Dictionary<string, float[]>(), Array.Empty<HardwareReading>());
    }

    [Fact]
    public void Cells_map_to_pixels_with_even_gaps_at_any_scale()
    {
        var l = new PanelLayout { Columns = 3, Rows = 2, CellWidth = 100, CellHeight = 50, Gap = 6, Padding = 8 };
        Assert.Equal(new Size(8 * 2 + 300 + 12, 8 * 2 + 100 + 6), PanelGrid.PanelSize(l, 1));
        Assert.Equal(new Rectangle(8, 8, 100, 50), PanelGrid.CellRect(l, new Rectangle(0, 0, 1, 1), 1));
        // A 2×2 block spans the gap between its cells.
        Assert.Equal(Rectangle.FromLTRB(114, 8, 320, 114), PanelGrid.CellRect(l, new Rectangle(1, 0, 2, 2), 1));

        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        {
            var a = PanelGrid.CellRect(l, new Rectangle(0, 0, 1, 1), scale);
            var b = PanelGrid.CellRect(l, new Rectangle(1, 0, 1, 1), scale);
            var c = PanelGrid.CellRect(l, new Rectangle(2, 0, 1, 1), scale);
            Assert.InRange(b.Left - a.Right, (int)(6 * scale) - 1, (int)(6 * scale) + 1);
            Assert.InRange(c.Left - b.Right, (int)(6 * scale) - 1, (int)(6 * scale) + 1);
            // The last cell ends exactly a padding away from the panel's edge.
            Assert.Equal(PanelGrid.PanelSize(l, scale).Width - (int)Math.Round(8 * scale), c.Right, 1f);
        }
    }

    [Fact]
    public void Pack_fills_left_to_right_and_counts_rows()
    {
        var l = new PanelLayout { Columns = 2 };
        l.Tiles.AddRange(new[]
        {
            new PanelTile { ColumnSpan = 2 }, new PanelTile(), new PanelTile { RowSpan = 2 }, new PanelTile(),
        });
        PanelGrid.Pack(l);
        Assert.Equal(new[] { (0, 0), (0, 1), (1, 1), (0, 2) }, l.Tiles.Select(t => (t.Column, t.Row)));
        Assert.Equal(3, l.Rows);
        Assert.Empty(PanelGrid.Problems(l));
    }

    [Fact]
    public void Problems_finds_overlaps_and_tiles_off_the_grid()
    {
        var l = new PanelLayout { Columns = 2, Rows = 1 };
        l.Tiles.Add(new PanelTile { Source = "cpu" });
        l.Tiles.Add(new PanelTile { Source = "gpu" });
        l.Tiles.Add(new PanelTile { Source = "ram", Column = 1, Row = 0, RowSpan = 2 });
        var problems = PanelGrid.Problems(l);
        Assert.Contains(problems, p => p.Contains("tile 1 (gpu) overlaps tile 0 (cpu)"));
        Assert.Contains(problems, p => p.Contains("tile 2 (ram) is outside"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Every_preset_fits_its_grid_on_any_pc(bool full, bool battery)
    {
        var snap = Snap(full, battery);
        foreach (var (id, _, _) in PanelPresets.All)
        {
            var l = PanelPresets.Build(id, snap);
            Assert.Empty(PanelGrid.Problems(l));
            Assert.NotEmpty(l.Tiles);
            Assert.Equal(id, l.Preset);
            // Nothing that this PC can't read.
            Assert.All(l.Tiles.Where(t => t.Kind == PanelTileKind.Value), t => Assert.NotNull(PanelSources.Read(t, snap, null)));
        }
    }

    [Fact]
    public void Presets_leave_out_missing_devices_and_empty_optional_readings()
    {
        var bare = PanelPresets.Build("full", Snap(full: false));
        var sources = bare.Tiles.Select(t => t.Source).ToList();
        Assert.DoesNotContain("gpu", sources);
        Assert.DoesNotContain("cpufan", sources);
        Assert.DoesNotContain("battery", sources);
        Assert.Contains("cpu", sources);
        // Without a snapshot (e.g. before the first reading) every tile is kept.
        Assert.Contains("gpu", PanelPresets.Build("full", null).Tiles.Select(t => t.Source));
        Assert.Contains("battery", PanelPresets.Build("full", Snap(full: true, battery: true)).Tiles.Select(t => t.Source));
    }

    [Fact]
    public void Thresholds_pick_the_highest_one_reached()
    {
        var steps = PanelColor.TemperatureDefaults();
        Assert.Null(PanelColor.ForValue(steps, 50));
        Assert.Equal(Theme.Warn, PanelColor.ForValue(steps, 65));
        Assert.Equal(Theme.Hot, PanelColor.ForValue(steps, 90));
        Assert.Null(PanelColor.ForValue(steps, null));
        var custom = new List<PanelThreshold> { new() { Value = 80, Color = "#FF0000" }, new() { Value = 20, Color = "#00FF00" } };
        Assert.Equal(Color.FromArgb(255, 255, 0, 0), PanelColor.ForValue(custom, 85));
        Assert.Equal(Color.FromArgb(255, 0, 255, 0), PanelColor.ForValue(custom, 50));
    }

    [Fact]
    public void Colours_parse_and_print()
    {
        Assert.Equal(Color.FromArgb(255, 0x12, 0x34, 0x56), PanelColor.Parse("#123456"));
        Assert.Equal(Color.FromArgb(0x80, 0x12, 0x34, 0x56), PanelColor.Parse("80123456"));
        Assert.Null(PanelColor.Parse("blue"));
        Assert.Null(PanelColor.Parse(null));
        Assert.Equal("#123456", PanelColor.ToHex(Color.FromArgb(0x12, 0x34, 0x56)));
    }

    [Fact]
    public void Readings_format_with_a_separate_unit()
    {
        Fmt.Fahrenheit = false;
        Assert.Equal(("70", "°C"), PanelSources.Format(70.4f, PanelUnit.Temp, null));
        Assert.Equal(("4.80", "GHz"), PanelSources.Format(4800, PanelUnit.Clock, null));
        Assert.Equal(("2.0", "KB/s"), PanelSources.Format(2048, PanelUnit.Rate, null));
        Assert.Equal(("12.5", "%"), PanelSources.Format(12.46f, PanelUnit.Percent, 1));
        Assert.Equal(("--", "W"), PanelSources.Format(null, PanelUnit.Watts, null));
    }

    [Fact]
    public void Gpu_power_scale_comes_from_the_power_limit()
    {
        var r = PanelSources.Read(new PanelTile { Source = "gpupower" }, Snap(full: true), null)!;
        Assert.Equal(300, r.Max!.Value, 1f); // 120 W is 40 % of the limit
        Assert.Equal("gpu:/gpu-nvidia/0", r.Page);
        var vram = PanelSources.Read(new PanelTile { Source = "gpuvram" }, Snap(full: true), null)!;
        Assert.Equal(25, vram.Value!.Value, 1f);
        Assert.Equal("4.0 GB", vram.Text);
    }

    [Fact]
    public void Panels_survive_a_save_and_load()
    {
        var s = AppSettings.CreateDefault();
        var panel = PanelPresets.EnsurePanel(s, Snap(full: true));
        panel.Enabled = true;
        panel.Locked = true;
        panel.Mode = PanelMode.AlwaysOnTop;
        panel.Position = new Point(40, 60);
        panel.Monitor = @"\\.\DISPLAY2";
        panel.MonitorSize = new Size(2560, 1440);
        panel.Style.ValueColor = "#FFAA00";
        panel.Tiles[0].Visuals = PanelVisual.Label | PanelVisual.Number | PanelVisual.Graph;
        panel.Tiles[0].Thresholds = new() { new() { Value = 90, Color = "hot" } };
        var gpu = new HardwareReading("/gpu-nvidia/0", "NVIDIA GeForce RTX 5080", HardwareType.GpuNvidia,
                                      new[] { new SensorReading("/gpu-nvidia/0/fan/1", "GPU Fan 1", SensorType.Fan, 1234f, 0f, 2100f) });
        panel.Tiles.Add(new PanelTile
        {
            Sensor = SensorKey.For(gpu, gpu.Sensors[0]), Visuals = PanelVisual.Number | PanelVisual.Bar, BarDirection = PanelBarDirection.Vertical,
            Row = panel.Rows,
        });
        panel.Rows++;
        s.SaveTo(FilePath);

        var back = AppSettings.LoadFrom(FilePath).Panels.Single();
        Assert.True(back.Enabled);
        Assert.True(back.Locked);
        Assert.Equal(PanelMode.AlwaysOnTop, back.Mode);
        Assert.Equal(new Point(40, 60), back.Position);
        Assert.Equal(@"\\.\DISPLAY2", back.Monitor);
        Assert.Equal(new Size(2560, 1440), back.MonitorSize);
        Assert.Equal("#FFAA00", back.Style.ValueColor);
        Assert.Equal("compact", back.Preset);
        Assert.Equal(PanelVisual.Label | PanelVisual.Number | PanelVisual.Graph, back.Tiles[0].Visuals);
        Assert.Equal(90, back.Tiles[0].Thresholds!.Single().Value);
        Assert.Equal("/gpu-nvidia/0/fan/1", back.Tiles[^1].Sensor!.Id);
        Assert.Equal(PanelBarDirection.Vertical, back.Tiles[^1].BarDirection);
        Assert.Empty(PanelGrid.Problems(back));
    }

    [Fact]
    public void Panel_sensors_get_history()
    {
        var s = AppSettings.CreateDefault();
        var key = new SensorKey("/lpc/nct6799d/0/fan/1", HardwareType.SuperIO, "Nuvoton NCT6799D", SensorType.Fan, "Fan #2");
        var panel = PanelPresets.EnsurePanel(s, null);
        panel.Tiles.Add(new PanelTile { Sensor = key });
        Assert.DoesNotContain(s.TrackedSensors(null), k => k.Id == key.Id); // the panel is off
        panel.Enabled = true;
        Assert.Contains(s.TrackedSensors(null), k => k.Id == key.Id);
        Assert.True(PanelSources.Needs(panel.Tiles[^1]).Board);
    }

    [Fact]
    public void Version_4_settings_migrate_without_a_panel()
    {
        File.WriteAllText(FilePath, """{ "Version": 4, "IntervalMs": 2000 }""");
        var s = AppSettings.LoadFrom(FilePath);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.Empty(s.Panels);
        Assert.Equal(2000, s.IntervalMs);
        Assert.Contains("\"Panels\": []", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Broken_panel_entries_are_repaired()
    {
        File.WriteAllText(FilePath, """{ "Version": 5, "Panels": [ null, { "Enabled": true, "Style": null, "Tiles": [ null, { "Source": "cpu" } ] } ] }""");
        var panel = AppSettings.LoadFrom(FilePath).Panels.Single();
        Assert.NotNull(panel.Style);
        Assert.Equal("cpu", panel.Tiles.Single().Source);
    }

    [Theory]
    [InlineData(false, PanelMode.Desktop)]
    [InlineData(true, PanelMode.AlwaysOnTop)]
    public void Panel_window_can_be_created_and_drawn(bool locked, PanelMode mode)
    {
        // Form's constructor reads CreateParams before the panel's own constructor has run.
        var layout = PanelPresets.Build("gauges", Snap(full: true));
        layout.Enabled = true;
        layout.Locked = locked;
        layout.Mode = mode;
        using var panel = new DesktopPanel(AppSettings.CreateDefault(), layout, null);
        _ = panel.Handle;
        panel.SetSnapshot(Snap(full: true));
        panel.AllowClose = true;
    }

    [Fact]
    public void Renderer_draws_every_preset_and_skips_unchanged_frames()
    {
        var snap = Snap(full: true);
        foreach (var (id, _, _) in PanelPresets.All)
        {
            var layout = PanelPresets.Build(id, snap);
            using var bmp = PanelRenderer.Render(layout, snap, 1.5f, null);
            Assert.Equal(PanelGrid.PanelSize(layout, 1.5f), bmp.Size);
        }
        using var renderer = new PanelRenderer(PanelPresets.Build("compact", snap), null);
        var now = new DateTime(2026, 10, 28, 12, 0, 0);
        Assert.Equal(renderer.Prepare(snap, now).Signature, renderer.Prepare(snap, now).Signature);
        var hotter = snap with { Summary = new Summary { CpuTemp = 90, CpuName = "x", RamTotalGb = 32, Gpus = snap.Summary.Gpus, Gpu = snap.Summary.Gpu } };
        Assert.NotEqual(renderer.Prepare(snap, now).Signature, renderer.Prepare(hotter, now).Signature);
    }
}
