using System.Drawing.Drawing2D;

namespace PCVitals;

/// <summary>
/// Layout editor for a desktop panel: a palette of tile types, a live canvas to drag, drop and resize tiles
/// on, and the selected tile's properties. Edits a copy; Save hands it back to the app.
/// </summary>
sealed class PanelEditorForm : Form
{
    readonly AppSettings _settings;
    readonly Func<Snapshot?> _latest;
    readonly Action<PanelLayout> _save;
    readonly List<PanelLayout> _undo = new(), _redo = new();
    PanelLayout _saved;
    string? _lastEditKey;
    DateTime _lastEditTime;
    int _selected = -1;
    // Set while fields are filled in from the layout, so their change events aren't taken as edits.
    bool _suppress;

    readonly EditorCanvas _canvas;
    readonly FlowLayoutPanel _props;
    readonly PillButton _undoButton, _redoButton, _saveButton;

    public PanelLayout Working { get; private set; }
    internal PanelRenderer Renderer { get; private set; }
    public string PanelId => Working.Id;

    public PanelEditorForm(AppSettings settings, PanelLayout layout, Func<Snapshot?> latest, Func<string, int, float[]>? history, Action<PanelLayout> save)
    {
        _settings = settings;
        _latest = latest;
        _save = save;
        _saved = layout.Clone();
        Working = layout.Clone();
        Renderer = new PanelRenderer(Working, settings) { HistoryProvider = history, IntervalMs = settings.IntervalMs };

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 780);
        MinimumSize = new Size(940, 560);
        Icon = TrayIconRenderer.AppIcon;
        KeyPreview = true;

        // Toolbar.
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 10, 12, 6), BackColor = Theme.Rail, WrapContents = false };
        _undoButton = Tool("Undo", Undo, "Undo (Ctrl+Z)");
        _redoButton = Tool("Redo", Redo, "Redo (Ctrl+Y)");
        bar.Controls.Add(_undoButton);
        bar.Controls.Add(_redoButton);
        bar.Controls.Add(Spacer());
        bar.Controls.Add(Tool("Duplicate", Duplicate, "Duplicate the selected tile (Ctrl+D)"));
        bar.Controls.Add(Tool("Delete", DeleteSelected, "Delete the selected tile (Delete)"));
        bar.Controls.Add(Spacer());
        var presets = Tool("Start from layout…", () => { }, "Replace the tiles with one of the ready-made layouts");
        presets.Click += (_, _) => PresetMenu().Show(presets, new Point(0, presets.Height));
        bar.Controls.Add(presets);
        bar.Controls.Add(Tool("Import…", Import, "Load a layout from a file"));
        bar.Controls.Add(Tool("Export…", Export, "Save this layout to a file to share or move to another PC"));
        bar.Controls.Add(Spacer());
        bar.Controls.Add(Tool("Revert", Revert, "Go back to the last saved layout"));
        _saveButton = Tool("Save", Save, "Save and show on the desktop (Ctrl+S)");
        _saveButton.Accent = true;
        bar.Controls.Add(_saveButton);

        // Palette.
        var palette = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = 176, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(12, 12, 8, 12), BackColor = Theme.Rail,
        };
        palette.Controls.Add(new Label { Text = "ADD A TILE", AutoSize = true, ForeColor = Theme.TextDim, Font = new Font("Segoe UI Semibold", 8.25f), Margin = new Padding(4, 0, 0, 6) });
        foreach (var item in PaletteItem.All)
            palette.Controls.Add(new PaletteButton(item, () => AddTile(item.Make(), null)));
        palette.Controls.Add(new Label
        {
            Text = "Drag a tile onto the panel, or click to add it in the first free spot.", AutoSize = true, MaximumSize = new Size(150, 0),
            ForeColor = Theme.TextDim, Margin = new Padding(4, 10, 0, 0),
        });

        // Properties.
        _props = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 340, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(16, 12, 16, 16), BackColor = Theme.Rail,
        };

        _canvas = new EditorCanvas(this) { Dock = DockStyle.Fill };

        Controls.Add(_canvas);
        Controls.Add(_props);
        Controls.Add(palette);
        Controls.Add(bar);
        ResumeLayout(false);
        PerformLayout();
        BuildProperties();
        UpdateTitle();
    }

    static Control Spacer() => new Panel { Width = 14, Height = 1, BackColor = Color.Transparent, Margin = Padding.Empty };

    static PillButton Tool(string text, Action onClick, string description) =>
        new(text, onClick) { Margin = new Padding(0, 0, 6, 0), AccessibleDescription = description };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
        // Scrollbars in the theme's colours.
        try { SetWindowTheme(_props.Handle, Theme.IsLight ? "Explorer" : "DarkMode_Explorer", null); }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    public void SetSnapshot(Snapshot snap)
    {
        if (Visible) _canvas.Invalidate();
    }

    internal Snapshot? Latest => _latest();
    internal int Selected => _selected;
    internal AppSettings Settings => _settings;

    // ---- Editing ------------------------------------------------------------------------------

    bool Dirty => System.Text.Json.JsonSerializer.Serialize(Working, AppSettings.Json) != System.Text.Json.JsonSerializer.Serialize(_saved, AppSettings.Json);

    void UpdateTitle()
    {
        Text = $"Edit panel: {Working.Name}{(Dirty ? " •" : "")} - PC Vitals";
        _undoButton.Enabled = _undo.Count > 0;
        _redoButton.Enabled = _redo.Count > 0;
        _undoButton.Invalidate();
        _redoButton.Invalidate();
    }

    /// <summary>
    /// Makes a change that can be undone. Changes with the same <paramref name="key"/> close together (typing in a
    /// field) are one undo step.
    /// </summary>
    internal void Edit(Action change, string? key = null, bool rebuild = false)
    {
        if (_suppress) return;
        bool merge = key != null && key == _lastEditKey && (DateTime.Now - _lastEditTime).TotalSeconds < 1.5;
        if (!merge)
        {
            _undo.Add(Working.Clone());
            if (_undo.Count > 100) _undo.RemoveAt(0);
        }
        _redo.Clear();
        _lastEditKey = key;
        _lastEditTime = DateTime.Now;
        // No longer the preset as it came (picking a preset sets this again).
        Working.Preset = null;
        change();
        Changed(rebuild);
    }

    void Changed(bool rebuild)
    {
        if (_selected >= Working.Tiles.Count) _selected = Working.Tiles.Count - 1;
        Renderer.Invalidate();
        _canvas.Invalidate();
        _canvas.AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        if (rebuild) BuildProperties();
        UpdateTitle();
    }

    /// <summary>Replaces the working layout (undo, redo, revert), keeping the same renderer settings.</summary>
    void Replace(PanelLayout layout)
    {
        Working = layout;
        var old = Renderer;
        Renderer = new PanelRenderer(layout, _settings) { HistoryProvider = old.HistoryProvider, IntervalMs = old.IntervalMs };
        old.Dispose();
        _lastEditKey = null;
        Changed(rebuild: true);
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Working.Clone());
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Replace(previous);
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Working.Clone());
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Replace(next);
    }

    internal void Select(int index)
    {
        index = Math.Clamp(index, -1, Working.Tiles.Count - 1);
        if (index == _selected) return;
        _selected = index;
        _canvas.Invalidate();
        BuildProperties();
        _canvas.AccessibilityNotifyClients(AccessibleEvents.Focus, index);
    }

    /// <summary>Adds a tile where it was dropped (<paramref name="cells"/> or position), else in the first free spot.</summary>
    internal void AddTile(PanelTile tile, Rectangle? at)
    {
        Edit(() =>
        {
            if (at is { } spot && Working.FreePlacement)
            {
                (tile.X, tile.Y, tile.Width, tile.Height) = (spot.X, spot.Y, spot.Width, spot.Height);
                Working.Tiles.Add(tile);
                Working.Width = Math.Max(Working.Width, tile.X + tile.Width);
                Working.Height = Math.Max(Working.Height, tile.Y + tile.Height);
            }
            else if (at is { } cells && PanelGrid.Fits(Working, -1, cells))
            {
                (tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan) = (cells.X, cells.Y, cells.Width, cells.Height);
                Working.Tiles.Add(tile);
            }
            else
            {
                if (Working.FreePlacement) (tile.Width, tile.Height) = (Math.Max(tile.Width, 60), Math.Max(tile.Height, 40));
                PanelGrid.Add(Working, tile);
            }
        });
        Select(Working.Tiles.Count - 1);
        _canvas.Focus();
    }

    internal void Duplicate()
    {
        if (_selected < 0) return;
        var copy = Working.Tiles[_selected].Clone();
        if (Working.FreePlacement)
        {
            copy.X = Math.Min(copy.X + 12, Math.Max(0, Working.Width - copy.Width));
            copy.Y = Math.Min(copy.Y + 12, Math.Max(0, Working.Height - copy.Height));
            Edit(() => Working.Tiles.Add(copy));
        }
        else Edit(() => PanelGrid.Add(Working, copy));
        Select(Working.Tiles.Count - 1);
    }

    void DeleteSelected()
    {
        if (_selected < 0) return;
        int index = _selected;
        Edit(() => Working.Tiles.RemoveAt(index));
        _selected = -2; // force a rebuild
        Select(Math.Min(index, Working.Tiles.Count - 1));
    }

    /// <summary>Free placement: moves the selected tile up or down the stack (later tiles are drawn on top).</summary>
    void Restack(int delta)
    {
        int from = _selected, to = from + delta;
        if (from < 0 || to < 0 || to >= Working.Tiles.Count) return;
        Edit(() =>
        {
            var t = Working.Tiles[from];
            Working.Tiles.RemoveAt(from);
            Working.Tiles.Insert(to, t);
        });
        _selected = to;
        _canvas.Invalidate();
    }

    void Save()
    {
        _save(Working.Clone());
        _saved = Working.Clone();
        UpdateTitle();
    }

    void Revert()
    {
        if (!Dirty) return;
        Edit(() => { }, rebuild: false);
        Replace(_saved.Clone());
    }

    ContextMenuStrip PresetMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, BackColor = Theme.Surface };
        foreach (var (id, name, description) in PanelPresets.All)
            menu.Items.Add(new ToolStripMenuItem($"{name}  ·  {description}", null, (_, _) =>
            {
                Edit(() => Working.ApplyPreset(PanelPresets.Build(id, _latest())));
                _selected = -2;
                Select(-1);
            }) { ForeColor = Theme.Text });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        return menu;
    }

    void Import()
    {
        using var dialog = new OpenFileDialog { Filter = PanelExchange.FileFilter, Title = "Import a panel layout" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var layout = PanelExchange.Import(dialog.FileName);
            Edit(() => Working.ApplyPreset(layout));
            _selected = -2;
            Select(-1);
            var missing = layout.Tiles.Count(t => t.Kind == PanelTileKind.Value && _latest() is { } snap && PanelSources.Read(t, snap, _settings)?.Value == null);
            if (missing > 0)
                MessageBox.Show(this, $"{missing} reading{(missing == 1 ? "" : "s")} in this layout aren't available on this PC and show \"--\". Select a tile to pick another reading.",
                                "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't import the layout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Export()
    {
        var name = string.Concat(Working.Name.Split(Path.GetInvalidFileNameChars()));
        using var dialog = new SaveFileDialog { Filter = PanelExchange.FileFilter, FileName = (name.Length > 0 ? name : "panel") + ".pcvpanel.json", Title = "Export the panel layout" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { PanelExchange.Export(Working, dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't export the layout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Dirty && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "Save the changes to this panel?", "PC Vitals", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) e.Cancel = true;
            else if (answer == DialogResult.Yes) Save();
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Text fields keep their own Ctrl+Z.
        bool inText = ActiveControl is TextBoxBase || (ActiveControl as ContainerControl)?.ActiveControl is TextBoxBase || FindFocusedTextBox(this);
        switch (keyData)
        {
            case Keys.Control | Keys.Z when !inText: Undo(); return true;
            case Keys.Control | Keys.Y when !inText:
            case Keys.Control | Keys.Shift | Keys.Z when !inText: Redo(); return true;
            case Keys.Control | Keys.D: Duplicate(); return true;
            case Keys.Control | Keys.S: Save(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static bool FindFocusedTextBox(Control root)
    {
        foreach (Control c in root.Controls)
            if ((c is TextBoxBase && c.Focused) || FindFocusedTextBox(c)) return true;
        return false;
    }

    internal void CanvasKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Tab)
        {
            int n = Working.Tiles.Count;
            if (n == 0) return;
            Select(((_selected < 0 ? (e.Shift ? 0 : -1) : _selected) + (e.Shift ? -1 : 1) + n) % n);
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Escape) { Select(-1); e.Handled = true; return; }
        if (_selected < 0) return;
        int dx = e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, _ => 0 };
        int dy = e.KeyCode switch { Keys.Up => -1, Keys.Down => 1, _ => 0 };
        if (dx == 0 && dy == 0) return;
        e.Handled = true;
        var t = Working.Tiles[_selected];
        int index = _selected;
        if (Working.FreePlacement)
        {
            int step = e.Control ? 10 : 1;
            Edit(() =>
            {
                if (e.Shift)
                {
                    t.Width = Math.Clamp(t.Width + dx * step, PanelGrid.MinFreeSize, Working.Width - t.X);
                    t.Height = Math.Clamp(t.Height + dy * step, PanelGrid.MinFreeSize, Working.Height - t.Y);
                }
                else
                {
                    t.X = Math.Clamp(t.X + dx * step, 0, Math.Max(0, Working.Width - t.Width));
                    t.Y = Math.Clamp(t.Y + dy * step, 0, Math.Max(0, Working.Height - t.Height));
                }
            }, "key-" + index);
        }
        else
        {
            var cells = t.Cells;
            var next = e.Shift ? cells with { Width = cells.Width + dx, Height = cells.Height + dy } : cells with { X = cells.X + dx, Y = cells.Y + dy };
            if (!PanelGrid.Fits(Working, index, next)) { System.Media.SystemSounds.Beep.Play(); return; }
            Edit(() => PanelGrid.Place(Working, index, next), "key-" + index);
        }
        UpdatePositionFields();
    }

    // ---- Properties ---------------------------------------------------------------------------

    Action? _updatePosition;

    void UpdatePositionFields() => _updatePosition?.Invoke();

    void BuildProperties()
    {
        _props.SuspendLayout();
        foreach (Control c in _props.Controls.Cast<Control>().ToList()) c.Dispose();
        _props.Controls.Clear();
        _updatePosition = null;
        if (_selected >= 0 && _selected < Working.Tiles.Count) BuildTileProperties(Working.Tiles[_selected]);
        else BuildPanelProperties();
        _props.ResumeLayout();
    }

    int FieldWidth => 300;

    void Heading(string text) => _props.Controls.Add(new Label
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 8),
    });

    void Section(string text) => _props.Controls.Add(new Label
    {
        Text = text.ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI Semibold", 8.25f), ForeColor = Theme.TextDim, Margin = new Padding(0, 14, 0, 4),
    });

    void Note(string text) => _props.Controls.Add(new Label
    {
        Text = text, AutoSize = true, MaximumSize = new Size(FieldWidth, 0), ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 0, 6),
    });

    /// <summary>A labelled field: the title above its control.</summary>
    void Field(string title, Control control)
    {
        _props.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 3) });
        control.AccessibleName ??= title;
        control.Margin = new Padding(0, 0, 0, 2);
        _props.Controls.Add(control);
    }

    /// <summary>A toggle on the same line as its title.</summary>
    void Switch(string title, bool value, Action<bool> set, bool rebuild = false)
    {
        var t = new ToggleSwitch { Checked = value, AccessibleName = title };
        t.CheckedChanged += (_, _) => Edit(() => set(t.Checked), rebuild: rebuild);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail, Margin = new Padding(0, 4, 0, 0) };
        var label = new Label { Text = title, AutoSize = false, Width = FieldWidth - 64, Height = 26, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text };
        row.Controls.Add(label);
        row.Controls.Add(t);
        _props.Controls.Add(row);
    }

    Dropdown Choice(string title, string[] items, int selected, Action<int> set, bool rebuild = false)
    {
        var d = new Dropdown(items, Math.Clamp(selected, 0, items.Length - 1), FieldWidth);
        d.SelectedIndexChanged += (_, _) => Edit(() => set(d.SelectedIndex), rebuild: rebuild);
        Field(title, d);
        return d;
    }

    InputBox TextField(string title, string value, string placeholder, Action<string> set, string key, int maxLength = 200)
    {
        var box = new InputBox(value, placeholder, FieldWidth, maxLength) { FieldName = title };
        box.TextChanged += (_, _) => Edit(() => set(box.Value), key);
        Field(title, box);
        return box;
    }

    /// <summary>A whole-number field; ignores text that isn't one.</summary>
    InputBox Number(string title, int value, int min, int max, Action<int> set, string key, Control? host = null)
    {
        var box = new InputBox(value.ToString(), "", host != null ? 66 : FieldWidth, 5) { FieldName = title };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Value, out var v) && v >= min && v <= max) Edit(() => set(v), key);
        };
        if (host != null)
        {
            host.Controls.Add(new Label { Text = title, AutoSize = false, Width = 22, Height = 32, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.TextDim, Margin = Padding.Empty });
            box.Margin = new Padding(2, 0, 6, 0);
            host.Controls.Add(box);
        }
        else Field(title, box);
        return box;
    }

    void Colour(string title, string? value, Func<Color> automatic, Action<string?> set)
    {
        var picker = new ColorPicker(PanelColor.Parse(value), automatic, 190);
        picker.ValueChanged += () => Edit(() => set(picker.Value is { } c ? PanelColor.ToHex(c) : null));
        var auto = new PillButton("Automatic", () =>
        {
            picker.Value = null;
            Edit(() => set(null));
        }) { AccessibleName = title + ": automatic" };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail, Margin = Padding.Empty };
        picker.Margin = new Padding(0, 0, 8, 0);
        picker.AccessibleName = title;
        row.Controls.Add(picker);
        row.Controls.Add(auto);
        Field(title, row);
    }

    void FontField(string title, string? value, string placeholder, Action<string?> set)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail, Margin = Padding.Empty };
        var box = new InputBox(value ?? "", placeholder, FieldWidth - 96, 64) { FieldName = title };
        box.TextChanged += (_, _) => Edit(() => set(string.IsNullOrWhiteSpace(box.Value) ? null : box.Value.Trim()), "font-" + title);
        var choose = new PillButton("Choose…", () =>
        {
            using var dialog = new FontDialog { ShowEffects = false, ShowColor = false, FontMustExist = true };
            try { dialog.Font = new Font(string.IsNullOrWhiteSpace(box.Value) ? placeholder : box.Value, 12f); }
            catch (ArgumentException) { }
            if (dialog.ShowDialog(this) == DialogResult.OK) box.Value = dialog.Font.FontFamily.Name;
        }, 88) { AccessibleName = $"Choose {title.ToLowerInvariant()}" };
        box.Margin = new Padding(0, 0, 8, 0);
        row.Controls.Add(box);
        row.Controls.Add(choose);
        Field(title, row);
    }

    PillButton Button(string text, Action onClick, string? accessibleName = null)
    {
        var b = new PillButton(text, onClick) { Margin = new Padding(0, 6, 8, 0) };
        if (accessibleName != null) b.AccessibleName = accessibleName;
        return b;
    }

    void Buttons(params PillButton[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Rail, Margin = new Padding(0, 4, 0, 0), MaximumSize = new Size(FieldWidth + 10, 0) };
        row.Controls.AddRange(buttons);
        _props.Controls.Add(row);
    }

    static readonly int[] Radii = { 0, 4, 6, 8, 10, 14, 20 };
    static readonly string[] Fits = { "Fill (crop)", "Fit (show all)", "Stretch", "Tile", "Centre" };

    void BuildPanelProperties()
    {
        var l = Working;
        Heading("Panel");
        Note("Select a tile on the panel to change it. Colours and opacity are in Settings → Desktop panel.");
        TextField("Name", l.Name, "Panel", v => l.Name = v.Trim().Length > 0 ? v.Trim() : "Panel", "name", 40);

        Section("Layout");
        Choice("Placement", new[] { "Grid (tiles snap to cells)", "Free (place tiles anywhere)" }, l.FreePlacement ? 1 : 0,
               i => PanelGrid.SetFreePlacement(l, i == 1), rebuild: true);
        if (l.FreePlacement)
        {
            var size = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail };
            Number("W", l.Width, PanelGrid.MinFreeSize, 4000, v => l.Width = Math.Max(v, l.Tiles.Select(t => t.X + t.Width).DefaultIfEmpty(0).Max()), "panel-w", size);
            Number("H", l.Height, PanelGrid.MinFreeSize, 4000, v => l.Height = Math.Max(v, l.Tiles.Select(t => t.Y + t.Height).DefaultIfEmpty(0).Max()), "panel-h", size);
            Field("Size (at 100 %)", size);
            Note("Tiles snap to the panel's edges and centre, and to each other. Later tiles are drawn on top.");
        }
        else
        {
            var cols = Enumerable.Range(1, PanelGrid.MaxColumns).Select(i => i.ToString()).ToArray();
            var rows = Enumerable.Range(1, PanelGrid.MaxRows).Select(i => i.ToString()).ToArray();
            Dropdown? colsBox = null, rowsBox = null;
            colsBox = Choice("Columns", cols, l.Columns - 1, i =>
            {
                if (!PanelGrid.Resize(l, i + 1, l.Rows)) BeginInvoke(() => Refused("Move or shrink the tiles on the right first."));
            }, rebuild: true);
            rowsBox = Choice("Rows", rows, l.Rows - 1, i =>
            {
                if (!PanelGrid.Resize(l, l.Columns, i + 1)) BeginInvoke(() => Refused("Move or shrink the tiles at the bottom first."));
            }, rebuild: true);
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail };
            Number("W", l.CellWidth, 24, 800, v => l.CellWidth = v, "cell-w", cell);
            Number("H", l.CellHeight, 16, 800, v => l.CellHeight = v, "cell-h", cell);
            Field("Cell size (at 100 %)", cell);
            var spacing = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail };
            Number("Gap", l.Gap, 0, 60, v => l.Gap = v, "gap", spacing);
            Number("Pad", l.Padding, 0, 80, v => l.Padding = v, "padding", spacing);
            Field("Space between tiles and around them", spacing);
        }

        Section("Look");
        Choice("Corner rounding", Radii.Select(r => r == 0 ? "Square" : $"{r} px").ToArray(), Math.Max(0, Array.IndexOf(Radii, l.Style.CornerRadius)),
               i => l.Style.CornerRadius = Radii[i]);
        FontField("Font", l.Style.FontFamily, "Segoe UI", v => l.Style.FontFamily = v ?? "Segoe UI");

        Section("Background picture");
        Note(l.Style.BackgroundImage == null ? "None. A picture goes behind the tiles; set the background opacity in Settings to tint it." : "A picture behind the tiles.");
        Buttons(Button(l.Style.BackgroundImage == null ? "Choose picture…" : "Change picture…", () =>
                {
                    if (ChoosePicture() is { } name) Edit(() => l.Style.BackgroundImage = name, rebuild: true);
                }),
                Button("Remove", () => Edit(() => l.Style.BackgroundImage = null, rebuild: true)));
        if (l.Style.BackgroundImage != null)
            Choice("Fit", Fits, (int)l.Style.BackgroundFit, i => l.Style.BackgroundFit = (PanelImageFit)i);
    }

    void Refused(string why) => MessageBox.Show(this, why, "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Information);

    string? ChoosePicture()
    {
        using var dialog = new OpenFileDialog { Filter = PanelImages.FileFilter, Title = "Choose a picture" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        var name = PanelImages.Import(dialog.FileName);
        if (name == null) Refused("That file couldn't be read as a picture.");
        return name;
    }

    static readonly int[] GraphTimes = { 60, 300, 900 };
    static readonly int[] TextSizes = { 50, 75, 100, 125, 150, 200, 250 };
    static readonly string[] ClockFormats = { "HH:mm", "HH:mm:ss", "h:mm tt", "dddd d MMMM", "ddd d MMM", "d MMM yyyy", "dddd" };

    void BuildTileProperties(PanelTile t)
    {
        var snap = _latest();
        Heading(t.Kind switch
        {
            PanelTileKind.Text => "Text tile",
            PanelTileKind.Clock => "Clock tile",
            PanelTileKind.Divider => "Divider",
            PanelTileKind.Image => "Picture tile",
            _ => "Reading tile",
        });

        switch (t.Kind)
        {
            case PanelTileKind.Value:
                BuildValueProperties(t, snap);
                break;
            case PanelTileKind.Text:
            {
                var box = TextField("Text", t.Text ?? "", "Text", v => t.Text = v, "text", 300);
                var items = PanelText.Available(snap?.Summary);
                var insert = new Dropdown(new[] { "Insert a reading…" }.Concat(items.Select(i => i.Name)).ToArray(), 0, FieldWidth);
                insert.SelectedIndexChanged += (_, _) =>
                {
                    if (insert.SelectedIndex <= 0) return;
                    box.Value += items[insert.SelectedIndex - 1].Placeholder;
                    insert.SelectedIndex = 0;
                };
                Field("Readings in the text", insert);
                Note("Readings appear as {name} in the text, e.g. \"CPU {cputemp}\".");
                TextStyle(t);
                break;
            }
            case PanelTileKind.Clock:
            {
                int known = Array.IndexOf(ClockFormats, t.Text ?? "HH:mm");
                var labels = ClockFormats.Select(f => $"{DateTime.Now.ToString(f)}   ({f})").Append("Custom").ToArray();
                Choice("Shows", labels, known < 0 ? labels.Length - 1 : known, i => { if (i < ClockFormats.Length) t.Text = ClockFormats[i]; }, rebuild: true);
                if (known < 0) TextField("Custom format", t.Text ?? "", "HH:mm", v => t.Text = v, "clock", 40);
                Note("Formats use .NET date patterns: HH hours, mm minutes, dddd weekday, MMMM month.");
                TextStyle(t);
                break;
            }
            case PanelTileKind.Image:
                Note(t.Image == null ? "No picture chosen yet." : "Shows a picture.");
                Buttons(Button(t.Image == null ? "Choose picture…" : "Change picture…", () =>
                {
                    if (ChoosePicture() is { } name) Edit(() => t.Image = name, rebuild: true);
                }));
                Choice("Fit", Fits, (int)t.ImageFit, i => t.ImageFit = (PanelImageFit)i);
                break;
            case PanelTileKind.Divider:
                Note("A thin line across the tile (or down it, when the tile is taller than wide).");
                break;
        }

        BuildPositionFields(t);

        Section("Tile");
        var actions = new List<PillButton> { Button("Duplicate", Duplicate), Button("Delete", DeleteSelected) };
        if (Working.FreePlacement)
        {
            actions.Add(Button("Bring forward", () => Restack(1)));
            actions.Add(Button("Send back", () => Restack(-1)));
        }
        Buttons(actions.ToArray());
    }

    void BuildPositionFields(PanelTile t)
    {
        if (!Working.FreePlacement)
        {
            var info = new Label { AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(0, 14, 0, 0) };
            _updatePosition = () => info.Text = $"Column {t.Column + 1}, row {t.Row + 1}; {t.ColumnSpan} × {t.RowSpan} cells. Drag to move, drag an edge to resize, or use the arrow keys (Shift to resize).";
            info.MaximumSize = new Size(FieldWidth, 0);
            _updatePosition();
            _props.Controls.Add(info);
            return;
        }
        Section("Position and size (at 100 %)");
        var pos = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail };
        var x = Number("X", t.X, 0, 4000, v => t.X = Math.Min(v, Math.Max(0, Working.Width - t.Width)), "x", pos);
        var y = Number("Y", t.Y, 0, 4000, v => t.Y = Math.Min(v, Math.Max(0, Working.Height - t.Height)), "y", pos);
        _props.Controls.Add(pos);
        var size = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail, Margin = new Padding(0, 4, 0, 0) };
        var w = Number("W", t.Width, PanelGrid.MinFreeSize, 4000, v => t.Width = Math.Min(v, Working.Width - t.X), "w", size);
        var h = Number("H", t.Height, PanelGrid.MinFreeSize, 4000, v => t.Height = Math.Min(v, Working.Height - t.Y), "h", size);
        _props.Controls.Add(size);
        _updatePosition = () =>
        {
            // Shown without making another undo step: the move was already one.
            _suppress = true;
            (x.Value, y.Value, w.Value, h.Value) = (t.X.ToString(), t.Y.ToString(), t.Width.ToString(), t.Height.ToString());
            _suppress = false;
        };
    }

    internal void PositionChanged() => _updatePosition?.Invoke();

    void TextStyle(PanelTile t)
    {
        Section("Text");
        Choice("Size", TextSizes.Select(s => s + " %").ToArray(), Math.Max(0, Array.IndexOf(TextSizes, t.FontSize)), i => t.FontSize = TextSizes[i]);
        FontField("Font", t.FontFamily, Working.Style.FontFamily, v => t.FontFamily = v);
        Colour("Colour", t.ValueColor, () => PanelColor.Parse(Working.Style.ValueColor) ?? Theme.Text, v => t.ValueColor = v);
    }

    void BuildValueProperties(PanelTile t, Snapshot? snap)
    {
        // The reading: a headline one, or any sensor.
        var sources = PanelSources.All(snap?.Summary);
        var names = sources.Select(s => s.Name).ToList();
        int selected = t.Sensor == null ? sources.FindIndex(s => s.Key == t.Source) : -1;
        if (t.Sensor is { } key)
        {
            names.Add("Sensor: " + _settings.SensorName(key.Id, key.SensorName) + $" ({HardwareService.ShortName(key.HardwareName)})");
            selected = names.Count - 1;
        }
        names.Add("Pick any sensor…");
        var reading = new Dropdown(names.ToArray(), Math.Max(0, selected), FieldWidth);
        int current = Math.Max(0, selected);
        reading.SelectedIndexChanged += (_, _) =>
        {
            int i = reading.SelectedIndex;
            if (i < sources.Count)
            {
                Edit(() => { t.Source = sources[i].Key; t.Sensor = null; t.Label = null; t.Max = null; });
                current = i;
                return;
            }
            if (i == names.Count - 1)
            {
                var entry = SensorPicker.Pick(this, _latest, _settings);
                if (entry == null)
                {
                    reading.SelectedIndex = current;
                    return;
                }
                Edit(() => { t.Sensor = entry.Key; t.Source = null; t.Label = null; t.Max = null; }, rebuild: true);
                SensorAdded?.Invoke();
            }
        };
        Field("Reading", reading);

        Section("Shows");
        foreach (var (visual, title) in new[]
                 {
                     (PanelVisual.Label, "Label"), (PanelVisual.Number, "Number"), (PanelVisual.Bar, "Bar"),
                     (PanelVisual.Ring, "Gauge"), (PanelVisual.Graph, "History graph"),
                 })
            Switch(title, (t.Visuals & visual) != 0, on =>
            {
                t.Visuals = on ? t.Visuals | visual : t.Visuals & ~visual;
                // A gauge already has its own way of showing the level.
                if (on && visual == PanelVisual.Ring) t.Visuals &= ~(PanelVisual.Bar | PanelVisual.Graph);
                if (on && visual is PanelVisual.Bar or PanelVisual.Graph) t.Visuals &= ~PanelVisual.Ring;
            }, rebuild: true);

        if ((t.Visuals & PanelVisual.Label) != 0)
        {
            var defaultLabel = snap != null ? PanelSources.Read(t, snap, _settings)?.Label ?? "" : "";
            TextField("Label", t.Label ?? "", defaultLabel, v => t.Label = v.Length == 0 ? null : v, "label", 24);
        }
        if ((t.Visuals & PanelVisual.Ring) != 0)
            Choice("Gauge style", new[] { "Three-quarter ring", "Semicircle", "Full circle", "Segments" }, (int)t.GaugeStyle, i => t.GaugeStyle = (PanelGaugeStyle)i);
        if ((t.Visuals & PanelVisual.Bar) != 0)
        {
            Choice("Bar style", new[] { "Solid", "Segments (LED)" }, (int)t.BarStyle, i => t.BarStyle = (PanelBarStyle)i);
            Choice("Bar direction", new[] { "Across, at the bottom", "Upwards, at the side" }, (int)t.BarDirection, i => t.BarDirection = (PanelBarDirection)i);
        }
        if ((t.Visuals & PanelVisual.Graph) != 0)
            Choice("Graph shows", new[] { "Last minute", "Last 5 minutes", "Last 15 minutes" }, Math.Max(0, Array.IndexOf(GraphTimes, t.GraphSeconds)),
                   i => t.GraphSeconds = GraphTimes[i]);
        if ((t.Visuals & (PanelVisual.Bar | PanelVisual.Ring | PanelVisual.Graph)) != 0)
            TextField("Full scale", t.Max?.ToString("0.###") ?? "", "Automatic", v =>
            {
                t.Max = float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? m : null;
            }, "max", 10);

        if ((t.Visuals & PanelVisual.Number) != 0)
        {
            Section("Number");
            Choice("Decimal places", new[] { "Automatic", "0", "1", "2", "3" }, t.Decimals is { } d ? d + 1 : 0, i => t.Decimals = i == 0 ? null : i - 1);
            Switch("Show the unit", t.ShowUnit, on => t.ShowUnit = on);
            Choice("Text size", TextSizes.Select(s => s + " %").ToArray(), Math.Max(0, Array.IndexOf(TextSizes, t.FontSize)), i => t.FontSize = TextSizes[i]);
            FontField("Font", t.FontFamily, Working.Style.FontFamily, v => t.FontFamily = v);
        }

        Section("Colours");
        Colour("Number", t.ValueColor, () => PanelColor.Parse(Working.Style.ValueColor) ?? Theme.Text, v => t.ValueColor = v);
        Colour("Bar, gauge and graph", t.AccentColor,
               () => PanelColor.Parse(Working.Style.AccentColor) ?? (snap != null ? PanelSources.Read(t, snap, _settings)?.Accent : null) ?? Theme.Cpu,
               v => t.AccentColor = v);
        BuildThresholds(t, snap);
    }

    /// <summary>Raised when a tile starts showing a new sensor, so the app starts keeping its history.</summary>
    public event Action? SensorAdded;

    void BuildThresholds(PanelTile t, Snapshot? snap)
    {
        Section("Colour steps");
        bool temp = (snap != null ? PanelSources.Read(t, snap, _settings)?.Unit : null) == PanelUnit.Temp
                    || t.Sensor?.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature
                    || t.Source is "cputemp" or "gputemp" or "igputemp" or "gpuhotspot" or "chipset" or "vrm" or "disk" || t.Source?.StartsWith("disk:") == true;
        var steps = t.Thresholds ?? (temp ? PanelColor.TemperatureDefaults() : new List<PanelThreshold>());
        Note(t.Thresholds == null
            ? temp ? "Using the usual temperature steps: amber from 65 °C, red from 82 °C." : "None: the tile keeps its colours at any value."
            : steps.Count == 0 ? "None: the tile keeps its colours at any value." : "From each value upwards, the number and bar take that colour." + (temp ? " Temperatures are in °C." : ""));
        for (int i = 0; i < steps.Count; i++)
        {
            int index = i;
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Rail, Margin = new Padding(0, 4, 0, 0) };
            var value = new InputBox(steps[i].Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "Value", 80, 10)
            {
                FieldName = $"Step {i + 1} value", Margin = new Padding(0, 0, 8, 0),
            };
            value.TextChanged += (_, _) =>
            {
                if (float.TryParse(value.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    Edit(() => Own(t)[index].Value = v, "step-" + index);
            };
            var colour = new ColorPicker(PanelColor.Parse(steps[i].Color), () => Theme.Warn, 130) { AccessibleName = $"Step {i + 1} colour", Margin = new Padding(0, 0, 8, 0) };
            colour.ValueChanged += () => Edit(() => Own(t)[index].Color = colour.Value is { } c ? PanelColor.ToHex(c) : "warn");
            var remove = new PillButton("", () => Edit(() => Own(t).RemoveAt(index), rebuild: true), 34) { Glyph = "", AccessibleName = $"Remove step {i + 1}" };
            row.Controls.Add(value);
            row.Controls.Add(colour);
            row.Controls.Add(remove);
            _props.Controls.Add(row);
        }
        Buttons(Button("Add step", () => Edit(() =>
                {
                    var own = Own(t);
                    own.Add(new PanelThreshold { Value = own.Count == 0 ? (temp ? 80 : 90) : own.Max(s => s.Value) + 10, Color = own.Count == 0 ? "warn" : "hot" });
                }, rebuild: true)),
                Button("Defaults", () => Edit(() => t.Thresholds = null, rebuild: true), "Use the default colour steps"),
                Button("None", () => Edit(() => t.Thresholds = new(), rebuild: true), "No colour steps"));
    }

    /// <summary>The tile's own steps, copied from the defaults the first time they are changed.</summary>
    static List<PanelThreshold> Own(PanelTile t) =>
        t.Thresholds ??= (t.Source is "cputemp" or "gputemp" or "igputemp" or "gpuhotspot" or "chipset" or "vrm" or "disk" ||
                          t.Source?.StartsWith("disk:") == true || t.Sensor?.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature
                              ? PanelColor.TemperatureDefaults() : new List<PanelThreshold>());

    protected override void Dispose(bool disposing)
    {
        if (disposing) Renderer.Dispose();
        base.Dispose(disposing);
    }

    // ---- Palette ------------------------------------------------------------------------------

    sealed record PaletteItem(string Name, string Glyph, Func<PanelTile> Make)
    {
        public static readonly PaletteItem[] All =
        {
            new("Number", "", () => Value("cpu", PanelVisual.Label | PanelVisual.Number)),
            new("Bar", "", () => Value("cpu", PanelVisual.Label | PanelVisual.Number | PanelVisual.Bar)),
            new("Gauge", "", () => Value("cputemp", PanelVisual.Label | PanelVisual.Number | PanelVisual.Ring, 1, 2)),
            new("Graph", "", () => Value("cpu", PanelVisual.Label | PanelVisual.Number | PanelVisual.Graph, 2)),
            new("Combined", "", () => Value("gpu", PanelVisual.Label | PanelVisual.Number | PanelVisual.Bar | PanelVisual.Graph, 2, 2)),
            new("Text", "", () => new PanelTile { Kind = PanelTileKind.Text, Text = "CPU {cputemp}", Visuals = PanelVisual.Number, Width = 160, Height = 48 }),
            new("Clock", "", () => new PanelTile { Kind = PanelTileKind.Clock, Text = "HH:mm", Visuals = PanelVisual.Number, Width = 160, Height = 64 }),
            new("Divider", "", () => new PanelTile { Kind = PanelTileKind.Divider, Visuals = PanelVisual.None, Width = 200, Height = 16 }),
            new("Picture", "", () => new PanelTile { Kind = PanelTileKind.Image, Visuals = PanelVisual.None, Width = 120, Height = 120 }),
        };

        static PanelTile Value(string source, PanelVisual visuals, int columns = 1, int rows = 1) => new()
        {
            Kind = PanelTileKind.Value, Source = source, Visuals = visuals, ColumnSpan = columns, RowSpan = rows,
            Width = 120 * columns + 6 * (columns - 1), Height = 64 * rows + 6 * (rows - 1),
        };
    }

    /// <summary>A tile type in the palette: click to add, drag onto the canvas to place.</summary>
    sealed class PaletteButton : Control
    {
        readonly PaletteItem _item;
        bool _hover, _down;
        Point _downAt;

        public PaletteButton(PaletteItem item, Action onClick)
        {
            _item = item;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Size = new Size(148, 38);
            Margin = new Padding(0, 0, 0, 4);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = $"Add a {item.Name.ToLowerInvariant()} tile";
            Click += (_, _) => onClick();
        }

        public PaletteItem Item => _item;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; _downAt = e.Location; }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_down || Math.Abs(e.X - _downAt.X) + Math.Abs(e.Y - _downAt.Y) < SystemInformation.DragSize.Width) return;
            _down = false;
            DoDragDrop(new DataObject(typeof(PaletteItem).FullName!, _item), DragDropEffects.Copy);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Rail);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Gfx.FillRounded(g, r, Ui.S(this, 6), _hover ? Theme.SurfaceHover : Theme.Surface);
            if (Focused)
                using (var path = Gfx.RoundedPath(r, Ui.S(this, 6)))
                using (var pen = new Pen(Theme.TextDim, Ui.S(this, 2)))
                    g.DrawPath(pen, path);
            var f = UiFonts.For(DeviceDpi);
            Gfx.Text(g, _item.Glyph, f.Icon, Theme.Cpu, new Rectangle(Ui.S(this, 10), 0, Ui.S(this, 22), Height), TextFormatFlags.VerticalCenter);
            Gfx.Text(g, _item.Name, f.Body, Theme.Text, new Rectangle(Ui.S(this, 40), 0, Width - Ui.S(this, 44), Height), TextFormatFlags.VerticalCenter);
        }
    }

    internal static PanelTile? Dropped(IDataObject? data) =>
        data?.GetData(typeof(PaletteItem).FullName!) is PaletteItem item ? item.Make() : null;
}

/// <summary>
/// The editor's live view of the panel: click to select, drag to move, drag an edge or corner to resize, drop
/// palette tiles on it. Grid panels snap to cells (a move onto other tiles is refused); free panels snap to
/// edges and show the guide lines they snapped to.
/// </summary>
sealed class EditorCanvas : Control
{
    readonly PanelEditorForm _editor;
    float _scale = 1;
    Point _origin;

    enum Grip { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    Grip _grip;
    Point _downAt;
    Rectangle _downCells, _downFree;
    bool _dragging;
    // What a drag (or a palette drop) would do: a block of cells or a free rectangle, and whether it's allowed.
    Rectangle? _preview;
    bool _previewOk;
    int? _guideX, _guideY;

    public EditorCanvas(PanelEditorForm editor)
    {
        _editor = editor;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AllowDrop = true;
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = "Panel layout";
    }

    PanelLayout L => _editor.Working;

    void Fit()
    {
        var natural = PanelGrid.PanelSize(L, 1);
        float dpi = DeviceDpi / 96f;
        int margin = Ui.S(this, 40);
        float room = Math.Min((Width - margin * 2) / (float)Math.Max(1, natural.Width), (Height - margin * 2) / (float)Math.Max(1, natural.Height));
        _scale = Math.Max(0.2f, Math.Min(dpi * 1.5f, room));
        var size = PanelGrid.PanelSize(L, _scale);
        _origin = new Point((Width - size.Width) / 2, (Height - size.Height) / 2);
    }

    Rectangle TileRect(int i)
    {
        var r = PanelGrid.TileRect(L, L.Tiles[i], _scale);
        r.Offset(_origin);
        return r;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        Fit();
        var size = PanelGrid.PanelSize(L, _scale);
        int pad = Ui.S(this, 20);
        PanelBackdrop.Paint(g, new Rectangle(_origin.X - pad, _origin.Y - pad, size.Width + pad * 2, size.Height + pad * 2), Ui.S(this, 10));

        var frame = _editor.Renderer.Prepare(_editor.Latest, DateTime.Now);
        var state = g.Save();
        _editor.Renderer.Draw(g, frame, ClientSize, _scale, _origin, false);
        g.Restore(state);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Empty cells, so there is something to drop onto.
        if (!L.FreePlacement)
            using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), 1) { DashStyle = DashStyle.Dot })
                for (int row = 0; row < L.Rows; row++)
                    for (int col = 0; col < L.Columns; col++)
                    {
                        var cells = new Rectangle(col, row, 1, 1);
                        if (L.Tiles.Any(t => t.Cells.IntersectsWith(cells))) continue;
                        var r = PanelGrid.CellRect(L, cells, _scale);
                        r.Offset(_origin);
                        g.DrawRectangle(pen, r);
                    }

        // Empty picture tiles and dividers are invisible on their own; outline every tile faintly.
        using (var faint = new Pen(Color.FromArgb(60, 255, 255, 255)) { DashStyle = DashStyle.Dash })
            for (int i = 0; i < L.Tiles.Count; i++)
                if (L.Tiles[i].Kind is PanelTileKind.Image or PanelTileKind.Divider) g.DrawRectangle(faint, TileRect(i));

        if (_editor.Selected >= 0 && _editor.Selected < L.Tiles.Count)
        {
            var sel = TileRect(_editor.Selected);
            using (var pen = new Pen(Theme.Cpu, Math.Max(2, Ui.S(this, 2))))
                g.DrawRectangle(pen, Rectangle.Inflate(sel, 1, 1));
            using var handle = new SolidBrush(Theme.Cpu);
            foreach (var h in Handles(sel)) g.FillRectangle(handle, h.Rect);
        }

        if (_preview is { } p)
        {
            var r = L.FreePlacement ? FreeToScreen(p) : ScreenOf(p);
            var c = _previewOk ? Theme.Good : Theme.Hot;
            using (var fill = new SolidBrush(Theme.WithAlpha(c, 40))) g.FillRectangle(fill, r);
            using (var pen = new Pen(c, Math.Max(2, Ui.S(this, 2))) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r);
        }
        using (var guide = new Pen(Theme.Warn, 1) { DashStyle = DashStyle.Dash })
        {
            if (_guideX is { } gx) g.DrawLine(guide, _origin.X + gx * _scale, _origin.Y, _origin.X + gx * _scale, _origin.Y + size.Height);
            if (_guideY is { } gy) g.DrawLine(guide, _origin.X, _origin.Y + gy * _scale, _origin.X + size.Width, _origin.Y + gy * _scale);
        }

        if (L.Tiles.Count == 0)
            Gfx.Text(g, "Drag a tile here from the left", UiFonts.For(DeviceDpi).Body, Theme.Text,
                     new Rectangle(_origin, size), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    Rectangle ScreenOf(Rectangle cells)
    {
        var r = PanelGrid.CellRect(L, cells, _scale);
        r.Offset(_origin);
        return r;
    }

    Rectangle FreeToScreen(Rectangle free) => new(_origin.X + (int)Math.Round(free.X * _scale), _origin.Y + (int)Math.Round(free.Y * _scale),
                                                  (int)Math.Round(free.Width * _scale), (int)Math.Round(free.Height * _scale));

    IEnumerable<(Grip Grip, Rectangle Rect)> Handles(Rectangle r)
    {
        int s = Math.Max(6, Ui.S(this, 7)), h = s / 2;
        Rectangle At(int x, int y) => new(x - h, y - h, s, s);
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        yield return (Grip.TopLeft, At(r.Left, r.Top));
        yield return (Grip.Top, At(cx, r.Top));
        yield return (Grip.TopRight, At(r.Right, r.Top));
        yield return (Grip.Right, At(r.Right, cy));
        yield return (Grip.BottomRight, At(r.Right, r.Bottom));
        yield return (Grip.Bottom, At(cx, r.Bottom));
        yield return (Grip.BottomLeft, At(r.Left, r.Bottom));
        yield return (Grip.Left, At(r.Left, cy));
    }

    Grip GripAt(Point p)
    {
        if (_editor.Selected < 0 || _editor.Selected >= L.Tiles.Count) return Grip.None;
        var sel = TileRect(_editor.Selected);
        foreach (var (grip, rect) in Handles(sel))
            if (Rectangle.Inflate(rect, 3, 3).Contains(p)) return grip;
        return sel.Contains(p) ? Grip.Move : Grip.None;
    }

    static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.Move => Cursors.SizeAll,
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Default,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var grip = GripAt(e.Location);
        if (grip == Grip.None)
        {
            int hit = PanelGrid.HitTest(L, new Point(e.X - _origin.X, e.Y - _origin.Y), _scale);
            _editor.Select(hit);
            grip = hit >= 0 ? Grip.Move : Grip.None;
        }
        if (e.Button == MouseButtons.Right && _editor.Selected >= 0)
        {
            TileMenu().Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left || grip == Grip.None) return;
        _grip = grip;
        _downAt = e.Location;
        var t = L.Tiles[_editor.Selected];
        _downCells = t.Cells;
        _downFree = new Rectangle(t.X, t.Y, t.Width, t.Height);
        _dragging = false;
    }

    ContextMenuStrip TileMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, BackColor = Theme.Surface };
        void Add(string text, Keys keys)
        {
            var item = new ToolStripMenuItem(text) { ForeColor = Theme.Text };
            item.Click += (_, _) => _editor.CanvasKey(new KeyEventArgs(keys));
            menu.Items.Add(item);
        }
        Add("Delete", Keys.Delete);
        var dup = new ToolStripMenuItem("Duplicate") { ForeColor = Theme.Text, ShortcutKeyDisplayString = "Ctrl+D" };
        dup.Click += (_, _) => _editor.Duplicate();
        menu.Items.Add(dup);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        return menu;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_grip == Grip.None)
        {
            Cursor = CursorFor(GripAt(e.Location));
            return;
        }
        int dx = e.X - _downAt.X, dy = e.Y - _downAt.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < SystemInformation.DragSize.Width) return;
        _dragging = true;
        int index = _editor.Selected;
        if (L.FreePlacement)
        {
            var (r, gx, gy) = FreeTarget(index, dx / _scale, dy / _scale);
            (_preview, _previewOk, _guideX, _guideY) = (r, true, gx, gy);
        }
        else
        {
            var cells = GridTarget(dx, dy);
            (_preview, _previewOk) = (cells, PanelGrid.Fits(L, index, cells));
        }
        Invalidate();
    }

    /// <summary>Grid: the cells a drag would give, from how far it has moved in whole cells.</summary>
    Rectangle GridTarget(int dx, int dy)
    {
        int cols = (int)Math.Round(dx / ((L.CellWidth + L.Gap) * _scale));
        int rows = (int)Math.Round(dy / ((L.CellHeight + L.Gap) * _scale));
        var c = _downCells;
        int left = c.Left, top = c.Top, right = c.Right, bottom = c.Bottom;
        switch (_grip)
        {
            case Grip.Move: left += cols; right += cols; top += rows; bottom += rows; break;
            default:
                if (_grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft) left = Math.Min(left + cols, right - 1);
                if (_grip is Grip.Right or Grip.TopRight or Grip.BottomRight) right = Math.Max(right + cols, left + 1);
                if (_grip is Grip.Top or Grip.TopLeft or Grip.TopRight) top = Math.Min(top + rows, bottom - 1);
                if (_grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) bottom = Math.Max(bottom + rows, top + 1);
                break;
        }
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>Free placement: the rectangle a drag would give, snapped to guides, and the guides used.</summary>
    (Rectangle Rect, int? GuideX, int? GuideY) FreeTarget(int index, float dx, float dy)
    {
        var (xs, ys) = PanelGrid.Guides(L, index);
        int threshold = (int)Math.Ceiling(6 / _scale);
        var d = _downFree;
        int left = d.Left, top = d.Top, right = d.Right, bottom = d.Bottom;
        int? gx = null, gy = null;
        if (_grip == Grip.Move)
        {
            int nx = (int)Math.Round(d.X + dx), ny = (int)Math.Round(d.Y + dy);
            // Snap whichever edge is closer to a guide.
            var (sl, gl) = PanelGrid.Snap(nx, xs, threshold);
            var (sr, gr) = PanelGrid.Snap(nx + d.Width, xs, threshold);
            if (gl != null) { nx = sl; gx = gl; } else if (gr != null) { nx = sr - d.Width; gx = gr; }
            var (st, gt) = PanelGrid.Snap(ny, ys, threshold);
            var (sb, gb) = PanelGrid.Snap(ny + d.Height, ys, threshold);
            if (gt != null) { ny = st; gy = gt; } else if (gb != null) { ny = sb - d.Height; gy = gb; }
            nx = Math.Clamp(nx, 0, Math.Max(0, L.Width - d.Width));
            ny = Math.Clamp(ny, 0, Math.Max(0, L.Height - d.Height));
            return (new Rectangle(nx, ny, d.Width, d.Height), gx, gy);
        }
        int min = PanelGrid.MinFreeSize;
        if (_grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft)
            (left, gx) = Snapped(Math.Clamp((int)Math.Round(left + dx), 0, right - min), xs, threshold);
        if (_grip is Grip.Right or Grip.TopRight or Grip.BottomRight)
            (right, gx) = Snapped(Math.Clamp((int)Math.Round(right + dx), left + min, L.Width), xs, threshold);
        if (_grip is Grip.Top or Grip.TopLeft or Grip.TopRight)
            (top, gy) = Snapped(Math.Clamp((int)Math.Round(top + dy), 0, bottom - min), ys, threshold);
        if (_grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight)
            (bottom, gy) = Snapped(Math.Clamp((int)Math.Round(bottom + dy), top + min, L.Height), ys, threshold);
        return (Rectangle.FromLTRB(left, top, right, bottom), gx, gy);
    }

    static (int, int?) Snapped(int value, List<int> guides, int threshold)
    {
        var (v, g) = PanelGrid.Snap(value, guides, threshold);
        return (v, g);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var grip = _grip;
        _grip = Grip.None;
        if (!_dragging || grip == Grip.None || _preview is not { } target)
        {
            ClearPreview();
            return;
        }
        _dragging = false;
        int index = _editor.Selected;
        if (L.FreePlacement)
        {
            var t = L.Tiles[index];
            _editor.Edit(() => (t.X, t.Y, t.Width, t.Height) = (target.X, target.Y, target.Width, target.Height));
        }
        else if (_previewOk)
            _editor.Edit(() => PanelGrid.Place(L, index, target));
        else
            System.Media.SystemSounds.Beep.Play();
        _editor.PositionChanged();
        ClearPreview();
    }

    void ClearPreview()
    {
        _preview = null;
        _guideX = _guideY = null;
        Invalidate();
    }

    // Palette drops.

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = PanelEditorForm.Dropped(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    Rectangle? DropTarget(DragEventArgs e, PanelTile tile)
    {
        var p = PointToClient(new Point(e.X, e.Y));
        if (L.FreePlacement)
        {
            int x = (int)Math.Round((p.X - _origin.X) / _scale - tile.Width / 2f), y = (int)Math.Round((p.Y - _origin.Y) / _scale - tile.Height / 2f);
            return new Rectangle(Math.Clamp(x, 0, Math.Max(0, L.Width - tile.Width)), Math.Clamp(y, 0, Math.Max(0, L.Height - tile.Height)),
                                 Math.Min(tile.Width, L.Width), Math.Min(tile.Height, L.Height));
        }
        var cell = PanelGrid.CellAt(L, new Point(p.X - _origin.X, p.Y - _origin.Y), _scale);
        return new Rectangle(cell.X, cell.Y, Math.Min(tile.ColumnSpan, L.Columns), tile.RowSpan);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (PanelEditorForm.Dropped(e.Data) is not { } tile) return;
        e.Effect = DragDropEffects.Copy;
        _preview = DropTarget(e, tile);
        _previewOk = L.FreePlacement || (_preview is { } r && PanelGrid.Fits(L, -1, r));
        Invalidate();
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        ClearPreview();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (PanelEditorForm.Dropped(e.Data) is not { } tile) return;
        var target = DropTarget(e, tile);
        ClearPreview();
        // A drop on taken cells still adds the tile, in the first free spot.
        _editor.AddTile(tile, target);
    }

    // Keyboard.

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Tab || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyCode == Keys.Tab && e.Control) || e.Alt) return;
        _editor.CanvasKey(e);
        if (e.Handled) e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    // Screen readers: one child per tile.

    protected override AccessibleObject CreateAccessibilityInstance() => new CanvasAccessible(this);

    internal new void AccessibilityNotifyClients(AccessibleEvents e, int child)
    {
        if (IsHandleCreated) base.AccessibilityNotifyClients(e, child);
    }

    sealed class CanvasAccessible(EditorCanvas owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.L.Tiles.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner.L.Tiles.Count ? new TileAccessible(owner, index, this) : null;
        public override AccessibleObject? GetFocused() => owner._editor.Selected >= 0 ? GetChild(owner._editor.Selected) : this;
        public override string? Value { get => $"{owner.L.Tiles.Count} tiles. Tab moves between them; arrow keys move the selected tile, Shift and arrows resize it."; set { } }
    }

    sealed class TileAccessible(EditorCanvas owner, int index, AccessibleObject parent) : AccessibleObject
    {
        public override AccessibleObject Parent => parent;
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override string Name
        {
            get
            {
                var t = owner.L.Tiles[index];
                var what = t.Kind switch
                {
                    PanelTileKind.Value => (owner._editor.Latest is { } snap ? PanelSources.Read(t, snap, owner._editor.Settings)?.Label : null) ?? t.Source ?? "reading",
                    PanelTileKind.Text => "text: " + t.Text,
                    PanelTileKind.Clock => "clock",
                    PanelTileKind.Image => "picture",
                    _ => "divider",
                };
                var where = owner.L.FreePlacement ? $"at {t.X}, {t.Y}, {t.Width} by {t.Height}" : $"column {t.Column + 1}, row {t.Row + 1}, {t.ColumnSpan} by {t.RowSpan} cells";
                return $"{what} tile, {where}";
            }
        }
        public override AccessibleStates State => AccessibleStates.Selectable | AccessibleStates.Focusable |
                                                   (owner._editor.Selected == index ? AccessibleStates.Selected | AccessibleStates.Focused : AccessibleStates.None);
        public override Rectangle Bounds => owner.RectangleToScreen(owner.TileRect(index));
        public override void Select(AccessibleSelection flags) => owner._editor.Select(index);
        public override void DoDefaultAction() => owner._editor.Select(index);
    }
}
