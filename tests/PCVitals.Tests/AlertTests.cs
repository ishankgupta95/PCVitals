using LibreHardwareMonitor.Hardware;
using Xunit;

namespace PCVitals.Tests;

public class AlertEngineTests
{
    static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0);

    static AlertRule Hot(float limit = 90, int duration = 15, int cooldown = 15) =>
        new() { Id = "hot", Series = "cputemp", Threshold = limit, DurationSeconds = duration, CooldownMinutes = cooldown };

    /// <summary>Feeds one value per second; returns the seconds at which events happened.</summary>
    static List<(int Second, bool Fired)> Run(AlertEngine engine, AlertRule rule, IEnumerable<float?> values, int startSecond = 0)
    {
        var events = new List<(int, bool)>();
        int s = startSecond;
        foreach (var v in values)
        {
            foreach (var e in engine.Evaluate(new[] { rule }, T0.AddSeconds(s), _ => v)) events.Add((s, e.Fired));
            s++;
        }
        return events;
    }

    static IEnumerable<float?> Repeat(float? v, int n) => Enumerable.Repeat(v, n);

    [Fact]
    public void Fires_only_after_the_condition_held_for_its_duration()
    {
        var events = Run(new AlertEngine(), Hot(), Repeat(80, 5).Concat(Repeat(95, 20)));
        Assert.Equal(new[] { (20, true) }, events); // above from second 5, fires 15 s later
    }

    [Fact]
    public void A_short_spike_does_not_fire()
    {
        var events = Run(new AlertEngine(), Hot(), Repeat(95, 10).Concat(Repeat(80, 5)).Concat(Repeat(95, 10)));
        Assert.Empty(events);
    }

    [Fact]
    public void Hovering_just_under_the_limit_keeps_the_timer_running()
    {
        // 88 °C is under 90 but within the 3 °C margin, so it doesn't count as cooled down.
        var values = new float?[] { 95, 95, 95, 88, 88, 95, 95, 95, 95, 95, 95, 95, 95, 95, 95, 95 };
        Assert.Equal(new[] { (15, true) }, Run(new AlertEngine(), Hot(), values));
    }

    [Fact]
    public void Repeats_once_per_cooldown_while_it_stays_high_and_reports_recovery()
    {
        var engine = new AlertEngine();
        var events = Run(engine, Hot(duration: 5, cooldown: 1), Repeat(95, 130).Concat(Repeat(86, 3)));
        // Fires at 5 s, again after the 1-minute cooldown at 65 s and 125 s, then recovers below 87 °C.
        Assert.Equal(new[] { (5, true), (65, true), (125, true), (130, false) }, events);
        Assert.Equal(T0.AddSeconds(125), engine.LastFired("hot"));
    }

    [Fact]
    public void Missing_reading_never_fires_and_restarts_the_timer()
    {
        var events = Run(new AlertEngine(), Hot(duration: 5), Repeat(95, 4).Concat(Repeat(null, 10)).Concat(Repeat(95, 4)));
        Assert.Empty(events);
    }

    [Fact]
    public void Below_rules_fire_when_the_value_drops()
    {
        var fan = new AlertRule
        {
            Id = "fan", Condition = AlertCondition.Below, Threshold = 300, DurationSeconds = 3,
            Sensor = new SensorKey("/lpc/0/fan/1", HardwareType.SuperIO, "Chip", SensorType.Fan, "Fan #2"),
        };
        Assert.Equal(15f, fan.Margin); // 5% of 300 RPM
        var events = Run(new AlertEngine(), fan, Repeat(1200, 2).Concat(Repeat(0, 5)).Concat(Repeat(310, 2)).Concat(Repeat(400, 1)));
        Assert.Equal(new[] { (5, true), (9, false) }, events); // 310 is within the margin; 400 is clear
    }

    [Fact]
    public void Disabled_rules_do_nothing()
    {
        var rule = Hot(duration: 1);
        rule.Enabled = false;
        Assert.Empty(Run(new AlertEngine(), rule, Repeat(100, 10)));
    }

    [Fact]
    public void Rules_read_headline_values_and_sensors_from_a_snapshot()
    {
        var fanKey = new SensorKey("/lpc/0/fan/1", HardwareType.SuperIO, "Chip", SensorType.Fan, "Fan #2");
        var chip = new HardwareReading("/lpc/0", "Chip", HardwareType.SuperIO, new[] { new SensorReading("/lpc/0/fan/1", "Fan #2", SensorType.Fan, 812, null, null) });
        var snap = new Snapshot(T0, new[] { chip }, new Summary { CpuTemp = 71, RamLoad = 40 }, new Dictionary<string, float[]>(), new[] { chip });
        Assert.Equal(71f, new AlertRule { Series = "cputemp" }.ValueIn(snap));
        Assert.Equal(40f, new AlertRule { Series = "ram" }.ValueIn(snap));
        Assert.Null(new AlertRule { Series = "gputemp" }.ValueIn(snap));
        Assert.Equal(812f, new AlertRule { Sensor = fanKey }.ValueIn(snap));
        Assert.Equal(SensorType.Temperature, new AlertRule { Series = "gputemp" }.Type);
    }
}

public sealed class AlertSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcvitals-tests-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "settings.json");

    public AlertSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Version_3_alerts_become_rules_with_the_same_limits()
    {
        File.WriteAllText(FilePath, """
            { "Version": 3, "AlertCpuEnabled": true, "AlertCpuTemp": 85, "AlertGpuEnabled": false, "AlertGpuTemp": 80,
              "AlertRamEnabled": true, "AlertRamPercent": 90 }
            """);
        var s = AppSettings.LoadFrom(FilePath);
        Assert.Equal(new[] { ("cputemp", true, 85f), ("gputemp", false, 80f), ("ram", true, 90f) },
                     s.AlertRules.Select(r => (r.Series!, r.Enabled, r.Threshold)));
        Assert.All(s.AlertRules, r =>
        {
            Assert.Equal(15, r.DurationSeconds);
            Assert.Equal(15, r.CooldownMinutes);
            Assert.True(r.Notify);
        });
        var saved = File.ReadAllText(FilePath);
        Assert.DoesNotContain("AlertCpuEnabled", saved); // the old fields are gone once converted
        Assert.Contains("\"AlertRules\"", saved);
    }

    [Fact]
    public void Missing_old_values_use_the_old_defaults()
    {
        File.WriteAllText(FilePath, """{ "Version": 3 }""");
        Assert.Equal(new[] { 90f, 85f, 95f }, AppSettings.LoadFrom(FilePath).AlertRules.Select(r => r.Threshold));
    }

    [Fact]
    public void Rules_round_trip_and_unusable_ones_are_dropped()
    {
        var s = AppSettings.CreateDefault();
        s.AlertRules.Add(new AlertRule
        {
            Sensor = new SensorKey("/lpc/0/fan/1", HardwareType.SuperIO, "Chip", SensorType.Fan, "Fan #2"),
            Condition = AlertCondition.Below, Threshold = 300, DurationSeconds = 30, CooldownMinutes = 60,
            Sound = "Exclamation", RunProgram = @"C:\Tools\shutdown-safely.cmd", RunArguments = "/now", RunAsAdmin = true,
        });
        s.AlertRules.Add(new AlertRule { Series = "no-such-reading" });
        s.SaveTo(FilePath);

        var back = AppSettings.LoadFrom(FilePath);
        Assert.Equal(4, back.AlertRules.Count);
        var fan = back.AlertRules[3];
        Assert.Equal(s.AlertRules[3].Id, fan.Id);
        Assert.Equal(AlertCondition.Below, fan.Condition);
        Assert.Equal("Fan #2", fan.Sensor!.SensorName);
        Assert.Equal(@"C:\Tools\shutdown-safely.cmd", fan.RunProgram);
        Assert.True(fan.RunAsAdmin);
        Assert.DoesNotContain("\"Margin\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Sensors_watched_by_alerts_are_tracked()
    {
        var s = AppSettings.CreateDefault();
        var key = new SensorKey("/lpc/0/fan/1", HardwareType.SuperIO, "Chip", SensorType.Fan, "Fan #2");
        s.AlertRules.Add(new AlertRule { Sensor = key });
        Assert.Contains(key, s.TrackedSensors(null));
    }
}

public class AlertTextTests
{
    [Fact]
    public void Thresholds_show_in_the_display_unit_and_convert_back()
    {
        try
        {
            Fmt.Fahrenheit = true;
            var rule = new AlertRule { Series = "cputemp", Threshold = 90, DurationSeconds = 90 };
            Assert.Equal("above 194°F for 1 min 30 s", AlertText.Condition(rule));
            Assert.Equal(90f, Fmt.CelsiusFromDisplay(194f), 3);
            Fmt.Fahrenheit = false;
            Assert.Equal("above 90°C for 1 min 30 s", AlertText.Condition(rule));
            Assert.Equal(90f, Fmt.CelsiusFromDisplay(90f));
        }
        finally
        {
            Fmt.Fahrenheit = false;
        }
    }

    [Fact]
    public void Actions_are_summarised()
    {
        Assert.Equal("Notification", AlertText.Actions(new AlertRule()));
        Assert.Equal("Notification, sound, runs fan.cmd as administrator",
                     AlertText.Actions(new AlertRule { Sound = "Hand", RunProgram = @"C:\x\fan.cmd", RunAsAdmin = true }));
        Assert.StartsWith("No action", AlertText.Actions(new AlertRule { Notify = false }));
    }
}
