using Xunit;

namespace PCVitals.Tests;

public sealed class AwayTests
{
    [Fact]
    public void Lock_and_display_off_each_make_it_away_and_both_must_clear()
    {
        var s = new AwayState();
        int changes = 0;
        s.Changed += () => changes++;
        Assert.False(s.IsAway);

        s.OnLock();
        Assert.True(s.IsAway);
        s.OnDisplay(0);              // already away: no second event
        s.OnUnlock();
        Assert.True(s.IsAway);       // the display is still off
        s.OnDisplay(1);
        Assert.False(s.IsAway);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Dimmed_counts_as_visible_and_repeats_do_not_raise_events()
    {
        var s = new AwayState();
        int changes = 0;
        s.Changed += () => changes++;
        s.OnDisplay(2);
        Assert.False(s.IsAway);
        s.OnDisplay(0);
        s.OnDisplay(0);
        Assert.True(s.IsAway);
        s.OnDisplay(2);
        Assert.False(s.IsAway);
        s.OnUnlock();
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Panel_draws_nothing_while_away_and_resumes_on_return(bool accelerated)
    {
        var layout = PanelPresets.Build("gauges", PanelTests.Snap(full: true));
        layout.Enabled = true;
        layout.HideInFullscreen = false; // a fullscreen window in front would hide it for another reason
        layout.Accelerated = accelerated;
        layout.Style.Motion = PanelMotion.Full;
        layout.Style.AnimateBackground = true;
        var away = new AwayState();
        using var panel = new DesktopPanel(AppSettings.CreateDefault(), layout, null) { Away = away };
        _ = panel.Handle;
        panel.SetSnapshot(PanelTests.Snap(full: true));
        Assert.True(panel.DrawCount > 0);

        away.OnLock();
        Assert.True(panel.Paused);
        Assert.False(panel.Visible);
        int before = panel.DrawCount;
        for (int i = 0; i < 5; i++) panel.SetSnapshot(PanelTests.Snap(full: true));
        panel.Redraw(force: true);
        panel.CheckPlacement();
        Assert.Equal(before, panel.DrawCount);

        away.OnUnlock();
        Assert.False(panel.Paused);
        Assert.True(panel.DrawCount > before);

        // Turned off per panel: being away changes nothing.
        var other = layout.Clone();
        other.Style.PauseWhenAway = false;
        panel.Apply(other);
        away.OnDisplay(0);
        Assert.False(panel.Paused);
        int now = panel.DrawCount;
        panel.SetSnapshot(PanelTests.Snap(full: true));
        panel.Redraw(force: true);
        Assert.True(panel.DrawCount > now);
        panel.AllowClose = true;
    }

    [Fact]
    public void Pause_when_away_defaults_on_for_older_settings_files()
    {
        Assert.True(new PanelStyle().PauseWhenAway);
        var style = System.Text.Json.JsonSerializer.Deserialize<PanelStyle>("{\"Glow\":true}")!;
        Assert.True(style.PauseWhenAway);
    }
}

public sealed class PauseSettingTests
{
    [Fact]
    public void Choosing_a_layout_keeps_the_pause_when_away_choice()
    {
        // It's a window option like "hide in fullscreen", not part of the look a preset brings.
        var panel = new PanelLayout();
        panel.Style.PauseWhenAway = false;
        panel.ApplyPreset(PanelPresets.Build("case-glance", null));
        Assert.False(panel.Style.PauseWhenAway);
    }
}
