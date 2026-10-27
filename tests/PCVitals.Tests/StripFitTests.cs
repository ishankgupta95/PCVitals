using Xunit;

namespace PCVitals.Tests;

public class StripFitTests
{
    // A 4K taskbar at 150 % like the one PC Vitals was developed on: centred icons, tray on the right.
    static TaskbarInfo Taskbar(Rectangle start, Rectangle tasks, Rectangle? tray = null) => new(
        IntPtr.Zero, new Rectangle(0, 2088, 3840, 72), tray ?? new Rectangle(3315, 2088, 525, 72), tasks, start, 144);

    static readonly TaskbarInfo Centred = Taskbar(new Rectangle(1655, 2088, 67, 72), new Rectangle(1722, 2088, 330, 72));

    [Fact]
    public void Free_region_is_between_app_icons_and_tray()
    {
        var (left, right, anchorLeft) = StripFit.FreeRegion(Centred, WidgetPosition.NextToTray, 1.5f);
        Assert.Equal(2052 + 12, left);
        Assert.Equal(3315 - 12, right);
        Assert.False(anchorLeft);
    }

    [Fact]
    public void Left_side_uses_space_before_centred_icons()
    {
        var (left, right, anchorLeft) = StripFit.FreeRegion(Centred, WidgetPosition.LeftSide, 1.5f);
        Assert.Equal(12, left);
        Assert.Equal(1655 - 12, right);
        Assert.True(anchorLeft);
    }

    [Fact]
    public void Left_side_falls_back_to_tray_region_when_icons_are_left_aligned()
    {
        var leftAligned = Taskbar(new Rectangle(0, 2088, 67, 72), new Rectangle(67, 2088, 600, 72));
        var (left, right, anchorLeft) = StripFit.FreeRegion(leftAligned, WidgetPosition.LeftSide, 1.5f);
        Assert.Equal(667 + 12, left);
        Assert.Equal(3315 - 12, right);
        Assert.True(anchorLeft);
    }

    [Fact]
    public void Without_icon_info_the_region_starts_at_the_taskbar_edge()
    {
        var unknown = Taskbar(Rectangle.Empty, Rectangle.Empty);
        var (left, _, _) = StripFit.FreeRegion(unknown, WidgetPosition.NextToTray, 1f);
        Assert.Equal(8, left);
    }

    [Fact]
    public void Without_tray_info_space_is_reserved_for_the_clock()
    {
        var noTray = Taskbar(Rectangle.Empty, Rectangle.Empty, Rectangle.Empty);
        var (_, right, _) = StripFit.FreeRegion(noTray, WidgetPosition.NextToTray, 1f);
        Assert.Equal(3840 - 240 - 8, right);
    }

    // Fake measure: each item is 150 px (200 with its graph) at scale 1.5, proportional to scale.
    static int Measure(int items, float scale, bool graphs) => (int)Math.Round(items * (graphs ? 200 : 150) * scale / 1.5f);

    [Fact]
    public void Everything_fits_untouched()
    {
        var r = StripFit.Fit(4, Measure, 1000, 1.5f, 100, graphs: true);
        Assert.Equal((1.5f, true, 4), (r.Scale, r.Graphs, r.Shown));
        Assert.Null(r.Note);
    }

    [Fact]
    public void Graphs_are_dropped_first()
    {
        var r = StripFit.Fit(4, Measure, 600, 1.5f, 100, graphs: true);
        Assert.False(r.Graphs);
        Assert.Equal(1.5f, r.Scale);
        Assert.Equal(4, r.Shown);
        Assert.Contains("Graphs are hidden", r.Note);
    }

    [Fact]
    public void Text_shrinks_before_items_are_hidden()
    {
        var r = StripFit.Fit(4, Measure, 500, 1.5f, 100, graphs: true);
        Assert.Equal(4, r.Shown);
        Assert.True(r.Scale < 1.5f && r.Scale >= 1.05f);
        Assert.True(Measure(4, r.Scale, false) <= 500);
        Assert.Contains("made smaller", r.Note);
    }

    [Fact]
    public void Trailing_items_are_hidden_when_even_the_smallest_size_does_not_fit()
    {
        var r = StripFit.Fit(4, Measure, 200, 1.5f, 100, graphs: false);
        Assert.Equal(1, r.Shown);
        Assert.Equal(1.05f, r.Scale, 3);
        Assert.Contains("3 items are hidden", r.Note);
    }

    [Fact]
    public void At_least_one_item_is_always_shown()
    {
        var r = StripFit.Fit(4, Measure, 10, 1.5f, 100, graphs: true);
        Assert.Equal(1, r.Shown);
    }

    [Fact]
    public void User_size_is_clamped_and_applied()
    {
        Assert.Equal(1.5f * 1.5f, StripFit.Fit(1, Measure, 5000, 1.5f, 400, true).Scale, 3);
        Assert.Equal(1.5f * 0.85f, StripFit.Fit(1, Measure, 5000, 1.5f, 85, true).Scale, 3);
    }

    [Fact]
    public void No_items_is_not_an_error()
    {
        var r = StripFit.Fit(0, Measure, 0, 1f, 100, true);
        Assert.Equal(0, r.Shown);
        Assert.Null(r.Note);
    }
}

public class FullscreenTests
{
    static readonly Rectangle Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void Borderless_window_covering_the_monitor_is_fullscreen() =>
        Assert.True(Native.IsFullscreen(Monitor, Monitor, maximized: false));

    [Fact]
    public void Maximised_window_over_an_auto_hiding_taskbar_is_not()
    {
        // Maximised windows overhang the monitor by their border width on every side.
        Assert.False(Native.IsFullscreen(new Rectangle(-8, -8, 2576, 1456), Monitor, maximized: true));
    }

    [Fact]
    public void Ordinary_windows_are_not() =>
        Assert.False(Native.IsFullscreen(new Rectangle(100, 100, 1600, 900), Monitor, maximized: false));
}
