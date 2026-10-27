using LibreHardwareMonitor.Hardware;

namespace PCVitals.Core;

public enum AlertCondition
{
    Above,
    Below,
}

/// <summary>
/// "Tell me when X stays above/below N for D seconds", and what to do then. Thresholds are in the
/// sensor's own unit (temperatures in °C, whatever the display unit).
/// </summary>
public sealed class AlertRule
{
    /// <summary>Headline readings a rule can watch without picking a sensor.</summary>
    /// <remarks>Unit is shown after limits and values; "health" values are <see cref="HealthLevel"/> numbers (2 = Caution, 3 = Bad).</remarks>
    public static readonly IReadOnlyDictionary<string, (string Name, SensorType Type, string Unit)> SeriesTargets = new Dictionary<string, (string, SensorType, string)>
    {
        ["cputemp"] = ("CPU temperature", SensorType.Temperature, "°C"),
        ["gputemp"] = ("GPU temperature", SensorType.Temperature, "°C"),
        ["ram"] = ("Memory usage", SensorType.Load, "%"),
        ["diskhealth"] = ("Drive health (any drive)", SensorType.Factor, "health"),
        ["ping"] = ("Ping latency", SensorType.Factor, "ms"),
        ["pingloss"] = ("Ping loss", SensorType.Level, "%"),
    };

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    /// <summary>The sensor watched, or null when <see cref="Series"/> is set.</summary>
    public SensorKey? Sensor { get; set; }
    /// <summary>A headline reading from <see cref="SeriesTargets"/> ("cputemp": whichever sensor is the CPU temperature).</summary>
    public string? Series { get; set; }
    public AlertCondition Condition { get; set; } = AlertCondition.Above;
    public float Threshold { get; set; }
    /// <summary>How long the condition must hold before the alert fires.</summary>
    public int DurationSeconds { get; set; } = 15;
    /// <summary>Minimum time between two alerts from this rule while the condition keeps holding.</summary>
    public int CooldownMinutes { get; set; } = 15;

    // Actions
    public bool Notify { get; set; } = true;
    /// <summary>Null for none; "Asterisk", "Exclamation", "Hand" or "Beep" for a Windows sound; otherwise a .wav path.</summary>
    public string? Sound { get; set; }
    /// <summary>Program to run (set by the user in Settings only).</summary>
    public string? RunProgram { get; set; }
    public string? RunArguments { get; set; }
    /// <summary>Run <see cref="RunProgram"/> with administrator rights; otherwise it runs as the signed-in user, without them.</summary>
    public bool RunAsAdmin { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public SensorType Type => Sensor?.SensorType ?? (Series != null && SeriesTargets.TryGetValue(Series, out var t) ? t.Type : SensorType.Factor);

    public bool IsTriggered(float value) => Condition == AlertCondition.Above ? value > Threshold : value < Threshold;

    /// <summary>
    /// How far back past the threshold the value must go before the condition counts as over, so a
    /// reading hovering around the limit doesn't restart the timer or flap.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float Margin => Series == "diskhealth" ? 0.5f : Type switch
    {
        SensorType.Temperature => 3,
        SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Humidity => 3,
        _ => Math.Max(Math.Abs(Threshold) * 0.05f, 0.001f),
    };

    public bool IsClear(float value) => Condition == AlertCondition.Above ? value < Threshold - Margin : value > Threshold + Margin;

    public AlertRule Clone() => (AlertRule)MemberwiseClone();

    /// <summary>The watched reading in a snapshot; null when it isn't available (no admin rights, device gone).</summary>
    public float? ValueIn(Snapshot snap) => Series switch
    {
        "cputemp" => snap.Summary.CpuTemp,
        "gputemp" => snap.Summary.Gpu?.Temp,
        "ram" => snap.Summary.RamLoad,
        "diskhealth" => snap.Summary.WorstDriveHealth is var h && h != HealthLevel.Unknown ? (float)h : null,
        "ping" => snap.Summary.Ping?.AvgMs,
        "pingloss" => snap.Summary.Ping is { Samples: > 0 } p ? p.LossPercent : null,
        _ => Sensor is { } key ? snap.FindSensor(key)?.Sensor.Value : null,
    };
}

/// <param name="Fired">True when the alert went off; false when its reading came back (after having fired).</param>
public sealed record AlertEvent(AlertRule Rule, bool Fired, float Value, DateTime Time);

/// <summary>
/// Decides when rules fire. Per rule: the condition must hold for the rule's duration; it then fires,
/// and fires again at most once per cooldown while it keeps holding. The condition only counts as
/// over once the value is back past the threshold by <see cref="AlertRule.Margin"/>. A missing
/// reading (device gone, sensor not available) never fires and resets the timer.
/// </summary>
public sealed class AlertEngine
{
    sealed class State
    {
        public DateTime? Since;
        public DateTime? LastFired;
        public bool Active;
    }

    readonly Dictionary<string, State> _states = new();

    public List<AlertEvent> Evaluate(IReadOnlyList<AlertRule> rules, DateTime now, Func<AlertRule, float?> valueOf)
    {
        lock (_states) return EvaluateLocked(rules, now, valueOf);
    }

    List<AlertEvent> EvaluateLocked(IReadOnlyList<AlertRule> rules, DateTime now, Func<AlertRule, float?> valueOf)
    {
        var events = new List<AlertEvent>();
        foreach (var rule in rules)
        {
            if (!_states.TryGetValue(rule.Id, out var s)) _states[rule.Id] = s = new State();
            if (!rule.Enabled || valueOf(rule) is not { } v || !float.IsFinite(v))
            {
                s.Since = null;
                s.Active = false;
                continue;
            }
            if (rule.IsTriggered(v))
            {
                s.Since ??= now;
            }
            else if (rule.IsClear(v))
            {
                s.Since = null;
                if (s.Active)
                {
                    s.Active = false;
                    events.Add(new AlertEvent(rule, false, v, now));
                }
                continue;
            }
            // Between the threshold and the margin: keep whatever state we were in.
            if (s.Since is not { } since || now - since < TimeSpan.FromSeconds(rule.DurationSeconds)) continue;
            if (s.LastFired is { } last && now - last < TimeSpan.FromMinutes(rule.CooldownMinutes)) continue;
            s.LastFired = now;
            s.Active = true;
            events.Add(new AlertEvent(rule, true, v, now));
        }
        // Forget rules that were removed.
        if (_states.Count > rules.Count)
            foreach (var id in _states.Keys.Where(id => rules.All(r => r.Id != id)).ToList()) _states.Remove(id);
        return events;
    }

    /// <summary>When the rule last fired since PC Vitals started.</summary>
    public DateTime? LastFired(string ruleId)
    {
        lock (_states) return _states.TryGetValue(ruleId, out var s) ? s.LastFired : null;
    }
}

/// <summary>The last 100 alerts, kept in a small JSON file next to the settings.</summary>
public sealed class AlertLog
{
    public sealed record Entry(DateTime Time, string RuleId, string Name, string Text);

    public const int Capacity = 100;
    readonly string _path;
    readonly List<Entry> _entries;

    public AlertLog(string path)
    {
        _path = path;
        try
        {
            _entries = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _entries = new();
        }
    }

    public IReadOnlyList<Entry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public void Add(Entry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
            Save();
        }
    }

    public void Clear()
    {
        lock (_entries)
        {
            _entries.Clear();
            Save();
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HardwareService.LogOnce("alert-log", "Couldn't save the alert history: " + ex.Message);
        }
    }
}
