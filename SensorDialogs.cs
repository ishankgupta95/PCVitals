namespace PCVitals;

/// <summary>Small themed dialog asking for one line of text.</summary>
sealed class TextPrompt : Form
{
    readonly InputBox _input;

    TextPrompt(string title, string hint, string text, string placeholder)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 168);
        KeyPreview = true;

        var hintLabel = new Label
        {
            Text = hint, AutoSize = false, Location = new Point(20, 16), Size = new Size(400, 40), ForeColor = Theme.TextDim,
        };
        _input = new InputBox(text, placeholder, 400, 60) { Location = new Point(20, 64) };
        _input.FieldName = title;
        var ok = new PillButton("OK", Accept, 96) { Accent = true, Location = new Point(220, 116) };
        var cancel = new PillButton("Cancel", Close, 96) { Location = new Point(324, 116) };
        Controls.AddRange(new Control[] { hintLabel, _input, ok, cancel });
        ResumeLayout(false);
        PerformLayout();
    }

    void Accept()
    {
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _input.FocusText();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter) Accept();
        else if (e.KeyCode == Keys.Escape) Close();
        else return;
        e.Handled = e.SuppressKeyPress = true;
    }

    /// <summary>The text entered, or null when cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string title, string hint, string text, string placeholder)
    {
        using var prompt = new TextPrompt(title, hint, text, placeholder);
        if (owner is Form { TopMost: true }) prompt.TopMost = true;
        return prompt.ShowDialog(owner) == DialogResult.OK ? prompt._input.Value : null;
    }
}

/// <summary>Rows of the sensor picker: favourites first, then every device's sensors.</summary>
static class SensorPickerModel
{
    public sealed record Row(string? Header, SensorEntry? Entry);

    public static List<Row> Build(SensorCatalog catalog, AppSettings settings, string filter)
    {
        // Search the names the user sees too (renames), not just the library's.
        var matches = filter.Trim().Length == 0
            ? catalog.All.ToList()
            : catalog.Search(filter).Union(catalog.All.Where(e => Matches(settings.SensorName(e.Id, e.Sensor.Name), filter))).ToList();
        var order = catalog.All.Select((e, i) => (e.Id, i)).ToDictionary(t => t.Id, t => t.i);
        matches.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        var rows = new List<Row>();
        var favorites = matches.Where(e => settings.IsFavorite(e.Id)).ToList();
        if (favorites.Count > 0)
        {
            rows.Add(new Row("Favourites", null));
            rows.AddRange(favorites.Select(e => new Row(null, e)));
        }
        foreach (var device in matches.Where(e => !settings.IsFavorite(e.Id)).GroupBy(e => e.Hardware.Id))
        {
            rows.Add(new Row(device.First().Hardware.Name, null));
            rows.AddRange(device.Select(e => new Row(null, e)));
        }
        return rows;
    }

    static bool Matches(string text, string filter) =>
        filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Searchable list of every sensor, grouped by device, with live values.</summary>
sealed class SensorPicker : Form
{
    readonly Func<Snapshot?> _latest;
    readonly AppSettings _settings;
    readonly InputBox _search;
    readonly ListBox _list;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    List<SensorPickerModel.Row> _rows = new();
    bool _moving;

    public SensorEntry? Picked { get; private set; }

    SensorPicker(Func<Snapshot?> latest, AppSettings settings)
    {
        _latest = latest;
        _settings = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Add a sensor to the taskbar";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowIcon = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 600);
        MinimumSize = new Size(420, 360);
        KeyPreview = true;

        _search = new InputBox("", "Search sensors (e.g. \"gpu hot\", \"fan\", \"nvme temp\")", 520, 80)
        {
            Location = new Point(20, 16), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _search.FieldName = "Search sensors";
        _search.TextChanged += (_, _) => Rebuild();
        _search.FieldKeyDown += (_, e) =>
        {
            // Arrow keys in the search box move the selection, so typing and picking never need the mouse.
            if (e.KeyCode is Keys.Down or Keys.Up or Keys.PageDown or Keys.PageUp)
            {
                Move(e.KeyCode switch { Keys.Down => 1, Keys.Up => -1, Keys.PageDown => 10, _ => -10 });
                e.Handled = e.SuppressKeyPress = true;
            }
        };

        _list = new ListBox
        {
            Location = new Point(20, 60), Size = new Size(520, 470),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
            DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, AccessibleName = "Sensors",
        };
        _list.DrawItem += DrawRow;
        _list.DoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Down or Keys.Up)) return;
            Move(e.KeyCode == Keys.Down ? 1 : -1);
            e.Handled = true;
        };
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (!_moving) SkipHeader(1);
        };

        var add = new PillButton("Add to taskbar", Accept, 140) { Accent = true, Location = new Point(296, 548), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        var cancel = new PillButton("Cancel", Close, 96) { Location = new Point(444, 548), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        Controls.AddRange(new Control[] { _search, _list, add, cancel });
        ResumeLayout(false);
        PerformLayout();

        _refresh.Tick += (_, _) => _list.Invalidate();
        Rebuild();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
        _list.ItemHeight = Ui.S(this, 30);
        _refresh.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.FocusText();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter) Accept();
        else if (e.KeyCode == Keys.Escape) Close();
        else return;
        e.Handled = e.SuppressKeyPress = true;
    }

    void Rebuild()
    {
        var catalog = _latest()?.Catalog;
        _rows = catalog == null ? new() : SensorPickerModel.Build(catalog, _settings, _search.Value);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows)
            _list.Items.Add(row.Header ?? _settings.SensorName(row.Entry!.Id, row.Entry.Sensor.Name));
        _list.EndUpdate();
        int first = _rows.FindIndex(r => r.Entry != null);
        if (first >= 0) _list.SelectedIndex = first;
    }

    void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _moving = true;
        try
        {
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _rows.Count - 1);
            SkipHeader(delta >= 0 ? 1 : -1);
        }
        finally
        {
            _moving = false;
        }
    }

    /// <summary>Headers can't be picked: step past them in the direction of travel.</summary>
    void SkipHeader(int direction)
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _rows.Count || _rows[i].Entry != null) return;
        int j = i;
        while (j >= 0 && j < _rows.Count && _rows[j].Entry == null) j += direction;
        if (j < 0 || j >= _rows.Count)
            for (j = i; j >= 0 && j < _rows.Count && _rows[j].Entry == null; j -= direction) { }
        if (j >= 0 && j < _rows.Count && j != i) _list.SelectedIndex = j;
    }

    void Accept()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _rows.Count || _rows[i].Entry is not { } entry) return;
        Picked = entry;
        DialogResult = DialogResult.OK;
        Close();
    }

    void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _rows.Count) return;
        var g = e.Graphics;
        var f = UiFonts.For(DeviceDpi);
        var row = _rows[e.Index];
        var r = e.Bounds;
        int pad = Ui.S(this, 12);
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, r);
        if (row.Header != null)
        {
            Gfx.Text(g, row.Header.ToUpperInvariant(), f.Label, Theme.TextFaint, new Rectangle(r.Left + pad, r.Top, r.Width - pad * 2, r.Height),
                     TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            return;
        }
        var entry = row.Entry!;
        if ((e.State & DrawItemState.Selected) != 0)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Gfx.FillRounded(g, Rectangle.Inflate(r, -Ui.S(this, 4), -Ui.S(this, 2)), Ui.S(this, 6), Theme.WithAlpha(Theme.Cpu, 50));
        }
        // Live value from the newest snapshot (the list itself is rebuilt only when the search changes).
        var live = _latest()?.Catalog[entry.Id]?.Sensor ?? entry.Sensor;
        var name = _settings.SensorName(entry.Id, entry.Sensor.Name);
        var note = _settings.IsPinned(entry.Id) ? "  · on taskbar" : _settings.IsFavorite(entry.Id) ? $"  · {HardwareService.ShortName(entry.Hardware.Name)}" : "";
        int valueW = Ui.S(this, 120);
        Gfx.Text(g, name + note, f.Body, Theme.Text, new Rectangle(r.Left + pad + Ui.S(this, 6), r.Top, r.Width - valueW - pad * 2, r.Height),
                 TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Gfx.Text(g, Fmt.Sensor(live.Type, live.Value), f.BodyBold, live.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature ? Theme.TempColor(live.Value) : Theme.TextDim,
                 new Rectangle(r.Right - valueW - pad, r.Top, valueW, r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    /// <summary>The sensor chosen, or null when cancelled.</summary>
    public static SensorEntry? Pick(IWin32Window? owner, Func<Snapshot?> latest, AppSettings settings)
    {
        using var picker = new SensorPicker(latest, settings);
        return picker.ShowDialog(owner) == DialogResult.OK ? picker.Picked : null;
    }
}
