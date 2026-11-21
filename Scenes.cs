namespace PCVitals;

/// <summary>What makes a panel switch to its busy scene.</summary>
public enum SceneTrigger
{
    GpuUsage,
    CpuUsage,
    /// <summary>A game or video covers the main display (the threshold doesn't apply).</summary>
    Fullscreen,
}

/// <summary>
/// Decides which layout a panel shows: its own, or its "busy scene" (another panel's layout) while the PC is busy.
/// Busy must hold for <see cref="BusyAfter"/> before it switches, and calm for <see cref="CalmAfter"/> before it
/// switches back, so a brief spike (or a dip between loading screens) doesn't make the panel flicker.
/// One instance per panel.
/// </summary>
public sealed class SceneSwitcher
{
    public static readonly TimeSpan BusyAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CalmAfter = TimeSpan.FromSeconds(15);

    bool _busy;
    DateTime? _since;
    string? _sceneId;

    /// <summary>The scene panel for <paramref name="host"/>, if it names one that exists (and isn't the panel itself).</summary>
    public static PanelLayout? FindScene(PanelLayout host, IEnumerable<PanelLayout> panels) =>
        host.SceneId is { Length: > 0 } id && id != host.Id ? panels.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>Whether the trigger holds right now. A missing reading counts as calm.</summary>
    public static bool IsBusy(PanelLayout host, Summary? summary, bool fullscreen) => host.SceneTrigger switch
    {
        SceneTrigger.Fullscreen => fullscreen,
        SceneTrigger.CpuUsage => summary?.CpuLoad is { } cpu && cpu > host.SceneThreshold,
        _ => summary?.Gpu?.Load is { } gpu && gpu > host.SceneThreshold,
    };

    /// <summary>Feed one reading; returns the id of the layout to show (the host's own, or its scene's).</summary>
    public string Update(DateTime now, PanelLayout host, IReadOnlyList<PanelLayout> panels, Summary? summary, bool fullscreen)
    {
        var scene = FindScene(host, panels);
        if (scene == null || scene.Id != _sceneId)
        {
            // No scene (or a different one): start afresh, showing the panel's own layout.
            _busy = false;
            _since = null;
            _sceneId = scene?.Id;
            if (scene == null) return host.Id;
        }

        bool busyNow = IsBusy(host, summary, fullscreen);
        if (busyNow == _busy) _since = null;
        else
        {
            _since ??= now;
            if (now - _since >= (busyNow ? BusyAfter : CalmAfter))
            {
                _busy = busyNow;
                _since = null;
            }
        }
        return _busy ? scene.Id : host.Id;
    }

    /// <summary>
    /// A copy of the scene's tiles, grid and look that keeps the host's window options (and identity), so the panel
    /// stays on its screen. Never saved: settings keep the real panels.
    /// </summary>
    public static PanelLayout Merge(PanelLayout host, PanelLayout scene)
    {
        var l = scene.Clone();
        l.Id = host.Id;
        l.Name = host.Name;
        l.Enabled = host.Enabled;
        l.Monitor = host.Monitor;
        l.MonitorSize = host.MonitorSize;
        l.Position = host.Position;
        l.Mode = host.Mode;
        l.Locked = host.Locked;
        l.HideInFullscreen = host.HideInFullscreen;
        l.FillDisplay = host.FillDisplay;
        l.Scale = host.Scale;
        l.Accelerated = host.Accelerated;
        l.SceneId = host.SceneId;
        l.SceneTrigger = host.SceneTrigger;
        l.SceneThreshold = host.SceneThreshold;
        // Whether the window pauses while away is about the window, not the look.
        l.Style.PauseWhenAway = host.Style.PauseWhenAway;
        return l;
    }
}
