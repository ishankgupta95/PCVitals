using Xunit;

namespace PCVitals.Tests;

public sealed class SceneTests : IDisposable
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    readonly string _path = Path.Combine(Path.GetTempPath(), $"pcvitals-scene-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    static PanelLayout Panel(string id, string name = "P") => new() { Id = id, Name = name };
    static Summary Gpu(float load) => new() { Gpu = new GpuSummary { Id = "/gpu/0", Name = "GPU", ShortName = "GPU", Load = load } };

    static (PanelLayout Host, PanelLayout Scene, List<PanelLayout> All) Pair()
    {
        var host = Panel("host", "Idle");
        var scene = Panel("scene", "Gaming");
        host.SceneId = "scene";
        host.SceneThreshold = 60;
        return (host, scene, new List<PanelLayout> { host, scene });
    }

    [Fact]
    public void Switches_to_the_scene_only_after_five_busy_seconds()
    {
        var (host, _, all) = Pair();
        var sw = new SceneSwitcher();
        Assert.Equal("host", sw.Update(T0, host, all, Gpu(90), false));
        Assert.Equal("host", sw.Update(T0.AddSeconds(4), host, all, Gpu(90), false));
        Assert.Equal("scene", sw.Update(T0.AddSeconds(5), host, all, Gpu(90), false));
    }

    [Fact]
    public void A_short_spike_does_not_switch()
    {
        var (host, _, all) = Pair();
        var sw = new SceneSwitcher();
        sw.Update(T0, host, all, Gpu(90), false);
        sw.Update(T0.AddSeconds(3), host, all, Gpu(10), false);
        // The timer restarted: 4 s after the load resumes is still too soon.
        sw.Update(T0.AddSeconds(4), host, all, Gpu(90), false);
        Assert.Equal("host", sw.Update(T0.AddSeconds(8), host, all, Gpu(90), false));
        Assert.Equal("scene", sw.Update(T0.AddSeconds(9), host, all, Gpu(90), false));
    }

    [Fact]
    public void Goes_back_only_after_fifteen_calm_seconds()
    {
        var (host, _, all) = Pair();
        var sw = new SceneSwitcher();
        sw.Update(T0, host, all, Gpu(90), false);
        sw.Update(T0.AddSeconds(5), host, all, Gpu(90), false);
        Assert.Equal("scene", sw.Update(T0.AddSeconds(10), host, all, Gpu(5), false));
        Assert.Equal("scene", sw.Update(T0.AddSeconds(24), host, all, Gpu(5), false));
        // A busy blip resets the calm timer.
        Assert.Equal("scene", sw.Update(T0.AddSeconds(25), host, all, Gpu(90), false));
        Assert.Equal("scene", sw.Update(T0.AddSeconds(26), host, all, Gpu(5), false));
        Assert.Equal("scene", sw.Update(T0.AddSeconds(40), host, all, Gpu(5), false));
        Assert.Equal("host", sw.Update(T0.AddSeconds(41), host, all, Gpu(5), false));
    }

    [Fact]
    public void Threshold_and_trigger_decide_what_is_busy()
    {
        var (host, _, _) = Pair();
        Assert.False(SceneSwitcher.IsBusy(host, Gpu(60), false)); // "above"
        Assert.True(SceneSwitcher.IsBusy(host, Gpu(61), false));
        Assert.False(SceneSwitcher.IsBusy(host, new Summary { CpuLoad = 99 }, true)); // GPU trigger ignores CPU and fullscreen
        Assert.False(SceneSwitcher.IsBusy(host, null, false));

        host.SceneTrigger = SceneTrigger.CpuUsage;
        host.SceneThreshold = 80;
        Assert.False(SceneSwitcher.IsBusy(host, new Summary { CpuLoad = 75 }, false));
        Assert.True(SceneSwitcher.IsBusy(host, new Summary { CpuLoad = 85 }, false));
        Assert.False(SceneSwitcher.IsBusy(host, Gpu(99), false));
    }

    [Fact]
    public void Fullscreen_trigger_follows_the_flag_not_the_load()
    {
        var (host, _, all) = Pair();
        host.SceneTrigger = SceneTrigger.Fullscreen;
        var sw = new SceneSwitcher();
        sw.Update(T0, host, all, Gpu(0), true);
        Assert.Equal("scene", sw.Update(T0.AddSeconds(5), host, all, Gpu(0), true));
        sw.Update(T0.AddSeconds(6), host, all, Gpu(100), false);
        Assert.Equal("host", sw.Update(T0.AddSeconds(21), host, all, Gpu(100), false));
    }

    [Fact]
    public void A_missing_or_deleted_scene_falls_back_to_the_panels_own_layout()
    {
        var (host, scene, all) = Pair();
        var sw = new SceneSwitcher();
        sw.Update(T0, host, all, Gpu(90), false);
        Assert.Equal("scene", sw.Update(T0.AddSeconds(5), host, all, Gpu(90), false));

        all.Remove(scene); // deleted in Settings
        Assert.Equal("host", sw.Update(T0.AddSeconds(6), host, all, Gpu(90), false));
        Assert.Null(SceneSwitcher.FindScene(host, all));

        host.SceneId = "host"; // pointing at itself
        Assert.Null(SceneSwitcher.FindScene(host, new[] { host }));
        host.SceneId = null;
        Assert.Equal("host", new SceneSwitcher().Update(T0, host, all, Gpu(90), false));
    }

    [Fact]
    public void Merge_takes_the_scene_look_and_keeps_the_hosts_window()
    {
        var (host, scene, _) = Pair();
        host.Monitor = "\\\\.\\DISPLAY2";
        host.Position = new Point(5, 6);
        host.FillDisplay = true;
        host.Locked = true;
        host.Mode = PanelMode.AlwaysOnTop;
        host.Scale = 150;
        scene.Columns = 4;
        scene.Style.Background = "#112233";
        scene.Tiles.Add(new PanelTile { Kind = PanelTileKind.Clock });
        scene.Monitor = "other";
        scene.Position = new Point(99, 99);

        var merged = SceneSwitcher.Merge(host, scene);
        Assert.Equal("host", merged.Id);
        Assert.Equal(4, merged.Columns);
        Assert.Equal("#112233", merged.Style.Background);
        Assert.Single(merged.Tiles);
        Assert.Equal("\\\\.\\DISPLAY2", merged.Monitor);
        Assert.Equal(new Point(5, 6), merged.Position);
        Assert.True(merged.FillDisplay);
        Assert.True(merged.Locked);
        Assert.Equal(PanelMode.AlwaysOnTop, merged.Mode);
        Assert.Equal(150, merged.Scale);

        // The real panels are untouched, and the copy shares nothing with them.
        Assert.Empty(host.Tiles);
        Assert.Equal("other", scene.Monitor);
        merged.Tiles.Clear();
        Assert.Single(scene.Tiles);
    }

    [Fact]
    public void Scene_settings_round_trip_and_old_files_load_with_defaults()
    {
        var s = AppSettings.CreateDefault();
        s.Panels.Add(new PanelLayout { Id = "a", SceneId = "b", SceneTrigger = SceneTrigger.Fullscreen, SceneThreshold = 80 });
        s.SaveTo(_path);
        var back = AppSettings.LoadFrom(_path).Panels.Single(p => p.Id == "a");
        Assert.Equal("b", back.SceneId);
        Assert.Equal(SceneTrigger.Fullscreen, back.SceneTrigger);
        Assert.Equal(80, back.SceneThreshold);

        File.WriteAllText(_path, """{ "Version": 5, "Panels": [ { "Id": "old", "Name": "Old" } ] }""");
        var old = AppSettings.LoadFrom(_path).Panels.Single();
        Assert.Null(old.SceneId);
        Assert.Equal(SceneTrigger.GpuUsage, old.SceneTrigger);
        Assert.Equal(60, old.SceneThreshold);
    }
}
