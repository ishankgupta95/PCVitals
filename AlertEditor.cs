using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace PCVitals;

/// <summary>Add or edit one alert rule: what to watch, when it counts, and what to do.</summary>
sealed class AlertEditor : Form
{
    static readonly string[] TargetSeries = AlertRule.SeriesTargets.Keys.ToArray();
    static readonly string[] TargetNames = AlertRule.SeriesTargets.Values.Select(t => t.Name).Append("A sensor…").ToArray();
    static int SensorIndex => TargetSeries.Length;
    static readonly int[] Durations = { 5, 15, 30, 60, 120, 300, 600 };
    static readonly int[] Cooldowns = { 1, 5, 15, 30, 60, 240 };

    readonly AlertRule _rule;
    readonly Func<Snapshot?> _latest;
    readonly AppSettings _settings;
    readonly Action<AlertRule> _test;
    readonly Dropdown _target, _condition, _duration, _cooldown, _sound;
    readonly Label _sensorName, _unit, _error;
    readonly InputBox _threshold, _program, _arguments;
    readonly ToggleSwitch _notify = new(), _admin = new();
    readonly List<(string? Value, string Name)> _sounds;
    SensorKey? _sensor;

    internal AlertEditor(AlertRule rule, Func<Snapshot?> latest, AppSettings settings, Action<AlertRule> test)
    {
        _rule = rule;
        _latest = latest;
        _settings = settings;
        _test = test;
        _sensor = rule.Sensor;
        _sounds = AlertActions.Sounds.ToList();
        if (rule.Sound != null && _sounds.All(s => s.Value != rule.Sound)) _sounds.Add((rule.Sound, Path.GetFileName(rule.Sound)));
        _sounds.Add(("…", "A .wav file…"));

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Alert";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 600);
        KeyPreview = true;

        int targetIndex = rule.Sensor != null ? SensorIndex : Math.Max(0, Array.IndexOf(TargetSeries, rule.Series));
        _target = new Dropdown(TargetNames, targetIndex, 200) { AccessibleName = "Watch" };
        var choose = new PillButton("Choose sensor…", ChooseSensor, 140);
        _sensorName = new Label { AutoSize = false, Size = new Size(380, 22), ForeColor = Theme.TextDim, Tag = "dim" };
        _condition = new Dropdown(new[] { "goes above", "goes below" }, (int)rule.Condition, 130) { AccessibleName = "Condition" };
        _threshold = new InputBox("", "Value", 90, 12) { FieldName = "Threshold" };
        _unit = new Label { AutoSize = true, ForeColor = Theme.TextDim, Tag = "dim" };
        _duration = new Dropdown(Durations.Select(AlertText.Duration).ToArray(), Nearest(Durations, rule.DurationSeconds), 130) { AccessibleName = "For at least" };
        _cooldown = new Dropdown(Cooldowns.Select(m => AlertText.Duration(m * 60)).ToArray(), Nearest(Cooldowns, rule.CooldownMinutes), 130) { AccessibleName = "Repeat at most every" };
        _notify.Checked = rule.Notify;
        _notify.AccessibleName = "Show a notification";
        _sound = new Dropdown(_sounds.Select(s => s.Name).ToArray(), Math.Max(0, _sounds.FindIndex(s => s.Value == rule.Sound)), 260) { AccessibleName = "Sound" };
        _program = new InputBox(rule.RunProgram ?? "", "Program, script or file (optional)", 290, 260) { FieldName = "Program to run" };
        var browse = new PillButton("Browse…", BrowseProgram, 100);
        _arguments = new InputBox(rule.RunArguments ?? "", "Arguments (optional)", 290, 260) { FieldName = "Program arguments" };
        _admin.Checked = rule.RunAsAdmin;
        _admin.AccessibleName = "Run as administrator";
        _error = new Label { AutoSize = false, Size = new Size(560, 22), ForeColor = Theme.Warn };

        int y = 18;
        void Row(string label, params Control[] controls)
        {
            Controls.Add(new Label { Text = label, AutoSize = false, Location = new Point(20, y + 6), Size = new Size(150, 22), ForeColor = Theme.TextDim, Tag = "dim" });
            int x = 180;
            foreach (var c in controls)
            {
                c.Location = new Point(x, c is Label ? y + 6 : y);
                Controls.Add(c);
                x += c.Width + 10;
            }
            y += 44;
        }
        Row("Watch", _target, choose);
        _sensorName.Location = new Point(180, y - 8);
        Controls.Add(_sensorName);
        y += 22;
        Row("Alert when it", _condition, _threshold, _unit);
        Row("For at least", _duration);
        Row("Repeat at most every", _cooldown);
        Controls.Add(new Label
        {
            Text = "Repeats this often while the reading stays past the limit.",
            AutoSize = false, Location = new Point(180, y - 8), Size = new Size(400, 36), ForeColor = Theme.TextFaint,
        });
        y += 34;
        Row("Notification", _notify);
        Row("Sound", _sound);
        Row("Run", _program);
        browse.Location = new Point(_program.Right + 10, _program.Top);
        Controls.Add(browse);
        Row("Arguments", _arguments);
        Row("As administrator", _admin);
        Controls.Add(new Label
        {
            Text = "Programs run as you, without administrator rights, unless this is on.",
            AutoSize = false, Location = new Point(180, y - 8), Size = new Size(400, 20), ForeColor = Theme.TextFaint,
        });
        _error.Location = new Point(20, 512);
        Controls.Add(_error);

        var testButton = new PillButton("Test", () => { if (Collect() is { } r) _test(r); }, 90) { Location = new Point(20, 548), AccessibleName = "Test the alert's actions now" };
        var save = new PillButton("Save", Save, 96) { Accent = true, Location = new Point(378, 548) };
        var cancel = new PillButton("Cancel", Close, 96) { Location = new Point(484, 548) };
        Controls.AddRange(new Control[] { testButton, save, cancel });

        _target.SelectedIndexChanged += (_, _) =>
        {
            if (_target.SelectedIndex == SensorIndex && _sensor == null) ChooseSensor();
            UpdateTarget(resetThreshold: true);
        };
        _sound.SelectedIndexChanged += (_, _) => PickSound();
        ResumeLayout(false);
        PerformLayout();
        // A new rule starts from what the reading is now; an existing one keeps its limit.
        UpdateTarget(resetThreshold: settings.AlertRules.All(r => r.Id != rule.Id));
    }

    static int Nearest(int[] values, int value) => Array.IndexOf(values, values.OrderBy(v => Math.Abs(v - value)).First());

    SensorType CurrentType => _target.SelectedIndex == SensorIndex ? _sensor?.SensorType ?? SensorType.Factor
        : AlertRule.SeriesTargets[TargetSeries[_target.SelectedIndex]].Type;

    void UpdateTarget(bool resetThreshold)
    {
        var type = CurrentType;
        var probe = new AlertRule { Sensor = _target.SelectedIndex == SensorIndex ? _sensor : null, Series = _target.SelectedIndex < SensorIndex ? TargetSeries[_target.SelectedIndex] : null };
        var seriesUnit = probe.Series != null ? AlertRule.SeriesTargets[probe.Series].Unit : null;
        _unit.Text = type == SensorType.Temperature ? Fmt.TempUnit
            : seriesUnit == "health" ? "(1 = Good, 2 = Caution, 3 = Bad)"
            : seriesUnit ?? SensorCatalog.UnitOf(type);
        _sensorName.Text = _target.SelectedIndex == SensorIndex
            ? _sensor == null ? "No sensor chosen yet." : AlertText.Target(probe, _latest(), _settings)
            : "";
        float? threshold = resetThreshold
            ? seriesUnit == "health" ? 1 : (_latest() is { } snap ? probe.ValueIn(snap) : null) switch
            {
                // Start a little above what it reads now, so a fresh rule doesn't go off straight away.
                { } now when type == SensorType.Temperature => MathF.Round(now + 10),
                { } now => MathF.Round(now * 1.1f + 1),
                _ => (float?)null,
            }
            : null;
        threshold ??= _rule.Threshold;
        if (threshold is { } t)
            SetThreshold(type == SensorType.Temperature ? Fmt.TempValue(t) : t);
    }

    void SetThreshold(float display)
    {
        _threshold.Value = display.ToString("0.##", CultureInfo.InvariantCulture);
    }

    void ChooseSensor()
    {
        var entry = SensorPicker.Pick(this, _latest, _settings);
        if (entry != null) _sensor = entry.Key;
        if (_sensor == null) _target.SelectedIndex = 0;
        UpdateTarget(resetThreshold: entry != null);
        if (entry != null && _target.SelectedIndex != SensorIndex) _target.SelectedIndex = SensorIndex;
    }

    void PickSound()
    {
        if (_sounds[_sound.SelectedIndex].Value != "…") return;
        using var dialog = new OpenFileDialog { Filter = "Sound (*.wav)|*.wav", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media") };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _rule.Sound = dialog.FileName;
            AlertActions.PlaySound(dialog.FileName);
        }
        // Dropdown items are fixed once shown; the chosen file is picked up by Collect and listed next time.
        _sound.SelectedIndex = Math.Max(0, _sounds.FindIndex(s => s.Value == _rule.Sound));
    }

    void BrowseProgram()
    {
        using var dialog = new OpenFileDialog { Filter = "Programs and scripts|*.exe;*.bat;*.cmd;*.ps1;*.lnk|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _program.Value = dialog.FileName;
    }

    /// <summary>The rule as entered, or null (with a message) when something is missing.</summary>
    AlertRule? Collect()
    {
        _error.Text = "";
        var r = _rule.Clone();
        if (_target.SelectedIndex == SensorIndex)
        {
            if (_sensor == null)
            {
                _error.Text = "Choose a sensor to watch.";
                return null;
            }
            (r.Sensor, r.Series) = (_sensor, null);
        }
        else
        {
            (r.Sensor, r.Series) = (null, TargetSeries[_target.SelectedIndex]);
        }
        if (!float.TryParse(_threshold.Value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
        {
            _error.Text = "Enter a number for the limit.";
            return null;
        }
        r.Threshold = r.Type == SensorType.Temperature ? Fmt.CelsiusFromDisplay(value) : value;
        r.Condition = (AlertCondition)_condition.SelectedIndex;
        r.DurationSeconds = Durations[_duration.SelectedIndex];
        r.CooldownMinutes = Cooldowns[_cooldown.SelectedIndex];
        r.Notify = _notify.Checked;
        var sound = _sounds[_sound.SelectedIndex].Value;
        r.Sound = sound == "…" ? _rule.Sound : sound;
        r.RunProgram = string.IsNullOrWhiteSpace(_program.Value) ? null : _program.Value.Trim();
        r.RunArguments = string.IsNullOrWhiteSpace(_arguments.Value) ? null : _arguments.Value.Trim();
        r.RunAsAdmin = _admin.Checked;
        if (r.RunProgram != null && !File.Exists(Environment.ExpandEnvironmentVariables(r.RunProgram)) && !r.RunProgram.Contains("://"))
        {
            _error.Text = "That program wasn't found.";
            return null;
        }
        return r;
    }

    AlertRule? _result;

    void Save()
    {
        _result = Collect();
        if (_result == null) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        Close();
        e.Handled = e.SuppressKeyPress = true;
    }

    /// <summary>The edited rule (same id), or null when cancelled.</summary>
    public static AlertRule? Edit(IWin32Window? owner, AlertRule rule, Func<Snapshot?> latest, AppSettings settings, Action<AlertRule> test)
    {
        using var editor = new AlertEditor(rule.Clone(), latest, settings, test);
        if (owner is Form { TopMost: true }) editor.TopMost = true;
        return editor.ShowDialog(owner) == DialogResult.OK ? editor._result : null;
    }
}
