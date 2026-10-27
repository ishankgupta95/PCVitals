using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCVitals;

/// <summary>What the settings window needs from the running app.</summary>
interface ISettingsHost
{
    AppSettings Settings { get; }
    bool IsAdmin { get; }
    Snapshot? Latest { get; }
    string? WidgetFitNote { get; }
    bool GpuPowerEstimated { get; }
    IReadOnlyDictionary<string, float> PowerRatios { get; }
    /// <summary>Settings were changed: save, apply, redraw.</summary>
    void SettingsChanged();
    void RecalibrateGpuPower();
    /// <summary>Writes the one-minute history as CSV; returns the number of rows.</summary>
    int ExportHistory(string path);
    /// <summary>Saved history, when it is switched on.</summary>
    HistoryArchive? Archive { get; }
    long HistorySizeBytes { get; }
    Task DeleteHistoryAsync();
    AlertLog AlertLog { get; }
    /// <summary>When a rule last went off since PC Vitals started.</summary>
    DateTime? LastFired(string ruleId);
    /// <summary>Carries out a rule's actions now, as if it had gone off.</summary>
    void TestAlert(AlertRule rule);
    /// <summary>Rebuilds the settings window (after a reset or theme change) on the given page.</summary>
    void ReopenSettings(string page);
}

/// <summary>
/// Settings window: sidebar navigation, a page of cards per
/// topic, toggle switches, and live previews. Every change applies immediately.
/// </summary>
sealed class SettingsForm : Form
{
    readonly ISettingsHost _host;
    readonly AppSettings _s;
    readonly NavList _nav = new();
    readonly Panel _content = new() { Dock = DockStyle.Fill };
    readonly Dictionary<string, FlowLayoutPanel> _pages = new();
    readonly List<Control> _live = new();
    Label? _usage;
    FlowLayoutPanel? _itemsHost;
    CardDragger? _itemDragger;

    public string CurrentPage => _nav.Selected;

    public SettingsForm(ISettingsHost host, string? page = null)
    {
        _host = host;
        _s = host.Settings;

        // WinForms applies DPI auto-scaling when layout resumes, so build inside Suspend/Resume
        // (the pattern designer-generated code uses); otherwise the window stays at 96 DPI size.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "PC Vitals settings";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 720);
        MinimumSize = new Size(800, 520);
        MaximizeBox = true;
        MinimizeBox = true;
        Icon = TrayIconRenderer.AppIcon; // otherwise the taskbar shows the generic window icon

        _nav.Dock = DockStyle.Left;
        _nav.Width = 240;
        _content.BackColor = Theme.Bg;
        Controls.Add(_content);
        Controls.Add(_nav);

        AddPage("taskbar", "Taskbar", "", BuildTaskbar);
        AddPage("tray", "Tray icons", "", BuildTray);
        AddPage("graphics", "Graphics", "", BuildGraphics);
        AddPage("alerts", "Alerts", "", BuildAlerts);
        AddPage("appearance", "Appearance", "", BuildAppearance);
        AddPage("general", "General", "", BuildGeneral);
        AddPage("about", "About", "", BuildAbout);

        _nav.SelectionChanged += ShowPageInternal;
        _nav.Select(page != null && _pages.ContainsKey(page) ? page : "taskbar");
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
        foreach (var p in _pages.Values) ApplyScrollbarTheme(p);
    }

    /// <summary>
    /// Switches the open window to the current palette in place. Owner-drawn controls read the
    /// theme when they paint; the few standard controls and the title bar are recoloured here.
    /// </summary>
    public void ApplyTheme()
    {
        SuspendLayout();
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Recolor(this);
        ResumeLayout(false);
        if (IsHandleCreated)
        {
            Native.ApplyRoundedDarkFrame(Handle);
            Native.RefreshFrame(Handle);
            foreach (var p in _pages.Values) ApplyScrollbarTheme(p);
        }
        Invalidate(invalidateChildren: true);
    }

    static void Recolor(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case NavList:
                    c.BackColor = Theme.Rail;
                    break;
                case Card or AccessoryRow:
                    c.BackColor = Theme.Surface;
                    break;
                case TextBox box:
                    box.BackColor = Theme.SurfaceHover;
                    box.ForeColor = Theme.Text;
                    break;
                case Label label:
                    label.ForeColor = label.Tag as string == "dim" ? Theme.TextDim : Theme.Text;
                    break;
                case Panel: // pages, the item list, the theme swatch row
                    c.BackColor = Theme.Bg;
                    break;
            }
            Recolor(c);
        }
    }

    /// <summary>Refreshes previews and live values with a new sensor reading.</summary>
    public void SetSnapshot(Snapshot snap)
    {
        foreach (var c in _live) if (c.Visible) c.Invalidate();
        if (_usage is { Visible: true }) _usage.Text = SelfUsage();
    }

    // ---- Page plumbing ----------------------------------------------------------------------

    void AddPage(string key, string title, string glyph, Action<FlowLayoutPanel> build)
    {
        _nav.Add(key, title, glyph);
        var page = new SettingsPage
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(36, 26, 36, 36),
            BackColor = Theme.Bg,
            Visible = false,
        };
        page.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20f),
            ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 0, 18),
        });
        build(page);
        page.Resize += (_, _) => Stretch(page);
        _pages[key] = page;
        _content.Controls.Add(page);
    }

    /// <summary>
    /// For the layout check in --preview: shows each page scrolled to its end and reports whether its
    /// last control is fully in view.
    /// </summary>
    internal string ScrollReport()
    {
        var lines = new List<string>();
        foreach (var key in _pages.Keys.ToList())
        {
            _nav.Select(key);
            var page = _pages[key];
            page.PerformLayout();
            Application.DoEvents();
            page.AutoScrollPosition = new Point(0, int.MaxValue);
            Application.DoEvents();
            var last = page.Controls.Cast<Control>().Where(c => c.Visible).MaxBy(c => c.Bottom);
            int gap = page.ClientSize.Height - (last?.Bottom ?? 0);
            lines.Add($"{key}: last control ends {(gap >= 0 ? $"{gap}px above" : $"{-gap}px BELOW")} the bottom of the view");
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal void ShowAtEnd(string key)
    {
        _nav.Select(key);
        _pages[key].AutoScrollPosition = new Point(0, int.MaxValue);
    }

    void ShowPageInternal(string key)
    {
        foreach (var (k, p) in _pages) p.Visible = k == key;
        Stretch(_pages[key]);
    }

    /// <summary>FlowLayoutPanel doesn't stretch children; size full-width cards to the page.</summary>
    static void Stretch(FlowLayoutPanel page)
    {
        int width = page.ClientSize.Width - page.Padding.Horizontal - 2;
        page.SuspendLayout();
        foreach (Control c in page.Controls)
            if (c.Tag as string == "stretch" && c.Width != width) c.Width = Math.Max(300, width);
            else if (c is FlowLayoutPanel { Tag: "stretch-list" } list)
            {
                list.Width = Math.Max(300, width);
                foreach (Control child in list.Controls) child.Width = list.Width;
            }
        page.ResumeLayout();
    }

    static void ApplyScrollbarTheme(Control c)
    {
        try { SetWindowTheme(c.Handle, Theme.IsLight ? "Explorer" : "DarkMode_Explorer", null); }
        catch { }
    }

    void Changed()
    {
        _s.Save();
        _host.SettingsChanged();
        foreach (var c in _live) c.Invalidate();
    }

    // ---- Factories --------------------------------------------------------------------------

    static Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10.5f),
        ForeColor = Theme.Text,
        Margin = new Padding(0, 18, 0, 8),
    };

    static Label Hint(string text, int width = 640) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = Theme.TextDim,
        Tag = "dim",
        Margin = new Padding(0, 0, 0, 10),
    };

    ToggleSwitch Toggle(bool value, Action<bool> onChange)
    {
        var t = new ToggleSwitch { Checked = value };
        t.CheckedChanged += (_, _) =>
        {
            onChange(t.Checked);
            Changed();
        };
        return t;
    }

    Dropdown Combo(string[] items, int selected, int width, Action<int> onChange)
    {
        var c = new Dropdown(items, selected, width);
        c.SelectedIndexChanged += (_, _) =>
        {
            onChange(c.SelectedIndex);
            Changed();
        };
        return c;
    }

    // ---- Taskbar ----------------------------------------------------------------------------

    void BuildTaskbar(FlowLayoutPanel p)
    {
        var preview = new LivePreview(c =>
        {
            if (_host.Latest is not { } snap) return (null, null);
            var views = _s.WidgetItems.Where(i => i.Enabled).Select(i => Vitals.ForItem(i, snap, _s))
                .Where(v => v != null).Select(v => v!).ToList();
            if (views.Count == 0) return (null, "No items are turned on.");
            float scale = c.DeviceDpi / 96f * _s.WidgetScale / 100f;
            int height = (int)Math.Round(48 * c.DeviceDpi / 96f);
            var bmp = TaskbarWidget.RenderStrip(views, height, scale, Theme.TaskbarIsLight(), _s.WidgetGraphs, -1, out _);
            return (bmp, _s.WidgetEnabled ? _host.WidgetFitNote : "The taskbar strip is turned off.");
        }, 120);
        _live.Add(preview);
        p.Controls.Add(preview);

        p.Controls.Add(new Card("Show stats on the taskbar", "Labelled readouts next to the clock. Hover for details, click for the dashboard, drag to move.",
                                Toggle(_s.WidgetEnabled, v => _s.WidgetEnabled = v), ""));

        p.Controls.Add(Section("Items"));
        p.Controls.Add(Hint("Turn items on or off, rename their labels (leave empty for none), and drag them (or use the arrows) to reorder. Items at the end are the first to be hidden if the taskbar runs out of room."));
        _itemsHost = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 4),
            Tag = "stretch-list",
        };
        p.Controls.Add(_itemsHost);
        _itemDragger = new CardDragger(_itemsHost, ItemDropped);
        BuildItemRows();
        var addSensor = new PillButton("Add sensor…", AddSensor) { Margin = new Padding(0, 4, 0, 8), AccessibleName = "Add a sensor to the taskbar" };
        p.Controls.Add(addSensor);
        p.Controls.Add(Hint("Any reading from the dashboard can go on the taskbar too: pick one here, or right-click a sensor in the dashboard."));

        p.Controls.Add(Section("Layout"));
        var sizes = new[] { 85, 100, 115 };
        p.Controls.Add(new Card("Size", "Text and graph size on the taskbar. The strip still shrinks automatically if it would cover the app icons.",
                                Combo(new[] { "Small", "Medium", "Large" }, Math.Max(0, Array.IndexOf(sizes, _s.WidgetScale)), 160,
                                      i => _s.WidgetScale = sizes[i]), ""));
        p.Controls.Add(new Card("Mini history graphs", "A small bar graph of the last minute next to each item.",
                                Toggle(_s.WidgetGraphs, v => _s.WidgetGraphs = v), ""));
        var position = Combo(new[] { "Next to the system tray", "Left side of the taskbar" }, (int)_s.WidgetPosition, 220, i =>
        {
            _s.WidgetPosition = (WidgetPosition)i;
            _s.WidgetOffset = 0;
        });
        var reset = new PillButton("Reset", () =>
        {
            _s.WidgetOffset = 0;
            Changed();
        });
        p.Controls.Add(new Card("Position", "Where the strip sits. You can also drag it sideways on the taskbar.",
                                new AccessoryRow(position, reset), ""));
        p.Controls.Add(new Card("Show on all displays", "Also show the stats on the taskbar of every other monitor (needs \"Show my taskbar on all displays\" in Windows settings).",
                                Toggle(_s.ShowOnAllDisplays, v => _s.ShowOnAllDisplays = v), ""));
        p.Controls.Add(new Card("Keep visible when the taskbar auto-hides", "Shows the stats as a small floating bar at the bottom of the screen while an auto-hiding taskbar is tucked away.",
                                Toggle(_s.FloatWhenTaskbarHidden, v => _s.FloatWhenTaskbarHidden = v), ""));
    }

    static string Describe(WidgetKind k) => k switch
    {
        WidgetKind.Cpu => "Usage, temperature and power",
        WidgetKind.Gpu => "Primary graphics card: usage, temperature, power",
        WidgetKind.SecondGpu => "The other GPU (usually integrated graphics)",
        WidgetKind.Memory => "Usage and amount in use",
        WidgetKind.Network => "Upload and download speed",
        WidgetKind.Power => "CPU + graphics card power draw",
        WidgetKind.Board => "Chipset, VRM and system temperatures",
        WidgetKind.Disk => "Hottest drive, read and write speed",
        WidgetKind.Battery => "Charge, charging or discharging watts, time left",
        _ => "",
    };

    void BuildItemRows()
    {
        var host = _itemsHost!;
        host.SuspendLayout();
        foreach (Control c in host.Controls.Cast<Control>().ToList()) c.Dispose();
        host.Controls.Clear();
        for (int i = 0; i < _s.WidgetItems.Count; i++)
        {
            var item = _s.WidgetItems[i];
            int index = i;
            var label = new InputBox(item.Label, "No label", 110, 8);
            label.TextChanged += (_, _) =>
            {
                item.Label = label.Value.Trim();
                Changed();
            };
            var (name, description) = item.Kind == WidgetKind.Sensor ? DescribeSensor(item) : (Vitals.DisplayName(item.Kind), Describe(item.Kind));
            label.FieldName = $"Label for {name}";
            label.AccessibleName = $"Label for {name}";
            var up = new PillButton("", () => MoveItem(index, -1), 34) { Glyph = "", Enabled = i > 0, AccessibleName = $"Move {name} up" };
            var down = new PillButton("", () => MoveItem(index, +1), 34) { Glyph = "", Enabled = i < _s.WidgetItems.Count - 1, AccessibleName = $"Move {name} down" };
            var toggle = Toggle(item.Enabled, v => item.Enabled = v);
            toggle.AccessibleName = $"Show {name} on the taskbar";
            var controls = new List<Control> { label, up, down };
            if (item.Kind == WidgetKind.Sensor)
                controls.Add(new PillButton("", () => RemoveItem(index), 34) { Glyph = "", AccessibleName = $"Remove {name}" });
            controls.Add(toggle);
            var card = new Card(name, description, new AccessoryRow(controls.ToArray()), glyph: "") { Cursor = Cursors.SizeNS };
            _itemDragger?.Attach(card);
            host.Controls.Add(card);
        }
        host.ResumeLayout();
        if (host.Parent is FlowLayoutPanel page) Stretch(page);
    }

    /// <summary>Saves a drag-and-drop reorder of the taskbar items (the arrow buttons do it from the keyboard).</summary>
    void ItemDropped(int from, int to)
    {
        if (from < 0 || from >= _s.WidgetItems.Count) return;
        var item = _s.WidgetItems[from];
        _s.WidgetItems.RemoveAt(from);
        _s.WidgetItems.Insert(Math.Min(to, _s.WidgetItems.Count), item);
        // Rebuild after the mouse-up finishes; the dropped card is one of the controls being replaced.
        BeginInvoke(() =>
        {
            BuildItemRows();
            Changed();
        });
    }

    (string Name, string Description) DescribeSensor(WidgetItem item)
    {
        var key = item.Sensor!;
        var entry = _host.Latest?.FindSensor(key);
        var name = _s.SensorName(entry?.Id ?? key.Id, key.SensorName);
        var device = HardwareService.ShortName(entry?.Hardware.Name ?? key.HardwareName);
        return (name, entry == null ? $"{device} · not available right now" : $"{device} · {DashboardView.GroupName(key.SensorType).ToLowerInvariant()}");
    }

    void AddSensor()
    {
        var entry = SensorPicker.Pick(this, () => _host.Latest, _s);
        if (entry == null) return;
        _s.PinSensor(entry.Key, SensorFormat.DefaultLabel(_s.SensorName(entry.Id, entry.Sensor.Name)));
        BuildItemRows();
        Changed();
    }

    void RemoveItem(int index)
    {
        if (index < 0 || index >= _s.WidgetItems.Count || _s.WidgetItems[index].Kind != WidgetKind.Sensor) return;
        _s.WidgetItems.RemoveAt(index);
        BeginInvoke(() =>
        {
            BuildItemRows();
            Changed();
        });
    }

    /// <summary>Sensor items were pinned or removed elsewhere (the dashboard).</summary>
    public void SensorItemsChanged()
    {
        if (_itemsHost != null) BuildItemRows();
    }

    void MoveItem(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= _s.WidgetItems.Count) return;
        (_s.WidgetItems[index], _s.WidgetItems[target]) = (_s.WidgetItems[target], _s.WidgetItems[index]);
        // Rebuild after this click finishes; the clicked button is one of the controls being replaced.
        BeginInvoke(() =>
        {
            BuildItemRows();
            Changed();
        });
    }

    // ---- Tray -------------------------------------------------------------------------------

    void BuildTray(FlowLayoutPanel p)
    {
        var preview = new LivePreview(c =>
        {
            var metrics = _s.TrayMetrics.OrderBy(m => (int)m).ToList();
            if (metrics.Count == 0) return (null, "With no metric icons on, a single PC Vitals icon is shown in the tray.");
            int size = (int)Math.Round(32 * c.DeviceDpi / 96f);
            var s = _host.Latest?.Summary;
            var bmp = new Bitmap(metrics.Count * (size + size / 2), size);
            using (var g = Graphics.FromImage(bmp))
            {
                bool light = Theme.TaskbarIsLight();
                for (int i = 0; i < metrics.Count; i++)
                {
                    var (label, value, accent, temp) = metrics[i] switch
                    {
                        TrayMetric.CpuTemp => ("CPU", s?.CpuTemp, Theme.DarkPalette.Cpu, true),
                        TrayMetric.CpuLoad => ("CPU%", s?.CpuLoad, Theme.DarkPalette.Cpu, false),
                        TrayMetric.GpuTemp => ("GPU", s?.Gpu?.Temp, Theme.DarkPalette.Gpu, true),
                        TrayMetric.GpuLoad => ("GPU%", s?.Gpu?.Load, Theme.DarkPalette.Gpu, false),
                        TrayMetric.RamLoad => ("RAM%", s?.RamLoad, Theme.DarkPalette.Ram, false),
                        _ => ("WATT", s?.TotalPower, Theme.DarkPalette.Power, false),
                    };
                    var shown = temp && value is { } v ? Fmt.TempValue(v) : value;
                    var fg = light ? Color.FromArgb(20, 20, 24) : Color.White;
                    var h = TrayIconRenderer.Render(_s.TrayStyle == TrayIconStyle.LabelAndNumber ? label : null,
                                                    shown is { } x ? Math.Min(x, 999).ToString("0") : "--", fg,
                                                    Theme.OnTaskbar(accent, light), _s.TrayStyle == TrayIconStyle.NumberOnly ? (value ?? 0) / 100 : null, size);
                    using (var icon = Icon.FromHandle(h)) g.DrawIcon(icon, new Rectangle(i * (size + size / 2), 0, size, size));
                    TrayIconRenderer.Destroy(h);
                }
            }
            return (bmp, null);
        }, 100);
        _live.Add(preview);
        p.Controls.Add(preview);

        p.Controls.Add(Hint("Optional extra icons in the notification area, each showing one number. On Windows 11, new icons start in the overflow (^) menu; drag them onto the taskbar to keep them visible."));
        var names = new (TrayMetric Metric, string Name, string Glyph)[]
        {
            (TrayMetric.CpuTemp, "CPU temperature", ""), (TrayMetric.CpuLoad, "CPU usage", ""),
            (TrayMetric.GpuTemp, "GPU temperature", ""), (TrayMetric.GpuLoad, "GPU usage", ""),
            (TrayMetric.RamLoad, "Memory usage", ""), (TrayMetric.Power, "CPU + GPU power", ""),
        };
        foreach (var (metric, name, glyph) in names)
        {
            p.Controls.Add(new Card(name, null, Toggle(_s.TrayMetrics.Contains(metric), v =>
            {
                _s.TrayMetrics.Remove(metric);
                if (v) _s.TrayMetrics.Add(metric);
            }), glyph));
        }
        p.Controls.Add(Section("Style"));
        p.Controls.Add(new Card("Icon style", "A small label above the number makes icons easy to tell apart.",
                                Combo(new[] { "Label above number", "Number only" }, (int)_s.TrayStyle, 200, i => _s.TrayStyle = (TrayIconStyle)i), ""));
    }

    // ---- Graphics ---------------------------------------------------------------------------

    void BuildGraphics(FlowLayoutPanel p)
    {
        var gpus = _host.Latest?.Summary.Gpus ?? Array.Empty<GpuSummary>();
        p.Controls.Add(Hint("Choose which GPU is shown as \"GPU\" on the taskbar, tray and dashboard. The other one is available as the \"Second GPU\" taskbar item."));

        var cards = new List<(ChoiceCard Card, string? Id)>();
        void Select(string? id)
        {
            _s.PrimaryGpuId = id;
            foreach (var (card, cid) in cards) { card.Selected = cid == id; card.Invalidate(); }
            Changed();
        }
        var auto = new ChoiceCard(() => ("Automatic", "Prefers the discrete graphics card", null), () => Select(null)) { Selected = _s.PrimaryGpuId == null };
        cards.Add((auto, null));
        p.Controls.Add(auto);
        foreach (var gpu in gpus)
        {
            var id = gpu.Id;
            var card = new ChoiceCard(() =>
            {
                var live = _host.Latest?.Summary.Gpus.FirstOrDefault(x => x.Id == id) ?? gpu;
                var detail = $"{Vitals.Pct(live.Load)} usage  ·  {Fmt.Temp(live.Temp)}  ·  {Vitals.Watts(live.Power)}" +
                             (live.VramTotalMb is { } v && !live.Integrated ? $"  ·  {v / 1024:0} GB VRAM" : "");
                return (live.Name, detail, live.Integrated ? "INTEGRATED" : "DISCRETE");
            }, () => Select(id)) { Selected = _s.PrimaryGpuId == id };
            cards.Add((card, id));
            _live.Add(card);
            p.Controls.Add(card);
        }
        if (gpus.Count == 0) p.Controls.Add(Hint("No GPUs detected yet."));

        if (gpus.Any(g => !g.Integrated && g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            p.Controls.Add(Section("NVIDIA power reading"));
            var ratios = _host.PowerRatios;
            string desc = _host.GpuPowerEstimated
                ? "To save about 20 MB of memory, PC Vitals doesn't keep NVIDIA's NVML library loaded. Board power is computed from the driver's power percentage " +
                  $"using a ratio measured on this PC{(ratios.Count > 0 ? $" ({string.Join(", ", ratios.Select(r => $"{r.Value:0.00} W per 1%"))})" : "")}. " +
                  "It is typically within 0.5 W. The dashboard reads exact values while it is open."
                : "Board power is read directly from NVIDIA's NVML library.";
            p.Controls.Add(new Card("Power measurement", desc, new PillButton("Recalibrate", () =>
            {
                _host.RecalibrateGpuPower();
                MessageBox.Show(this, "Recalibrating in the background; it takes a few seconds.", "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }), ""));
        }
    }

    // ---- Alerts -----------------------------------------------------------------------------

    void BuildAlerts(FlowLayoutPanel p)
    {
        p.Controls.Add(Hint("Get told when a reading stays above or below a limit: a notification, a sound, or a program of your choice. Click a notification to open the dashboard on that device. You can also add an alert from a sensor's menu in the dashboard."));
        _alertsHost = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 4),
            Tag = "stretch-list",
        };
        p.Controls.Add(_alertsHost);
        p.Controls.Add(new PillButton("Add alert…", () => EditAlert(null)) { Margin = new Padding(0, 4, 0, 8), AccessibleName = "Add an alert" });

        p.Controls.Add(Section("Recent alerts"));
        _alertHistory = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Theme.TextDim, Tag = "dim", Margin = new Padding(0, 0, 0, 8) };
        p.Controls.Add(_alertHistory);
        p.Controls.Add(new PillButton("Clear history", () =>
        {
            _host.AlertLog.Clear();
            AlertsChanged();
        }));
        BuildAlertRows();
    }

    FlowLayoutPanel? _alertsHost;
    Label? _alertHistory;

    void BuildAlertRows()
    {
        if (_alertsHost is not { } host) return;
        host.SuspendLayout();
        foreach (Control c in host.Controls.Cast<Control>().ToList()) c.Dispose();
        host.Controls.Clear();
        var snap = _host.Latest;
        foreach (var rule in _s.AlertRules.ToList())
        {
            var name = AlertText.Target(rule, snap, _s);
            var last = _host.LastFired(rule.Id) is { } t ? $" · went off at {t:HH:mm}" : "";
            var now = snap != null && rule.ValueIn(snap) is { } v ? $" · now {AlertText.Value(rule, v)}" : "";
            var edit = new PillButton("Edit…", () => EditAlert(rule)) { AccessibleName = $"Edit the {name} alert" };
            var test = new PillButton("Test", () => _host.TestAlert(rule)) { AccessibleName = $"Test the {name} alert" };
            var remove = new PillButton("", () => RemoveAlert(rule), 34) { Glyph = "", AccessibleName = $"Remove the {name} alert" };
            var toggle = Toggle(rule.Enabled, on => rule.Enabled = on);
            toggle.AccessibleName = $"{name} alert on";
            host.Controls.Add(new Card(name, $"{char.ToUpperInvariant(AlertText.Condition(rule)[0])}{AlertText.Condition(rule)[1..]}{now}{last}\n{AlertText.Actions(rule)}",
                                       new AccessoryRow(edit, test, remove, toggle)));
        }
        if (_s.AlertRules.Count == 0)
            host.Controls.Add(Hint("No alerts yet."));
        host.ResumeLayout();
        if (host.Parent is FlowLayoutPanel page) Stretch(page);
        UpdateAlertHistory();
    }

    void UpdateAlertHistory()
    {
        if (_alertHistory == null) return;
        var entries = _host.AlertLog.Entries;
        _alertHistory.Text = entries.Count == 0
            ? "Nothing yet."
            : string.Join("\n", entries.TakeLast(12).Reverse().Select(e => $"{e.Time:ddd d MMM HH:mm}   {e.Name}: {e.Text}"))
              + (entries.Count > 12 ? $"\n…and {entries.Count - 12} earlier (the last {AlertLog.Capacity} are kept)." : "");
    }

    void EditAlert(AlertRule? rule)
    {
        var edited = AlertEditor.Edit(this, rule ?? new AlertRule { Series = "cputemp", Threshold = 90 }, () => _host.Latest, _s, _host.TestAlert);
        if (edited == null) return;
        int i = rule == null ? -1 : _s.AlertRules.FindIndex(r => r.Id == rule.Id);
        if (i >= 0) _s.AlertRules[i] = edited;
        else _s.AlertRules.Add(edited);
        BuildAlertRows();
        Changed();
    }

    void RemoveAlert(AlertRule rule)
    {
        _s.AlertRules.RemoveAll(r => r.Id == rule.Id);
        // Rebuild after this click finishes; the clicked button is one of the controls being replaced.
        BeginInvoke(() =>
        {
            BuildAlertRows();
            Changed();
        });
    }

    /// <summary>An alert went off, was tested, or rules changed elsewhere (the dashboard).</summary>
    public void AlertsChanged() => BuildAlertRows();

    // ---- Appearance -------------------------------------------------------------------------

    void BuildAppearance(FlowLayoutPanel p)
    {
        p.Controls.Add(Section("Theme"));
        p.Controls.Add(Hint("Applies to the dashboard, hover cards, menus and this window. The taskbar strip always follows the taskbar's own light or dark setting."));
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 8) };
        var swatches = new List<(ThemeSwatch Swatch, ThemeMode Mode)>();
        foreach (var (mode, name, palette) in new (ThemeMode, string, Palette?)[]
                 {
                     (ThemeMode.System, "System", null), (ThemeMode.Light, "Light", Theme.LightPalette),
                     (ThemeMode.Dark, "Dark", Theme.DarkPalette), (ThemeMode.Amoled, "AMOLED black", Theme.AmoledPalette),
                 })
        {
            var swatch = new ThemeSwatch(name, palette, () =>
            {
                if (_s.ColorTheme == mode) return;
                _s.ColorTheme = mode;
                foreach (var (sw, m) in swatches) sw.Selected = m == mode;
                Changed(); // the app applies the theme, which recolours this window in place
            }) { Selected = _s.ColorTheme == mode };
            swatches.Add((swatch, mode));
            row.Controls.Add(swatch);
        }
        p.Controls.Add(row);

        p.Controls.Add(Section("Units"));
        p.Controls.Add(new Card("Temperature unit", null,
                                Combo(new[] { "Celsius (°C)", "Fahrenheit (°F)" }, _s.Fahrenheit ? 1 : 0, 180, i => _s.Fahrenheit = i == 1), ""));
    }

    // ---- General ----------------------------------------------------------------------------

    void BuildGeneral(FlowLayoutPanel p)
    {
        var intervals = new[] { 500, 1000, 2000, 5000 };
        p.Controls.Add(new Card("Refresh every", "Slower refresh uses less CPU. Drives and memory modules are always read less often.",
                                Combo(new[] { "0.5 seconds", "1 second", "2 seconds", "5 seconds" }, Math.Max(0, Array.IndexOf(intervals, _s.IntervalMs)), 160,
                                      i => _s.IntervalMs = intervals[i]), ""));

        var autostart = new ToggleSwitch { Checked = _host.IsAdmin && Autostart.IsEnabled(), Enabled = _host.IsAdmin };
        autostart.CheckedChanged += (_, _) =>
        {
            bool ok = autostart.Checked ? Autostart.Enable() : Autostart.Disable();
            if (!ok) MessageBox.Show(this, "Couldn't update the startup task.", "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        p.Controls.Add(new Card("Start with Windows",
                                _host.IsAdmin ? "Starts with administrator rights at sign-in, without a UAC prompt each time."
                                              : "Restart PC Vitals as administrator to change this.", autostart, ""));

        var hotkeys = Hotkeys.Choices;
        p.Controls.Add(new Card("Keyboard shortcut", "Opens or closes the dashboard from anywhere.",
                                Combo(hotkeys, Math.Max(0, Array.IndexOf(hotkeys, _s.Hotkey)), 190, i => _s.Hotkey = hotkeys[i]), ""));
        p.Controls.Add(new Card("Show advanced sensors in the dashboard", "Per-engine GPU load, power connector pins, memory timings and sensor limits.",
                                Toggle(_s.ShowAdvancedSensors, v => _s.ShowAdvancedSensors = v), ""));

        p.Controls.Add(Section("Network"));
        p.Controls.Add(new Card("Wi-Fi signal on the taskbar", "Shows the Wi-Fi signal strength as the NET item's main value while you're on Wi-Fi. The network name, signal and link speed are always on the dashboard's Network page.",
                                Toggle(_s.NetShowsWifi, v => _s.NetShowsWifi = v), ""));
        var host = new InputBox(_s.PingHost, "Router (default gateway)", 200, 253) { FieldName = "Host to ping" };
        host.TextChanged += (_, _) =>
        {
            _s.PingHost = host.Value.Trim();
            Changed();
        };
        p.Controls.Add(new Card("Measure latency (ping)",
                                "Off by default: it's the only thing PC Vitals sends over the network. Pings once every 5 seconds and shows the average, jitter and loss on the Network page (and can raise alerts). Leave the host empty to ping your router, so nothing leaves your local network.",
                                new AccessoryRow(host, Toggle(_s.PingEnabled, v => _s.PingEnabled = v)), ""));

        p.Controls.Add(Section("History"));
        p.Controls.Add(new Card("Save history", "Keeps one-minute readings on this PC (about 1–2 MB a day, never sent anywhere) for the 7- and 30-day graphs and exports. The 1 hour and 24 hour graphs also continue across restarts.",
                                Toggle(_s.HistoryEnabled, v => _s.HistoryEnabled = v), ""));
        var retention = new[] { 7, 30, 90 };
        p.Controls.Add(new Card("Keep history for", "Older days are deleted. Days older than a week are kept at 5-minute resolution.",
                                Combo(new[] { "7 days", "30 days", "90 days" }, Math.Max(0, Array.IndexOf(retention, _s.HistoryRetentionDays)), 160,
                                      i => _s.HistoryRetentionDays = retention[i]), ""));
        var used = new Label { AutoSize = true, ForeColor = Theme.TextDim, Tag = "dim", Text = Fmt.Bytes(_host.HistorySizeBytes), Margin = new Padding(8, 7, 0, 0) };
        var delete = new PillButton("Delete…", async () =>
        {
            if (MessageBox.Show(this, "Delete all saved history? The graphs keep what is in memory until PC Vitals restarts.", "PC Vitals",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                await _host.DeleteHistoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't delete everything: " + ex.Message, "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            used.Text = Fmt.Bytes(_host.HistorySizeBytes);
        });
        p.Controls.Add(new Card("Saved history", HistoryArchive.DefaultDir, new AccessoryRow(used,
            new PillButton("Export…", ExportHistory),
            new PillButton("Open folder", () =>
            {
                Directory.CreateDirectory(HistoryArchive.DefaultDir);
                Process.Start(new ProcessStartInfo(HistoryArchive.DefaultDir) { UseShellExecute = true });
            }),
            delete), ""));

        p.Controls.Add(Section("Reset"));
        p.Controls.Add(new Card("Reset all settings", "Restores the default taskbar layout, alerts and appearance.", new PillButton("Reset…", () =>
        {
            if (MessageBox.Show(this, "Reset all PC Vitals settings to their defaults?", "PC Vitals", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var fresh = AppSettings.CreateDefault();
            foreach (var prop in typeof(AppSettings).GetProperties().Where(x => x.CanWrite)) prop.SetValue(_s, prop.GetValue(fresh));
            Changed();
            // Rebuild so every control shows the defaults.
            BeginInvoke(() => _host.ReopenSettings("general"));
        }), ""));
    }

    // ---- About ------------------------------------------------------------------------------

    void BuildAbout(FlowLayoutPanel p)
    {
        var version = typeof(SettingsForm).Assembly.GetName().Version;
        p.Controls.Add(Hint($"PC Vitals {version?.ToString(3)}\nA lightweight system monitor for the Windows taskbar."));

        _usage = new Label { AutoSize = true, ForeColor = Theme.Text, Text = SelfUsage() }; // background comes from its card
        p.Controls.Add(new Card("Resource use", "What PC Vitals itself is using right now.", _usage, ""));

        var (pawnInstalled, pawnVersion) = HardwareService.PawnIoStatus();
        p.Controls.Add(new Card("PawnIO driver", pawnInstalled switch
        {
            true => $"Installed{(pawnVersion != null ? $" (version {pawnVersion})" : "")}. CPU temperature, power and motherboard sensors are available.",
            false => "Not installed. CPU temperature, power and motherboard sensors need this free, signed driver. Restart PC Vitals after installing it.",
            _ => "Couldn't check whether the driver is installed.",
        }, pawnInstalled == true ? null : new PillButton("Get PawnIO", () =>
        {
            try { Process.Start(new ProcessStartInfo(DashboardView.PawnIoUrl) { UseShellExecute = true }); }
            catch { }
        }) { Accent = true }, ""));

        p.Controls.Add(Section("Files"));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCVitals");
        p.Controls.Add(new Card("Settings and logs", dir, new AccessoryRow(
            new PillButton("Open folder", () => Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })),
            new PillButton("Save sensor report…", SaveReport)), ""));
        p.Controls.Add(new Card("Export history", "CPU, GPU, memory, power, network, drive and favourite sensor readings as a CSV file for Excel or other tools: the last 24 hours, or everything saved when history is on (General).",
            new PillButton("Export CSV…", ExportHistory), ""));

        p.Controls.Add(Section("Credits"));
        p.Controls.Add(new Card("LibreHardwareMonitor", "Sensor access for CPUs, GPUs, motherboards, memory and drives. MPL-2.0 licence.", null, ""));
        p.Controls.Add(new Card("PawnIO", "Signed driver used for CPU and motherboard sensors.", null, ""));
    }

    static string SelfUsage()
    {
        var proc = Process.GetCurrentProcess();
        double cpu = proc.TotalProcessorTime.TotalMilliseconds / Math.Max(1, (DateTime.Now - proc.StartTime).TotalMilliseconds) * 100 / Environment.ProcessorCount;
        return $"{cpu:0.00}% CPU on average  ·  {proc.PrivateMemorySize64 / 1048576.0:0} MB memory";
    }

    void ExportHistory()
    {
        if (_host.Archive is { } archive)
        {
            ExportDialog.Show(this, archive, _host);
            return;
        }
        // History isn't saved: export what's in memory (the last 24 hours).
        using var dialog = new SaveFileDialog
        {
            FileName = $"PC Vitals history {DateTime.Now:yyyy-MM-dd HHmm}.csv",
            Filter = "CSV file|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            int rows = _host.ExportHistory(dialog.FileName);
            if (rows == 0)
                MessageBox.Show(this, "There's no history yet: one-minute averages start building up a minute after PC Vitals starts.",
                                "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't export: " + ex.Message, "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SaveReport()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = $"PC Vitals sensors {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            Filter = "Text file|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--dump", dialog.FileName }, UseShellExecute = false })!;
            proc.WaitForExit(15000);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't write the report: " + ex.Message, "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
