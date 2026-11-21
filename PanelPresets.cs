namespace PCVitals;

/// <summary>
/// The ready-made panels. Each is a list of tiles in reading order; tiles for devices this PC doesn't have
/// (a second GPU, a battery) or that read nothing here (no fan sensor) are dropped and the rest packed onto the grid.
/// </summary>
static class PanelPresets
{
    public static readonly (string Id, string Name, string Description)[] All =
    {
        ("compact", "Compact", "CPU, GPU and memory as numbers"),
        ("gaming", "Gaming", "Usage, temperature and power with bars"),
        ("graphs", "Graphs", "A history graph per reading"),
        ("gauges", "Gauges", "Ring gauges for usage and temperature"),
        ("sidebar", "Sidebar", "A tall strip for the edge of the screen"),
        ("full", "Full system", "Everything, sized for a case screen"),
        ("case-glance", "Case screen: glance", "Big numbers, for a tall screen"),
        ("case-dashboard", "Case screen: dashboard", "Clock, gauges and graphs"),
        ("case-full", "Case screen: everything", "Every reading, tall screen"),
    };

    /// <summary>Readings many PCs don't have; left out of a preset when they read nothing (others show "--").</summary>
    static readonly HashSet<string> Optional = new() { "cpufan", "gpufan", "cpuclock", "gpuclock", "chipset", "vrm", "gpuhotspot", "ping" };

    const PanelVisual Label = PanelVisual.Label, Number = PanelVisual.Number, Bar = PanelVisual.Bar,
                      Ring = PanelVisual.Ring, Graph = PanelVisual.Graph;

    static PanelTile V(string source, PanelVisual visuals, int columns = 1, int rows = 1, string? label = null) => new()
    {
        Kind = PanelTileKind.Value, Source = source, Visuals = visuals, ColumnSpan = columns, RowSpan = rows, Label = label,
    };

    static PanelTile Clock(string format, int columns = 1, int fontSize = 100, int rows = 1) => new()
    {
        Kind = PanelTileKind.Clock, Text = format, ColumnSpan = columns, RowSpan = rows, FontSize = fontSize, Visuals = PanelVisual.Number,
    };

    /// <summary>The panel to show (one for now), made from the Compact preset the first time.</summary>
    public static PanelLayout EnsurePanel(AppSettings settings, Snapshot? snap)
    {
        if (settings.Panels.Count == 0) settings.Panels.Add(Build("compact", snap));
        return settings.Panels[0];
    }

    /// <summary>A preset, fitted to the devices in <paramref name="snap"/> (all tiles when it is null).</summary>
    public static PanelLayout Build(string id, Snapshot? snap)
    {
        var layout = Define(id);
        layout.Preset = id;
        layout.Name = All.FirstOrDefault(p => p.Id == id).Name ?? id;
        if (snap != null)
            layout.Tiles.RemoveAll(t => t.Kind == PanelTileKind.Value &&
                                        (PanelSources.Read(t, snap, null) is not { } r || (r.Value == null && Optional.Contains(t.Source!))));
        PanelGrid.Pack(layout);
        if (id.StartsWith("case-", StringComparison.Ordinal)) FitPortrait(layout);
        return layout;
    }

    /// <summary>The portrait case screen (Lancool 207 Digital) is 720 × 1600 pixels (the presets are drawn at half that, then scaled up).</summary>
    const double PortraitAspect = 720.0 / 1600.0;

    /// <summary>
    /// Sets the row height so the whole panel has the case screen's 9:20 shape however many rows are left after
    /// dropping readings this PC lacks, so "Fill the display" has no bands at the sides or the bottom.
    /// </summary>
    static void FitPortrait(PanelLayout l)
    {
        int width = l.Padding * 2 + l.Columns * l.CellWidth + (l.Columns - 1) * l.Gap;
        double height = width / PortraitAspect;
        l.CellHeight = Math.Max(12, (int)((height - l.Padding * 2 - (l.Rows - 1) * l.Gap) / l.Rows));
    }

    /// <summary>The look shared by the case-screen presets: a soft radial glow, see-through cards and a glow on the gauges.</summary>
    static PanelStyle CaseStyle() => new()
    {
        BackgroundOpacity = 100, Gradient = PanelGradient.Radial, TileOpacity = 55, CornerRadius = 10,
        Glow = true, GradientFills = true, Motion = PanelMotion.Smooth,
    };

    static PanelLayout Define(string id)
    {
        switch (id)
        {
            case "gaming":
                return new PanelLayout
                {
                    Columns = 2, CellWidth = 150, CellHeight = 54,
                    Tiles =
                    {
                        V("cpu", Label | Number | Bar), V("gpu", Label | Number | Bar),
                        V("cputemp", Label | Number | Bar), V("gputemp", Label | Number | Bar),
                        V("cpupower", Label | Number | Bar), V("gpupower", Label | Number | Bar),
                        V("ram", Label | Number | Bar), V("gpuvram", Label | Number | Bar),
                    },
                };

            case "graphs":
                return new PanelLayout
                {
                    Columns = 1, CellWidth = 250, CellHeight = 66,
                    Tiles =
                    {
                        V("cpu", Label | Number | Graph), V("gpu", Label | Number | Graph), V("ram", Label | Number | Graph),
                        V("cputemp", Label | Number | Graph), V("gputemp", Label | Number | Graph), V("netdown", Label | Number | Graph),
                    },
                };

            case "gauges":
                return new PanelLayout
                {
                    Columns = 3, CellWidth = 100, CellHeight = 112,
                    Tiles =
                    {
                        V("cpu", Label | Number | Ring), V("gpu", Label | Number | Ring), V("ram", Label | Number | Ring),
                        V("cputemp", Label | Number | Ring), V("gputemp", Label | Number | Ring), V("gpuvram", Label | Number | Ring),
                    },
                };

            case "sidebar":
                return new PanelLayout
                {
                    Columns = 1, CellWidth = 132, CellHeight = 50, Gap = 4,
                    Tiles =
                    {
                        Clock("HH:mm", fontSize: 120),
                        V("cpu", Label | Number | Bar), V("cputemp", Label | Number | Bar),
                        V("gpu", Label | Number | Bar), V("gputemp", Label | Number | Bar),
                        V("ram", Label | Number | Bar), V("power", Label | Number),
                        V("netdown", Label | Number), V("netup", Label | Number),
                    },
                };

            case "full":
            {
                var full = new PanelLayout
                {
                    Columns = 4, CellWidth = 140, CellHeight = 70,
                    Tiles =
                    {
                        Clock("HH:mm", columns: 2, fontSize: 130), Clock("dddd d MMMM", columns: 2, fontSize: 70),
                        V("cpu", Label | Number | Graph, columns: 2), V("gpu", Label | Number | Graph, columns: 2),
                        V("cputemp", Label | Number | Ring, rows: 2), V("cpupower", Label | Number | Bar),
                        V("gputemp", Label | Number | Ring, rows: 2), V("gpupower", Label | Number | Bar),
                        V("cpuclock", Label | Number), V("gpuclock", Label | Number),
                        V("ram", Label | Number | Bar, columns: 2), V("gpuvram", Label | Number | Bar, columns: 2),
                        V("netdown", Label | Number | Graph), V("netup", Label | Number | Graph),
                        V("cpufan", Label | Number), V("gpufan", Label | Number),
                        V("disk", Label | Number | Bar), V("chipset", Label | Number | Bar), V("power", Label | Number), V("battery", Label | Number | Bar),
                    },
                };
                return full;
            }

            case "case-glance":
                // Designed at half size (360 × 800, drawn twice as big by "Fill the display" on 720 × 1600) so labels
                // and lines are legible on the 6-inch screen: 14 rows of 48 px in two 164 px columns.
                return new PanelLayout
                {
                    Columns = 2, CellWidth = 164, CellHeight = 48, Gap = 8, Padding = 12, Style = CaseStyle(),
                    Tiles =
                    {
                        Clock("HH:mm", columns: 2, rows: 3, fontSize: 200), Clock("dddd d MMMM", columns: 2, fontSize: 90),
                        V("cpu", Label | Number | Ring, rows: 3), V("gpu", Label | Number | Ring, rows: 3),
                        V("cputemp", Label | Number | Ring, rows: 3), V("gputemp", Label | Number | Ring, rows: 3),
                        V("ram", Label | Number | Bar, columns: 2, rows: 2), V("power", Label | Number, rows: 2), V("netdown", Label | Number, rows: 2),
                    },
                };

            case "case-dashboard":
                // Half size, 360 × 800: 16 rows of 41 px in two 164 px columns.
                return new PanelLayout
                {
                    Columns = 2, CellWidth = 164, CellHeight = 41, Gap = 8, Padding = 12, Style = CaseStyle(),
                    Tiles =
                    {
                        Clock("HH:mm", columns: 2, rows: 2, fontSize: 200), Clock("dddd d MMMM", columns: 2, fontSize: 90),
                        V("cpu", Label | Number | Ring, rows: 3), V("gpu", Label | Number | Ring, rows: 3),
                        V("cputemp", Label | Number | Ring, rows: 3), V("gputemp", Label | Number | Ring, rows: 3),
                        V("cpu", Label | Number | Graph, columns: 2, rows: 2), V("gpu", Label | Number | Graph, columns: 2, rows: 2),
                        V("ram", Label | Number | Bar), V("gpuvram", Label | Number | Bar),
                        V("power", Label | Number | Bar), V("gpupower", Label | Number | Bar),
                        V("netdown", Label | Number), V("netup", Label | Number),
                    },
                };

            case "case-full":
                // Half size, 360 × 800: 17 rows of 40 px in three 108 px columns.
                return new PanelLayout
                {
                    Columns = 3, CellWidth = 108, CellHeight = 40, Gap = 6, Padding = 12, Style = CaseStyle(),
                    Tiles =
                    {
                        Clock("HH:mm", columns: 2, rows: 2, fontSize: 200), Clock("ddd d MMM", rows: 2, fontSize: 100),
                        V("cpu", Label | Number | Ring, rows: 3), V("gpu", Label | Number | Ring, rows: 3), V("ram", Label | Number | Ring, rows: 3),
                        V("cputemp", Label | Number | Ring, rows: 3), V("gputemp", Label | Number | Ring, rows: 3), V("gpuvram", Label | Number | Ring, rows: 3),
                        V("cpu", Label | Number | Graph, columns: 3, rows: 2), V("gpu", Label | Number | Graph, columns: 3, rows: 2),
                        V("cpupower", Label | Number), V("gpupower", Label | Number), V("power", Label | Number),
                        V("cpuclock", Label | Number), V("gpuclock", Label | Number), V("ramused", Label | Number),
                        V("cpufan", Label | Number), V("gpufan", Label | Number), V("battery", Label | Number),
                        V("netdown", Label | Number | Graph), V("netup", Label | Number | Graph), V("ping", Label | Number),
                        V("chipset", Label | Number), V("vrm", Label | Number), V("disk", Label | Number),
                    },
                };

            default: // compact
                return new PanelLayout
                {
                    Columns = 2, CellWidth = 112, CellHeight = 58,
                    Tiles =
                    {
                        V("cpu", Label | Number), V("cputemp", Label | Number),
                        V("gpu", Label | Number), V("gputemp", Label | Number),
                        V("ram", Label | Number), V("netdown", Label | Number),
                    },
                };
        }
    }
}
