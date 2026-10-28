using System.Drawing.Drawing2D;

namespace PCVitals;

// Owner-drawn controls for the Settings window.
// They read Theme colours at paint time; the window is rebuilt when the theme changes.

static class Ui
{
    public static float Scale(Control c) => c.DeviceDpi / 96f;
    public static int S(Control c, float v) => (int)Math.Round(v * Scale(c));
}

/// <summary>Windows 11 style on/off switch.</summary>
sealed class ToggleSwitch : Control
{
    bool _checked;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessible(this);

    /// <summary>Reports as a check box (on/off) to screen readers.</summary>
    sealed class ToggleAccessible : ControlAccessibleObject
    {
        readonly ToggleSwitch _owner;
        public ToggleAccessible(ToggleSwitch owner) : base(owner) => _owner = owner;
        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override AccessibleStates State => base.State | (_owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
        public override string DefaultAction => _owner.Checked ? "Turn off" : "Turn on";
        public override void DoDefaultAction() => _owner.Checked = !_owner.Checked;
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Size = new Size(42, 22);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        bool on = _checked && Enabled;
        using (var path = Gfx.RoundedPath(r, r.Height / 2))
        {
            if (on)
            {
                using var fill = new SolidBrush(Theme.Cpu);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(Enabled ? Theme.TextDim : Theme.Border, Math.Max(1, Ui.Scale(this)));
                g.DrawPath(pen, path);
            }
        }
        int knob = r.Height - Ui.S(this, 8);
        int kx = _checked ? r.Right - knob - Ui.S(this, 4) : r.Left + Ui.S(this, 4);
        using var kb = new SolidBrush(on ? (Theme.IsLight ? Color.White : Theme.Bg) : Enabled ? Theme.TextDim : Theme.Border);
        g.FillEllipse(kb, kx, r.Top + (r.Height - knob) / 2f, knob, knob);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Text) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Enabled) Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && Enabled) Checked = !Checked;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }
}

/// <summary>A settings row: optional icon, title, wrapped description, and a control on the right.</summary>
sealed class Card : Control
{
    readonly string _title;
    readonly string? _description;
    readonly string? _glyph;
    readonly Control? _accessory;

    public Card(string title, string? description = null, Control? accessory = null, string? glyph = null)
    {
        _title = title;
        _description = description;
        _glyph = glyph;
        _accessory = accessory;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Size = new Size(600, 64);
        Margin = new Padding(0, 0, 0, 4);
        Tag = "stretch";
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = title;
        AccessibleDescription = description;
        if (accessory != null)
        {
            // Screen readers announce the setting's title on its control (unless it already has a name).
            foreach (var c in accessory is AccessoryRow row ? row.Controls.Cast<Control>() : new[] { accessory })
                c.AccessibleName ??= title;
            Controls.Add(accessory);
        }
    }

    bool _placeholder;

    /// <summary>Drawn as an empty dashed slot while the card itself is being dragged (<see cref="CardDragger"/>).</summary>
    public bool Placeholder
    {
        get => _placeholder;
        set
        {
            if (_placeholder == value) return;
            _placeholder = value;
            if (_accessory != null) _accessory.Visible = !value;
            Invalidate();
        }
    }

    int TextLeft => Ui.S(this, _glyph != null ? 56 : 18);

    int AccessoryWidth => _accessory?.Width ?? 0;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = Ui.S(this, 18);
        var f = UiFonts.For(DeviceDpi);
        int textW = Math.Max(50, Width - TextLeft - pad - AccessoryWidth - (AccessoryWidth > 0 ? pad : 0));
        int h = Ui.S(this, 20);
        if (_description != null)
            h += Ui.S(this, 3) + TextRenderer.MeasureText(_description, f.Small, new Size(textW, int.MaxValue),
                                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        int want = Math.Max(Ui.S(this, 64), Math.Max(h, (_accessory?.Height ?? 0)) + pad * 2 - Ui.S(this, 6));
        if (Height != want) Height = want;
        if (_accessory != null)
            _accessory.Location = new Point(Width - pad - _accessory.Width, (Height - _accessory.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        if (_placeholder)
        {
            // Where the dragged card will land.
            var slot = new Rectangle(1, 1, Width - 3, Height - 3);
            Gfx.FillRounded(g, slot, Ui.S(this, 8), Theme.WithAlpha(Theme.Cpu, 18));
            using var path = Gfx.RoundedPath(slot, Ui.S(this, 8));
            using var dash = new Pen(Theme.WithAlpha(Theme.Cpu, 150), Math.Max(1.5f, Ui.S(this, 1.5f))) { DashStyle = DashStyle.Dash };
            g.DrawPath(dash, path);
            return;
        }
        Gfx.FillRounded(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(this, 8), Theme.Surface);
        var f = UiFonts.For(DeviceDpi);
        int pad = Ui.S(this, 18);
        if (_glyph != null)
            Gfx.Text(g, _glyph, f.IconLarge, Theme.TextDim, new Rectangle(Ui.S(this, 16), 0, Ui.S(this, 28), Height),
                     TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        int textW = Math.Max(50, Width - TextLeft - pad - AccessoryWidth - (AccessoryWidth > 0 ? pad : 0));
        int descH = _description == null ? 0 : TextRenderer.MeasureText(g, _description, f.Small, new Size(textW, int.MaxValue),
                                                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        int blockH = Ui.S(this, 20) + (descH > 0 ? Ui.S(this, 3) + descH : 0);
        int y = (Height - blockH) / 2;
        Gfx.Text(g, _title, f.Body, Enabled ? Theme.Text : Theme.TextFaint, new Rectangle(TextLeft, y, textW, Ui.S(this, 20)), TextFormatFlags.EndEllipsis);
        if (_description != null)
            TextRenderer.DrawText(g, _description, f.Small, new Rectangle(TextLeft, y + Ui.S(this, 23), textW, descH + 2), Theme.TextDim,
                                  TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// Dropdown drawn like a Windows 11 combo box; the list opens as a themed menu. (A stock ComboBox
/// keeps a system-coloured border and arrow that can't be themed.)
/// </summary>
sealed class Dropdown : Control
{
    readonly string[] _items;
    int _selected;
    bool _hover;

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, _items.Length - 1));
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new DropdownAccessible(this);

    sealed class DropdownAccessible : ControlAccessibleObject
    {
        readonly Dropdown _owner;
        public DropdownAccessible(Dropdown owner) : base(owner) => _owner = owner;
        public override AccessibleRole Role => AccessibleRole.ComboBox;
        public override string? Value
        {
            get => _owner._items.Length > 0 ? _owner._items[_owner._selected] : null;
            set { }
        }
        public override string DefaultAction => "Open";
        public override void DoDefaultAction() => _owner.OnClick(EventArgs.Empty);
    }

    public Dropdown(string[] items, int selected, int width)
    {
        _items = items;
        _selected = Math.Clamp(selected, 0, Math.Max(0, items.Length - 1));
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Size = new Size(width, 32);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, r, Ui.S(this, 6), _hover ? Theme.Border : Theme.SurfaceHover);
        using (var path = Gfx.RoundedPath(r, Ui.S(this, 6)))
        using (var pen = new Pen(Focused ? Theme.Cpu : Theme.Border))
            g.DrawPath(pen, path);
        var f = UiFonts.For(DeviceDpi);
        int chevronW = Ui.S(this, 28);
        Gfx.Text(g, _items.Length > 0 ? _items[_selected] : "", f.Body, Enabled ? Theme.Text : Theme.TextFaint,
                 new Rectangle(Ui.S(this, 12), 0, Width - Ui.S(this, 12) - chevronW, Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Gfx.Text(g, "", f.Icon, Theme.TextDim, new Rectangle(Width - chevronW - Ui.S(this, 4), 0, chevronW, Height),
                 TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        Focus();
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true, BackColor = Theme.Surface };
        for (int i = 0; i < _items.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected, ForeColor = Theme.Text };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }
        menu.MinimumSize = new Size(Width, 0);
        menu.Closed += (_, _) =>
        {
            // The window may be closing (or rebuilding after a theme change) while the list is open.
            if (IsHandleCreated && !IsDisposed) BeginInvoke(menu.Dispose);
            else menu.Dispose();
        };
        menu.Show(this, new Point(0, Height + Ui.S(this, 2)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Down or Keys.Right) SelectedIndex++;
        else if (e.KeyCode is Keys.Up or Keys.Left) SelectedIndex--;
        else if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
}

/// <summary>Rounded push button; <see cref="Accent"/> makes it the primary action.</summary>
sealed class PillButton : Control
{
    bool _hover, _down;
    public bool Accent { get; set; }
    public string? Glyph { get; set; }

    public PillButton(string text, Action onClick, int width = 0)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        var f = UiFonts.For(96);
        Size = new Size(width > 0 ? width : TextRenderer.MeasureText(text, f.Body).Width + 32, 32);
        AccessibleRole = AccessibleRole.PushButton;
        if (!string.IsNullOrEmpty(text)) AccessibleName = text;
        Click += (_, _) => onClick();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var bg = Accent ? (_down ? ControlPaint.Dark(Theme.Cpu, 0.1f) : _hover ? ControlPaint.Light(Theme.Cpu, 0.15f) : Theme.Cpu)
                        : _down ? Theme.Border : _hover ? Theme.SurfaceHover : Theme.Bg;
        Gfx.FillRounded(g, r, Ui.S(this, 6), bg);
        if (!Accent)
        {
            using var path = Gfx.RoundedPath(r, Ui.S(this, 6));
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }
        var f = UiFonts.For(DeviceDpi);
        var fg = Accent ? (Theme.IsLight ? Color.White : Theme.Bg) : Enabled ? Theme.Text : Theme.TextFaint;
        Gfx.Text(g, Glyph ?? Text, Glyph != null ? f.Icon : f.Body, fg, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }
}

/// <summary>Borderless text box on a rounded field with an accent underline while focused.</summary>
sealed class InputBox : Control
{
    readonly TextBox _box;

    public InputBox(string text, string placeholder, int width, int maxLength)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(width, 32);
        _box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceHover,
            ForeColor = Theme.Text,
            MaxLength = maxLength,
        };
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_box);
    }

    public new event EventHandler? TextChanged;
    public string Value
    {
        get => _box.Text;
        set => _box.Text = value;
    }
    /// <summary>Raised for keys pressed in the text field (e.g. to move a list below it with the arrows).</summary>
    public event KeyEventHandler? FieldKeyDown
    {
        add => _box.KeyDown += value;
        remove => _box.KeyDown -= value;
    }

    public void FocusText()
    {
        _box.Focus();
        _box.SelectAll();
    }

    /// <summary>Name screen readers announce for the text field.</summary>
    public string? FieldName
    {
        get => _box.AccessibleName;
        set => _box.AccessibleName = value;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_box == null) return; // Size is set in the constructor before the inner box exists
        int pad = Ui.S(this, 10);
        _box.Width = Width - pad * 2;
        _box.Location = new Point(pad, (Height - _box.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, r, Ui.S(this, 6), Theme.SurfaceHover);
        using (var pen = new Pen(_box.Focused ? Theme.Cpu : Theme.Border, _box.Focused ? Math.Max(2, Ui.Scale(this) * 1.5f) : 1))
            g.DrawLine(pen, Ui.S(this, 4), Height - 2, Width - Ui.S(this, 5), Height - 2);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _box.Focus();
    }
}

/// <summary>Holds several accessory controls side by side inside a card.</summary>
sealed class AccessoryRow : FlowLayoutPanel
{
    public AccessoryRow(params Control[] controls)
    {
        FlowDirection = FlowDirection.LeftToRight;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Surface;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        foreach (var c in controls)
        {
            c.Margin = new Padding(8, 0, 0, 0);
            Controls.Add(c);
        }
    }
}

/// <summary>Sidebar navigation with icons.</summary>
sealed class NavList : Control
{
    readonly List<(string Key, string Text, string Glyph)> _items = new();
    int _hover = -1;
    public string Selected { get; private set; } = "";
    public event Action<string>? SelectionChanged;

    public NavList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Rail;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = "Settings pages";
    }

    // Keyboard: Up/Down (or Home/End) move between pages while the sidebar has focus.
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int i = _items.FindIndex(x => x.Key == Selected);
        int next = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, i - 1),
            Keys.Down => Math.Min(_items.Count - 1, i + 1),
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => i,
        };
        if (next != i && next >= 0)
        {
            Select(_items[next].Key);
            AccessibilityNotifyClients(AccessibleEvents.Focus, next);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    sealed class NavAccessible : ControlAccessibleObject
    {
        readonly NavList _owner;
        public NavAccessible(NavList owner) : base(owner) => _owner = owner;
        public override AccessibleRole Role => AccessibleRole.List;
        public override int GetChildCount() => _owner._items.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < _owner._items.Count ? new Item(_owner, index, this) : null;
        public override AccessibleObject? GetFocused() => GetChild(_owner._items.FindIndex(x => x.Key == _owner.Selected));

        sealed class Item : AccessibleObject
        {
            readonly NavList _list;
            readonly int _index;
            readonly AccessibleObject _parent;
            public Item(NavList list, int index, AccessibleObject parent) { _list = list; _index = index; _parent = parent; }
            public override string? Name => _list._items[_index].Text;
            public override AccessibleRole Role => AccessibleRole.ListItem;
            public override AccessibleObject? Parent => _parent;
            public override AccessibleStates State => AccessibleStates.Selectable |
                (_list._items[_index].Key == _list.Selected ? AccessibleStates.Selected | AccessibleStates.Focused : AccessibleStates.None);
            public override Rectangle Bounds => _list.RectangleToScreen(new Rectangle(0, _list.ItemTop(_index), _list.Width, Ui.S(_list, 36)));
            public override void DoDefaultAction() => _list.Select(_list._items[_index].Key);
        }
    }

    public void Add(string key, string text, string glyph) => _items.Add((key, text, glyph));

    public void Select(string key)
    {
        if (Selected == key) return;
        Selected = key;
        Invalidate();
        SelectionChanged?.Invoke(key);
    }

    int ItemTop(int i) => Ui.S(this, 96) + i * Ui.S(this, 40);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Rail);
        var f = UiFonts.For(DeviceDpi);

        // App identity block.
        var logo = new Rectangle(Ui.S(this, 20), Ui.S(this, 26), Ui.S(this, 36), Ui.S(this, 36));
        using (var path = Gfx.RoundedPath(logo, Ui.S(this, 8)))
        using (var bg = new LinearGradientBrush(logo, Color.FromArgb(70, 140, 255), Color.FromArgb(120, 80, 240), 45f))
            g.FillPath(bg, path);
        float s = logo.Width;
        var pts = new[]
        {
            new PointF(logo.Left + s * 0.16f, logo.Top + s * 0.56f), new PointF(logo.Left + s * 0.36f, logo.Top + s * 0.56f),
            new PointF(logo.Left + s * 0.45f, logo.Top + s * 0.30f), new PointF(logo.Left + s * 0.58f, logo.Top + s * 0.76f),
            new PointF(logo.Left + s * 0.67f, logo.Top + s * 0.52f), new PointF(logo.Left + s * 0.84f, logo.Top + s * 0.52f),
        };
        using (var pen = new Pen(Color.White, Math.Max(1.5f, s / 12)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, pts);
        Gfx.Text(g, "PC Vitals", f.Title, Theme.Text, new Rectangle(logo.Right + Ui.S(this, 12), logo.Top - Ui.S(this, 1), Width, Ui.S(this, 22)));
        Gfx.Text(g, "Settings", f.Small, Theme.TextDim, new Rectangle(logo.Right + Ui.S(this, 12), logo.Top + Ui.S(this, 20), Width, Ui.S(this, 18)));

        for (int i = 0; i < _items.Count; i++)
        {
            var (key, text, glyph) = _items[i];
            var r = new Rectangle(Ui.S(this, 10), ItemTop(i), Width - Ui.S(this, 20), Ui.S(this, 36));
            bool sel = key == Selected;
            if (sel || i == _hover) Gfx.FillRounded(g, r, Ui.S(this, 6), sel ? Theme.SurfaceHover : Theme.Surface);
            if (sel)
            {
                using var bar = new SolidBrush(Theme.Cpu);
                using var barPath = Gfx.RoundedPath(new Rectangle(r.Left, r.Top + Ui.S(this, 9), Ui.S(this, 3), r.Height - Ui.S(this, 18)), Ui.S(this, 1));
                g.FillPath(bar, barPath);
            }
            Gfx.Text(g, glyph, f.Icon, sel ? Theme.Cpu : Theme.TextDim, new Rectangle(r.Left + Ui.S(this, 12), r.Top, Ui.S(this, 22), r.Height),
                     TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            Gfx.Text(g, text, sel ? f.BodyBold : f.Body, Theme.Text, new Rectangle(r.Left + Ui.S(this, 44), r.Top, r.Width - Ui.S(this, 48), r.Height),
                     TextFormatFlags.VerticalCenter);
            if (sel && Focused && ShowFocusCues)
            {
                using var path = Gfx.RoundedPath(Rectangle.Inflate(r, -1, -1), Ui.S(this, 6));
                using var pen = new Pen(Theme.Text, Math.Max(1.5f, Ui.Scale(this) * 1.5f));
                g.DrawPath(pen, path);
            }
        }
    }

    int Hit(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
            if (p.Y >= ItemTop(i) && p.Y < ItemTop(i) + Ui.S(this, 36)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = Hit(e.Location);
        if (h == _hover) return;
        _hover = h;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int h = Hit(e.Location);
        if (h >= 0) Select(_items[h].Key);
    }
}

/// <summary>A selectable swatch showing what a theme looks like.</summary>
sealed class ThemeSwatch : Control
{
    readonly Palette? _palette;
    readonly string _name;
    bool _hover;
    public bool Selected { get; set; }

    public ThemeSwatch(string name, Palette? palette, Action onClick)
    {
        _name = name;
        _palette = palette;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Size = new Size(140, 118);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 12, 0);
        TabStop = true;
        AccessibleRole = AccessibleRole.RadioButton;
        AccessibleName = name + " theme";
        Click += (_, _) => onClick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var f = UiFonts.For(DeviceDpi);
        var preview = new Rectangle(Ui.S(this, 2), Ui.S(this, 2), Width - Ui.S(this, 5), Height - Ui.S(this, 32));

        void Mock(Rectangle r, Palette p)
        {
            Gfx.FillRounded(g, r, Ui.S(this, 6), p.Bg);
            var card = Rectangle.Inflate(r, -Ui.S(this, 10), -Ui.S(this, 12));
            Gfx.FillRounded(g, card, Ui.S(this, 4), p.Surface);
            using var accent = new SolidBrush(p.Cpu);
            g.FillRectangle(accent, card.Left + Ui.S(this, 8), card.Top + Ui.S(this, 9), card.Width / 3, Ui.S(this, 5));
            using var text = new SolidBrush(p.TextDim);
            g.FillRectangle(text, card.Left + Ui.S(this, 8), card.Top + Ui.S(this, 20), card.Width * 2 / 3, Ui.S(this, 4));
            g.FillRectangle(text, card.Left + Ui.S(this, 8), card.Top + Ui.S(this, 29), card.Width / 2, Ui.S(this, 4));
        }

        if (_palette != null)
        {
            Mock(preview, _palette);
        }
        else
        {
            // "System": half light, half dark.
            var state = g.Save();
            g.SetClip(new Rectangle(preview.Left, preview.Top, preview.Width / 2, preview.Height));
            Mock(preview, Theme.LightPalette);
            g.Restore(state);
            state = g.Save();
            g.SetClip(new Rectangle(preview.Left + preview.Width / 2, preview.Top, preview.Width - preview.Width / 2, preview.Height));
            Mock(preview, Theme.DarkPalette);
            g.Restore(state);
        }
        using (var path = Gfx.RoundedPath(preview, Ui.S(this, 6)))
        using (var pen = new Pen(Selected ? Theme.Cpu : _hover || Focused ? Theme.TextDim : Theme.Border, Selected || Focused ? Ui.S(this, 2) : 1))
            g.DrawPath(pen, path);
        Gfx.Text(g, _name, Selected ? f.BodyBold : f.Body, Theme.Text, new Rectangle(0, preview.Bottom + Ui.S(this, 6), Width, Ui.S(this, 22)),
                 TextFormatFlags.HorizontalCenter);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
}

/// <summary>Live render of something (taskbar strip, tray icons) on a taskbar-coloured backdrop.</summary>
sealed class LivePreview : Control
{
    readonly Func<Control, (Bitmap? Image, string? Note)> _render;

    public LivePreview(Func<Control, (Bitmap? Image, string? Note)> render, int height)
    {
        _render = render;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(600, height);
        Margin = new Padding(0, 0, 0, 12);
        Tag = "stretch";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var f = UiFonts.For(DeviceDpi);
        var (image, note) = _render(this);
        int noteH = note != null ? Ui.S(this, 40) : 0;
        // Fit the box to what it shows (plus a note), instead of a fixed height with empty bands.
        if (image != null)
        {
            float fit = Math.Min(1f, (Width - 1 - Ui.S(this, 24)) / (float)image.Width);
            int want = (int)(image.Height * fit) + Ui.S(this, 32) + noteH + 1;
            if (want != Height) BeginInvoke(() => Height = want);
        }
        var area = new Rectangle(0, 0, Width - 1, Height - 1 - noteH);
        bool light = Theme.TaskbarIsLight();
        Gfx.FillRounded(g, area, Ui.S(this, 8), light ? Color.FromArgb(238, 238, 242) : Color.FromArgb(28, 30, 36));
        using (var path = Gfx.RoundedPath(area, Ui.S(this, 8)))
        using (var pen = new Pen(Theme.Border))
            g.DrawPath(pen, path);
        if (image != null)
        {
            using (image)
            {
                float k = Math.Min(1f, (area.Width - Ui.S(this, 24)) / (float)image.Width);
                int w = (int)(image.Width * k), h = (int)(image.Height * k);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
            }
        }
        else
        {
            Gfx.Text(g, "Waiting for the first reading…", f.Small, Theme.TextDim, area, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (note != null)
        {
            var nr = new Rectangle(0, area.Bottom + Ui.S(this, 6), Width, noteH - Ui.S(this, 6));
            Gfx.Text(g, "", f.Icon, Theme.Warn, new Rectangle(nr.Left, nr.Top, Ui.S(this, 22), Ui.S(this, 20)));
            TextRenderer.DrawText(g, note, f.Small, new Rectangle(nr.Left + Ui.S(this, 26), nr.Top + Ui.S(this, 2), nr.Width - Ui.S(this, 26), nr.Height),
                                  Theme.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Radio-style card for choosing the primary GPU.</summary>
sealed class ChoiceCard : Control
{
    readonly Func<(string Title, string? Detail, string? Tag)> _content;
    bool _hover;
    public bool Selected { get; set; }

    public ChoiceCard(Func<(string Title, string? Detail, string? Tag)> content, Action onClick)
    {
        _content = content;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(600, 60);
        Margin = new Padding(0, 0, 0, 4);
        Cursor = Cursors.Hand;
        Tag = "stretch";
        TabStop = true;
        AccessibleRole = AccessibleRole.RadioButton;
        Click += (_, _) => onClick();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ChoiceAccessible(this);

    sealed class ChoiceAccessible : ControlAccessibleObject
    {
        readonly ChoiceCard _owner;
        public ChoiceAccessible(ChoiceCard owner) : base(owner) => _owner = owner;
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override string? Name
        {
            get
            {
                var (title, detail, tag) = _owner._content();
                return string.Join(", ", new[] { title, tag?.ToLowerInvariant(), detail }.Where(s => !string.IsNullOrEmpty(s)));
            }
            set { }
        }
        public override AccessibleStates State => base.State | (_owner.Selected ? AccessibleStates.Checked : AccessibleStates.None);
        public override void DoDefaultAction() => _owner.OnClick(EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var f = UiFonts.For(DeviceDpi);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, r, Ui.S(this, 8), _hover ? Theme.SurfaceHover : Theme.Surface);
        if (Selected || Focused)
        {
            using var path = Gfx.RoundedPath(r, Ui.S(this, 8));
            using var pen = new Pen(Selected ? Theme.Cpu : Theme.TextDim, Ui.S(this, 2));
            g.DrawPath(pen, path);
        }

        // Radio dot.
        int d = Ui.S(this, 18), cx = Ui.S(this, 18), cy = (Height - d) / 2;
        using (var pen = new Pen(Selected ? Theme.Cpu : Theme.TextDim, Math.Max(1.5f, Ui.Scale(this) * 1.5f)))
            g.DrawEllipse(pen, cx, cy, d, d);
        if (Selected)
        {
            using var dot = new SolidBrush(Theme.Cpu);
            int inner = d / 2;
            g.FillEllipse(dot, cx + (d - inner) / 2f, cy + (d - inner) / 2f, inner, inner);
        }

        var (title, detail, tag) = _content();
        int tx = cx + d + Ui.S(this, 16);
        int ty = detail != null ? Height / 2 - Ui.S(this, 19) : (Height - Ui.S(this, 20)) / 2;
        int titleW = Math.Min(TextRenderer.MeasureText(g, title, f.Body).Width, Width - tx - Ui.S(this, 140));
        Gfx.Text(g, title, f.Body, Theme.Text, new Rectangle(tx, ty, titleW + 4, Ui.S(this, 20)), TextFormatFlags.EndEllipsis);
        if (tag != null)
        {
            int pw = TextRenderer.MeasureText(g, tag, f.Label).Width + Ui.S(this, 12);
            var pill = new Rectangle(tx + titleW + Ui.S(this, 10), ty + Ui.S(this, 1), pw, Ui.S(this, 18));
            var c = tag == "INTEGRATED" ? Theme.IGpu : Theme.Gpu;
            Gfx.FillRounded(g, pill, Ui.S(this, 9), Theme.WithAlpha(c, 40));
            Gfx.Text(g, tag, f.Label, c, pill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (detail != null)
            Gfx.Text(g, detail, f.Small, Theme.TextDim, new Rectangle(tx, ty + Ui.S(this, 22), Width - tx - Ui.S(this, 16), Ui.S(this, 18)), TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
}

/// <summary>
/// A scrolling settings page. Cards settle their height only after the page has placed them (their
/// descriptions wrap at the final width), so the page's own scroll height can come out short and
/// leave the end of the page out of reach. After each layout this sizes the scroll area to where
/// the content actually ends, plus the bottom padding.
/// </summary>
sealed class SettingsPage : FlowLayoutPanel
{
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int bottom = 0;
        foreach (Control c in Controls)
            if (c.Visible) bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom);
        // Child positions are relative to the scrolled view; AutoScrollPosition.Y is negative when scrolled down.
        int content = bottom - AutoScrollPosition.Y + Padding.Bottom;
        if (AutoScrollMinSize.Height != content) AutoScrollMinSize = new Size(0, content);
    }
}

/// <summary>
/// Drag-and-drop reordering of the cards in a top-down list: the picked-up card lifts into a floating
/// copy that follows the pointer, its slot becomes a dashed gap that moves to where it will land, and
/// the page scrolls near its edges. Release drops it there; Esc puts it back.
/// </summary>
sealed class CardDragger : IMessageFilter
{
    readonly FlowLayoutPanel _list;
    readonly Action<int, int> _moved;
    readonly System.Windows.Forms.Timer _scroll = new() { Interval = 30 };
    Card? _card;
    Point _down, _grab;
    int _from;
    bool _dragging;
    DragGhost? _ghost;

    public CardDragger(FlowLayoutPanel list, Action<int, int> moved)
    {
        _list = list;
        _moved = moved;
        _scroll.Tick += (_, _) => ScrollNearEdges();
    }

    public void Attach(Card card)
    {
        card.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _card = card;
            _down = e.Location;
        };
        card.MouseMove += (_, e) =>
        {
            if (_card != card || (e.Button & MouseButtons.Left) == 0) return;
            if (!_dragging)
            {
                var size = SystemInformation.DragSize;
                if (Math.Abs(e.X - _down.X) < size.Width && Math.Abs(e.Y - _down.Y) < size.Height) return;
                Start(card);
            }
            Track();
        };
        card.MouseUp += (_, _) =>
        {
            if (_card == card) Finish(cancel: false);
        };
        // Alt+Tab or a click elsewhere mid-drag: keep the card where it is now.
        card.MouseCaptureChanged += (_, _) =>
        {
            if (_dragging && _card == card && !card.Capture) Finish(cancel: false);
        };
    }

    void Start(Card card)
    {
        _dragging = true;
        _from = _list.Controls.GetChildIndex(card);
        _grab = _down;
        var bmp = new Bitmap(card.Width, card.Height);
        card.DrawToBitmap(bmp, new Rectangle(Point.Empty, card.Size));
        using (var g = Graphics.FromImage(bmp))
        using (var path = Gfx.RoundedPath(new Rectangle(1, 1, bmp.Width - 3, bmp.Height - 3), Ui.S(card, 8)))
        using (var pen = new Pen(Theme.Cpu, Math.Max(1.5f, Ui.S(card, 1.5f))))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawPath(pen, path);
        }
        _ghost = new DragGhost(bmp) { Location = card.PointToScreen(Point.Empty) };
        _ghost.Show(card.FindForm());
        card.Placeholder = true;
        Application.AddMessageFilter(this);
        _scroll.Start();
    }

    /// <summary>Moves the floating copy to the pointer and the gap to where it would land.</summary>
    void Track()
    {
        if (_card == null || _ghost == null) return;
        var pointer = Cursor.Position;
        // Vertical only: the copy stays in the list's column.
        int left = _list.PointToScreen(new Point(_card.Left, 0)).X;
        _ghost.Location = new Point(left, pointer.Y - _grab.Y);

        int y = _list.PointToClient(pointer).Y;
        int target = 0;
        foreach (Control c in _list.Controls)
            if (c != _card && c.Top + c.Height / 2 < y) target++;
        if (target != _list.Controls.GetChildIndex(_card)) _list.Controls.SetChildIndex(_card, target);
    }

    void ScrollNearEdges()
    {
        var page = Ancestors(_list).OfType<ScrollableControl>().FirstOrDefault(s => s.AutoScroll);
        if (page == null || _card == null) return;
        var view = page.RectangleToScreen(page.ClientRectangle);
        int zone = Ui.S(page, 48), y = Cursor.Position.Y;
        int step = y < view.Top + zone ? -(view.Top + zone - y) / 2 : y > view.Bottom - zone ? (y - (view.Bottom - zone)) / 2 : 0;
        if (step == 0) return;
        page.AutoScrollPosition = new Point(0, Math.Max(0, -page.AutoScrollPosition.Y + step));
        Track();
    }

    static IEnumerable<Control> Ancestors(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent) yield return p;
    }

    void Finish(bool cancel)
    {
        var card = _card;
        _card = null;
        if (!_dragging || card == null) return;
        _dragging = false;
        _scroll.Stop();
        Application.RemoveMessageFilter(this);
        _ghost?.Close();
        _ghost?.Dispose();
        _ghost = null;
        if (cancel) _list.Controls.SetChildIndex(card, _from);
        card.Placeholder = false;
        int to = _list.Controls.GetChildIndex(card);
        if (to != _from) _moved(_from, to);
    }

    public bool PreFilterMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;
        if (m.Msg != WM_KEYDOWN || (int)m.WParam != VK_ESCAPE || !_dragging) return false;
        var card = _card;
        Finish(cancel: true);
        // After Finish, so losing the capture doesn't count as a drop.
        if (card != null) card.Capture = false;
        return true;
    }
}

/// <summary>The floating copy of a card being dragged: never activates and lets clicks through.</summary>
sealed class DragGhost : Form
{
    readonly Bitmap _image;

    public DragGhost(Bitmap image)
    {
        _image = image;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = image.Size;
        Opacity = 0.94;
        BackColor = Theme.Surface;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;                    // CS_DROPSHADOW
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20;      // TOOLWINDOW | NOACTIVATE | TRANSPARENT
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(_image, 0, 0);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A desktop panel preset in the gallery: a live thumbnail over a wallpaper-like backdrop, and its name.</summary>
sealed class PresetCard : Control
{
    readonly Func<PanelLayout> _layout;
    readonly Func<Snapshot?> _snapshot;
    readonly string _name, _description;
    bool _hover;
    public bool Selected { get; set; }

    public PresetCard(string name, string description, Func<PanelLayout> layout, Func<Snapshot?> snapshot, Action onClick)
    {
        _name = name;
        _description = description;
        _layout = layout;
        _snapshot = snapshot;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Size = new Size(204, 196);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 12, 12);
        TabStop = true;
        AccessibleRole = AccessibleRole.RadioButton;
        AccessibleName = $"{name} panel layout";
        AccessibleDescription = description;
        Click += (_, _) => onClick();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PresetAccessible(this);

    sealed class PresetAccessible(PresetCard owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Selected ? AccessibleStates.Checked : AccessibleStates.None);
        public override void DoDefaultAction() => owner.OnClick(EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var f = UiFonts.For(DeviceDpi);
        var area = new Rectangle(Ui.S(this, 1), Ui.S(this, 1), Width - Ui.S(this, 3), Height - Ui.S(this, 50));
        PanelBackdrop.Paint(g, area, Ui.S(this, 8));

        var layout = _layout();
        var natural = PanelGrid.PanelSize(layout, 1);
        var room = Rectangle.Inflate(area, -Ui.S(this, 10), -Ui.S(this, 10));
        float k = Math.Min(room.Width / (float)natural.Width, room.Height / (float)natural.Height);
        using (var thumb = PanelRenderer.Render(layout, _snapshot(), k, null))
            g.DrawImageUnscaled(thumb, room.Left + (room.Width - thumb.Width) / 2, room.Top + (room.Height - thumb.Height) / 2);

        using (var path = Gfx.RoundedPath(area, Ui.S(this, 8)))
        using (var pen = new Pen(Selected ? Theme.Cpu : _hover || Focused ? Theme.TextDim : Theme.Border, Selected || Focused ? Ui.S(this, 2) : 1))
            g.DrawPath(pen, path);
        Gfx.Text(g, _name, Selected ? f.BodyBold : f.Body, Theme.Text, new Rectangle(0, area.Bottom + Ui.S(this, 7), Width, Ui.S(this, 20)));
        Gfx.Text(g, _description, f.Small, Theme.TextDim, new Rectangle(0, area.Bottom + Ui.S(this, 27), Width, Ui.S(this, 18)), TextFormatFlags.EndEllipsis);
    }
}

/// <summary>A stand-in wallpaper behind panel previews, so see-through panels look as they will on the desktop.</summary>
static class PanelBackdrop
{
    public static void Paint(Graphics g, Rectangle r, int radius)
    {
        using var path = Gfx.RoundedPath(r, radius);
        using var brush = new LinearGradientBrush(r, Color.FromArgb(38, 64, 112), Color.FromArgb(104, 62, 108), LinearGradientMode.ForwardDiagonal);
        g.FillPath(brush, path);
    }
}

/// <summary>A colour setting: a swatch and its value; click to choose. Null means automatic (the theme's colour).</summary>
sealed class ColorPicker : Control
{
    readonly Func<Color> _automatic;
    bool _hover;
    Color? _value;
    public event Action? ValueChanged;

    public ColorPicker(Color? value, Func<Color> automatic, int width = 150)
    {
        _value = value;
        _automatic = automatic;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Size = new Size(width, 32);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        Click += (_, _) => Choose();
    }

    public Color? Value
    {
        get => _value;
        set
        {
            _value = value;
            AccessibleDescription = Text = value is { } c ? PanelColor.ToHex(c) : "Automatic";
            Invalidate();
        }
    }

    void Choose()
    {
        using var dialog = new ColorDialog { Color = _value ?? _automatic(), FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        Value = Color.FromArgb(255, dialog.Color);
        ValueChanged?.Invoke();
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Value = _value;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) Choose();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Gfx.FillRounded(g, r, Ui.S(this, 6), _hover ? Theme.SurfaceHover : Theme.Bg);
        using (var path = Gfx.RoundedPath(r, Ui.S(this, 6)))
        using (var pen = new Pen(Focused ? Theme.TextDim : Theme.Border))
            g.DrawPath(pen, path);
        int d = Ui.S(this, 18);
        var dot = new Rectangle(Ui.S(this, 8), (Height - d) / 2, d, d);
        using (var brush = new SolidBrush(_value ?? _automatic())) g.FillEllipse(brush, dot);
        using (var pen = new Pen(Theme.Border)) g.DrawEllipse(pen, dot);
        var f = UiFonts.For(DeviceDpi);
        Gfx.Text(g, _value is { } c ? PanelColor.ToHex(c) : "Automatic", f.Body, _value == null ? Theme.TextDim : Theme.Text,
                 Rectangle.FromLTRB(dot.Right + Ui.S(this, 8), 0, r.Right - Ui.S(this, 6), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
