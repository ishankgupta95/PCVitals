using System.Drawing.Drawing2D;

namespace PCVitals;

/// <summary>Detail card that pops up above a taskbar widget item on hover, like an iStat Menus dropdown.</summary>
sealed class HoverCard : Form
{
    ItemView? _view;
    int _dpi = 96;

    public HoverCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Text = "PC Vitals";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;                         // CS_DROPSHADOW
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8;            // TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    public void ShowFor(ItemView view, Rectangle anchor, TaskbarInfo taskbar)
    {
        _view = view;
        _dpi = taskbar.Dpi;
        var size = Measure(view, _dpi);
        int m = Scale(8, _dpi);
        var screen = Screen.FromRectangle(anchor).Bounds;
        int x = Math.Clamp(anchor.Left + anchor.Width / 2 - size.Width / 2, screen.Left + m, Math.Max(screen.Left + m, screen.Right - size.Width - m));
        // Open away from the screen edge the strip sits on (it can float above a hidden taskbar).
        bool bottomEdge = anchor.Top > screen.Top + screen.Height / 2;
        int y = bottomEdge ? anchor.Top - size.Height - m : anchor.Bottom + m;
        var bounds = new Rectangle(x, y, size.Width, size.Height);
        if (Bounds != bounds) Bounds = bounds;
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_view != null) PaintCard(e.Graphics, _view, _dpi, ClientSize);
    }

    static int Scale(float v, int dpi) => (int)Math.Round(v * dpi / 96f);

    const int Pad = 16, HeaderH = 20, DeviceH = 26, PrimaryH = 42, GraphH = 58, CaptionH = 24, RowH = 23, FooterH = 30;

    public static Size Measure(ItemView v, int dpi) =>
        new(Scale(340, dpi), Scale(Pad + HeaderH + DeviceH + PrimaryH + GraphH + CaptionH + 8 + v.Rows.Count * RowH + FooterH + Pad - 6, dpi));

    public static void PaintCard(Graphics g, ItemView v, int dpi, Size size)
    {
        int S(float x) => Scale(x, dpi);
        var f = UiFonts.For(dpi);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        int pad = S(Pad), x = pad, w = size.Width - pad * 2, y = pad;

        // Header: ● CPU  [DISCRETE]
        using (var dot = new SolidBrush(v.Accent)) g.FillEllipse(dot, x, y + S(5), S(8), S(8));
        var title = v.Title.ToUpperInvariant();
        Gfx.Text(g, title, f.Label, Theme.TextDim, new Rectangle(x + S(14), y, w, S(18)), TextFormatFlags.VerticalCenter);
        if (v.Tag != null)
        {
            int tx = x + S(14) + Gfx.TextWidth(g, title, f.Label) + S(8);
            var tag = v.Tag.ToUpperInvariant();
            var pill = new Rectangle(tx, y, Gfx.TextWidth(g, tag, f.Label) + S(12), S(18));
            Gfx.FillRounded(g, pill, S(9), Theme.WithAlpha(v.Accent, 40));
            Gfx.Text(g, tag, f.Label, v.Accent, pill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        y += S(HeaderH);

        Gfx.Text(g, v.Device ?? v.Title, f.Title, Theme.Text, new Rectangle(x, y, w, S(DeviceH)), TextFormatFlags.EndEllipsis);
        y += S(DeviceH);

        // Big value with the secondary reading on the right.
        var primary = v.Primary ?? v.Line2 ?? "--";
        Gfx.Text(g, primary, f.Big, Theme.Text, new Rectangle(x, y, w, S(PrimaryH)), TextFormatFlags.VerticalCenter);
        if (v.Line1 != null)
        {
            var color = v.Line1Temp != null ? Theme.TempColor(v.Line1Temp) : Theme.TextDim;
            Gfx.Text(g, v.Line1, f.Mid, color, new Rectangle(x, y, w, S(PrimaryH)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        y += S(PrimaryH);

        var graph = new Rectangle(x, y, w, S(GraphH));
        Gfx.FillRounded(g, graph, S(8), Theme.Surface);
        var state = g.Save();
        using (var clip = Gfx.RoundedPath(graph, S(8))) g.SetClip(clip, CombineMode.Intersect);
        Gfx.Sparkline(g, Rectangle.Inflate(graph, 0, -S(4)), v.History, v.HistoryMax, v.Accent, Math.Max(1.5f, S(1.6f)));
        g.Restore(state);
        y += S(GraphH);
        Gfx.Text(g, v.HistoryCaption, f.Small, Theme.TextFaint, new Rectangle(x, y + S(4), w, S(18)));
        y += S(CaptionH);

        using (var sep = new Pen(Theme.Border)) g.DrawLine(sep, x, y + S(4), x + w, y + S(4));
        y += S(8);

        foreach (var row in v.Rows)
        {
            var r = new Rectangle(x, y, w, S(RowH));
            int valueW = Math.Min(Gfx.TextWidth(g, row.Value, f.BodyBold) + S(2), w * 2 / 3);
            Gfx.Text(g, row.Label, f.Body, Theme.TextDim, new Rectangle(x, y, w - valueW - S(10), S(RowH)),
                     TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, row.Value, f.BodyBold, row.Color ?? Theme.Text, r,
                     TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += S(RowH);
        }

        Gfx.Text(g, "Click for full details  ·  drag to move  ·  right-click for options", f.Small, Theme.TextFaint,
                 new Rectangle(x, y + S(8), w, S(18)), TextFormatFlags.EndEllipsis);
    }
}
