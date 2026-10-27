using System.Diagnostics;

namespace PCVitals;

sealed class TrayContext : ApplicationContext, ISettingsHost
{
    sealed record MetricInfo(string Label, string Short, Color Accent);

    // Tray icons sit on the taskbar, so they use the bright accents regardless of the app theme.
    static readonly Dictionary<TrayMetric, MetricInfo> Metrics = new()
    {
        [TrayMetric.CpuTemp] = new("CPU temperature", "CPU", Theme.DarkPalette.Cpu),
        [TrayMetric.CpuLoad] = new("CPU usage", "CPU%", Theme.DarkPalette.Cpu),
        [TrayMetric.GpuTemp] = new("GPU temperature", "GPU", Theme.DarkPalette.Gpu),
        [TrayMetric.GpuLoad] = new("GPU usage", "GPU%", Theme.DarkPalette.Gpu),
        [TrayMetric.RamLoad] = new("Memory usage", "RAM%", Theme.DarkPalette.Ram),
        [TrayMetric.Power] = new("CPU + GPU power", "WATT", Theme.DarkPalette.Power),
    };

    readonly AppSettings _settings;
    readonly bool _isAdmin;
    readonly HardwareService _hw = new(lean: true);
    readonly DashboardForm _dash;
    readonly TaskbarWidget _widget;
    // Strips on other displays' taskbars, keyed by taskbar window (when "Show on all displays" is on).
    readonly Dictionary<IntPtr, TaskbarWidget> _extraWidgets = new();
    int _placementTicks;
    readonly ContextMenuStrip _menu;
    readonly Hotkeys _hotkeys = new();
    readonly System.Windows.Forms.Timer _placementTimer = new() { Interval = 1000 };
    readonly Dictionary<TrayMetric, NotifyIcon> _icons = new();
    readonly Dictionary<TrayMetric, (IntPtr Handle, string Key)> _hIcons = new();
    readonly Dictionary<int, ToolStripMenuItem> _intervalItems = new();
    readonly AlertEngine _alertEngine = new();
    readonly AlertLog _alertLog = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCVitals", "alert-history.json"));
    /// <summary>Copies of the rules for the poll thread; replaced (not changed) when settings change.</summary>
    AlertRule[] _alertRules = Array.Empty<AlertRule>();
    NotifyIcon? _logo;
    IntPtr _logoHandle;
    ToolStripMenuItem _autostartItem = null!, _widgetItem = null!;
    SettingsForm? _settingsForm;
    bool _taskbarLight = Theme.TaskbarIsLight();
    bool _exiting;
    int _snapshots;
    bool _calibrationStarted;
    string? _hotkeyRegistered;
    WidgetKind? _alertPage;
    Action? _balloonClick;

    public TrayContext(AppSettings settings, bool isAdmin)
    {
        _settings = settings;
        _isAdmin = isAdmin;

        _dash = new DashboardForm(settings, isAdmin) { HistoryProvider = _hw.GetHistory, MinuteHistoryProvider = _hw.GetMinuteHistory };
        _ = _dash.Handle; // create the window now so we can marshal sensor updates onto the UI thread
        _dash.RestartAsAdminRequested += RestartElevated;
        _dash.SettingsRequested += () => OpenSettings();
        _dash.SensorsChanged += () =>
        {
            _settings.Save();
            SettingsChanged();
            _settingsForm?.SensorItemsChanged();
        };
        _dash.ResetMinMaxRequested += _hw.ResetMinMax;
        _dash.AddAlertRequested += AddAlertFor;
        // Poll everything only while the dashboard is on screen; minimised or closed, go back to the lean polling.
        void DashboardShownChanged()
        {
            if (_hw.DetailedMode == _dash.OnScreen) return;
            _hw.DetailedMode = _dash.OnScreen;
            if (_dash.OnScreen)
            {
                if (_hw.Latest is { } latest) _dash.SetSnapshot(latest);
                _hw.Poke();
            }
            else
            {
                _longRange = null; // the 7 d / 30 d data is only kept while the dashboard is open
                Native.TrimWorkingSet();
            }
        }
        _dash.VisibleChanged += (_, _) => DashboardShownChanged();
        _dash.Resize += (_, _) => DashboardShownChanged();

        _menu = BuildMenu();
        _widget = new TaskbarWidget(settings, _menu) { LightTaskbar = _taskbarLight };
        _widget.ItemClicked += v => ToggleDashboard(v.Kind, pageKey: v.Page);
        // Windows closing a window for us (shutdown, Task Scheduler "End") means the app should exit.
        _widget.FormClosed += (_, _) => Exit();
        _dash.FormClosed += (_, _) => Exit();
        SyncIcons();

        _hotkeys.Pressed += () => ToggleDashboard(null, fromHotkey: true);
        ApplyHotkey();
        Theme.Changed += OnThemeChanged;

        // The taskbar can move/resize (tray icons added, explorer restart, fullscreen games)
        // independently of sensor updates; this cheap check keeps the widget placed correctly.
        // An auto-hiding taskbar slides in and out in ~200 ms, so poll faster only in that case.
        _placementTimer.Tick += (_, _) =>
        {
            if (_exiting || _widget.IsDisposed) return;
            _placementTimer.Interval = _widget.CheckPlacement() ? 150 : 1000;
            foreach (var extra in _extraWidgets.Values) extra.CheckPlacement();
            // Displays come and go rarely; look for added/removed taskbars every few seconds.
            if (++_placementTicks % 5 == 0) SyncExtraWidgets();
        };
        _placementTimer.Start();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ListenForExitSignal();

        ApplyToService();
        foreach (var (gpu, ratio) in _settings.GpuPowerPerPercent) _hw.PowerPerPercent[gpu] = ratio;
        _hw.PowerCalibrationChanged += () =>
        {
            try { _dash.BeginInvoke(SavePowerCalibration); }
            catch (InvalidOperationException) { }
        };
        ApplyHistory(seed: true);
        ApplyLatency();
        // Alerts are judged on the poll thread (cheap); their actions run on the UI thread.
        _hw.AddConsumer(new SnapshotConsumer(snap =>
        {
            var events = _alertEngine.Evaluate(Volatile.Read(ref _alertRules), snap.Time, r => r.ValueIn(snap));
            if (events.Count == 0 || _exiting) return;
            try { _dash.BeginInvoke(() => OnAlerts(events, snap)); }
            catch (InvalidOperationException) { }
        }));
        _hw.AddConsumer(new SnapshotConsumer(snap =>
        {
            if (_exiting) return;
            try { _dash.BeginInvoke(() => OnSnapshot(snap)); }
            catch (InvalidOperationException) { }
        }));
        _hw.Start();
    }

    // ---- History on disk ------------------------------------------------------------------

    HistoryArchive? _archive;
    HistoryRecorder? _recorder;
    HistoryRange? _longRange;
    int _longRangeSeconds;
    DateTime _longRangeLoaded;
    bool _longRangeLoading;

    /// <summary>Starts or stops saving history to match the settings.</summary>
    void ApplyHistory(bool seed = false)
    {
        if (_settings.HistoryEnabled && _recorder == null)
        {
            try
            {
                _archive ??= new HistoryArchive(HistoryArchive.DefaultDir);
                _recorder = new HistoryRecorder(_archive, key => HeadlineSeries.Describe(key, _hw.Latest)) { RetentionDays = _settings.HistoryRetentionDays };
                _hw.History.MinuteClosed += _recorder.Enqueue;
                var archive = _archive;
                // On the poll thread before its first reading, so the 1 h / 24 h graphs continue where they left off.
                if (seed) _hw.Enqueue(_ => _hw.History.Seed(archive.Load(DateTime.Now.AddHours(-24), DateTime.Now, 1)));
            }
            catch (Exception ex)
            {
                Program.Log("Couldn't start saving history: " + ex);
            }
        }
        else if (!_settings.HistoryEnabled && _recorder != null)
        {
            _hw.History.MinuteClosed -= _recorder.Enqueue;
            _recorder.Dispose();
            _recorder = null;
        }
        if (_recorder != null && _recorder.RetentionDays != _settings.HistoryRetentionDays)
        {
            _recorder.RetentionDays = _settings.HistoryRetentionDays;
            _recorder.MaintainNow();
        }
        _dash.LongHistoryProvider = _recorder != null ? LongHistory : null;
        _longRange = null;
    }

    /// <summary>
    /// The 7 d / 30 d graph data: loaded in the background (5-minute or hourly buckets), kept for a
    /// minute while the dashboard shows it. Null while loading.
    /// </summary>
    HistoryRange? LongHistory(int seconds)
    {
        bool fresh = _longRange != null && _longRangeSeconds == seconds && (DateTime.Now - _longRangeLoaded).TotalSeconds < 60;
        if (fresh || _longRangeLoading || _archive is not { } archive) return _longRangeSeconds == seconds ? _longRange : null;
        _longRangeLoading = true;
        int bucket = seconds > 7 * 86400 ? 60 : 5;
        Task.Run(() =>
        {
            HistoryRange? range = null;
            try { range = archive.Load(DateTime.Now.AddSeconds(-seconds), DateTime.Now, bucket); }
            catch (Exception ex) { Program.Log("Couldn't read history: " + ex); }
            try
            {
                _dash.BeginInvoke(() =>
                {
                    _longRange = range;
                    _longRangeSeconds = seconds;
                    _longRangeLoaded = DateTime.Now;
                    _longRangeLoading = false;
                    _dash.RefreshView();
                });
            }
            catch (InvalidOperationException) { }
        });
        return _longRangeSeconds == seconds ? _longRange : null;
    }

    // ---- Ping ---------------------------------------------------------------------------------

    LatencyMonitor? _latency;
    string? _latencyHost;

    /// <summary>Starts, restarts (new host) or stops the opt-in ping to match the settings.</summary>
    void ApplyLatency()
    {
        var host = _settings.PingEnabled ? _settings.PingHost.Trim() : null;
        if (host == _latencyHost && (_latency != null) == _settings.PingEnabled) return;
        _hw.Latency = null;
        _latency?.Dispose();
        _latency = _settings.PingEnabled ? new LatencyMonitor(host) : null;
        _latencyHost = host;
        _hw.Latency = _latency;
    }

    public long HistorySizeBytes => _archive?.SizeBytes() ?? 0;

    public Task DeleteHistoryAsync()
    {
        _longRange = null;
        if (_recorder != null) return _recorder.DeleteAllAsync();
        _archive ??= new HistoryArchive(HistoryArchive.DefaultDir);
        _archive.DeleteAll();
        return Task.CompletedTask;
    }

    public HistoryArchive? Archive => _settings.HistoryEnabled ? _archive : null;

    /// <summary>"Add alert…" from a sensor in the dashboard: the rule editor, starting on that sensor.</summary>
    void AddAlertFor(SensorKey key)
    {
        var rule = AlertEditor.Edit(_dash, new AlertRule { Sensor = key }, () => _hw.Latest, _settings, TestAlert);
        if (rule == null) return;
        _settings.AlertRules.Add(rule);
        _settings.Save();
        SettingsChanged();
        _settingsForm?.AlertsChanged();
    }

    public AlertLog AlertLog => _alertLog;
    public DateTime? LastFired(string ruleId) => _alertEngine.LastFired(ruleId);

    public void TestAlert(AlertRule rule)
    {
        var snap = _hw.Latest;
        var value = snap != null && rule.ValueIn(snap) is { } v ? AlertText.Value(rule, v) : "not available";
        RunAlertActions(rule, AlertText.Target(rule, snap, _settings), $"Test: now {value}; alerts when {AlertText.Condition(rule)}.", DateTime.Now, snap);
        _settingsForm?.AlertsChanged();
    }

    void ApplyToService()
    {
        _hw.IntervalMs = _settings.IntervalMs;
        _hw.PreferredGpuId = _settings.PrimaryGpuId;
        bool Shown(WidgetKind k) => _settings.WidgetEnabled && _settings.WidgetItems.Any(i => i.Enabled && i.Kind == k);
        _hw.NeedStorage = Shown(WidgetKind.Disk);
        _hw.NeedBoard = Shown(WidgetKind.Board);
        _hw.NeedSecondGpu = Shown(WidgetKind.SecondGpu);
        _hw.NeedBattery = Shown(WidgetKind.Battery);
        _hw.TrackedSensors = _settings.TrackedSensors(_hw.Latest);
        Volatile.Write(ref _alertRules, _settings.AlertRules.Select(r => r.Clone()).ToArray());
    }

    void SyncExtraWidgets()
    {
        var wanted = _settings.WidgetEnabled && _settings.ShowOnAllDisplays
            ? Native.FindSecondaryTaskbars().Select(t => t.Handle).ToHashSet()
            : new HashSet<IntPtr>();
        foreach (var handle in _extraWidgets.Keys.Where(h => !wanted.Contains(h)).ToList())
        {
            var w = _extraWidgets[handle];
            _extraWidgets.Remove(handle);
            w.AllowClose = true;
            w.Close();
            w.Dispose();
        }
        foreach (var handle in wanted.Where(h => !_extraWidgets.ContainsKey(h)))
        {
            var w = new TaskbarWidget(_settings, _menu, handle) { LightTaskbar = _taskbarLight };
            w.ItemClicked += v => ToggleDashboard(v.Kind, pageKey: v.Page);
            _extraWidgets[handle] = w;
            if (_hw.Latest is { } snap) w.SetSnapshot(snap);
        }
    }

    /// <summary>On a laptop, turn the battery item on the first time a battery shows up.</summary>
    void EnableBatteryItemOnce(Snapshot snap)
    {
        if (_settings.BatteryItemAutoEnabled || snap.Summary.Battery == null) return;
        _settings.BatteryItemAutoEnabled = true;
        var item = _settings.WidgetItems.FirstOrDefault(i => i.Kind == WidgetKind.Battery);
        if (item != null) item.Enabled = true;
        _settings.Save();
        SettingsChanged();
    }

    void ApplyHotkey()
    {
        if (_hotkeyRegistered == _settings.Hotkey) return;
        _hotkeyRegistered = _settings.Hotkey;
        if (!_hotkeys.Set(_settings.Hotkey))
            ShowNotification("Shortcut unavailable", $"{_settings.Hotkey} is already used by another app. Pick a different shortcut in Settings.", ToolTipIcon.Info, null);
    }

    void OnSnapshot(Snapshot snap)
    {
        if (_exiting) return;
        foreach (var metric in _icons.Keys) UpdateIcon(metric, snap.Summary);
        UpdateLogoTooltip(snap.Summary);
        _widget.SetSnapshot(snap);
        foreach (var extra in _extraWidgets.Values) extra.SetSnapshot(snap);
        if (_dash.OnScreen) _dash.SetSnapshot(snap);
        _settingsForm?.SetSnapshot(snap);

        _snapshots++;
        if (_snapshots == 1)
        {
            StartPowerCalibrationIfNeeded(snap);
            NoticeMissingPawnIo();
        }
        // Favourites are stored by id; their keys come from a snapshot (drives appear in the second).
        if (_snapshots <= 2) ApplyToService();
        // The battery group is discovered with the deferred groups on the second poll.
        if (_snapshots == 2) EnableBatteryItemOnce(snap);
        // Discovery (drives especially) allocates a lot once. The deferred groups are found on the
        // second poll, so give everything back after the third.
        if (_snapshots == 3) Native.TrimWorkingSet();
    }

    // ---- Alerts -----------------------------------------------------------------------------

    void OnAlerts(List<AlertEvent> events, Snapshot snap)
    {
        foreach (var e in events)
        {
            var name = AlertText.Target(e.Rule, snap, _settings);
            var value = AlertText.Value(e.Rule, e.Value);
            if (!e.Fired)
            {
                _alertLog.Add(new AlertLog.Entry(e.Time, e.Rule.Id, name, $"Back to {value}"));
                continue;
            }
            RunAlertActions(e.Rule, name, $"Now {value}: {AlertText.Condition(e.Rule)}.", e.Time, snap);
        }
        _settingsForm?.AlertsChanged();
    }

    void RunAlertActions(AlertRule rule, string name, string text, DateTime time, Snapshot? snap)
    {
        if (AlertActions.RunProgram(rule) is { } error) text += $" Couldn't run {Path.GetFileName(rule.RunProgram)}: {error}";
        AlertActions.PlaySound(rule.Sound);
        var page = AlertText.Page(rule, snap);
        if (rule.Notify)
            ShowNotification($"{name} is {(rule.Condition == AlertCondition.Above ? "high" : "low")}", text, ToolTipIcon.Warning, null,
                             () => ShowDashboard(null, false, page));
        _alertLog.Add(new AlertLog.Entry(time, rule.Id, name, text));
    }

    void ShowNotification(string title, string text, ToolTipIcon icon, WidgetKind? page, Action? onClick = null)
    {
        var notifyIcon = _logo ?? _icons.Values.FirstOrDefault();
        if (notifyIcon == null) return;
        _alertPage = page;
        _balloonClick = onClick;
        notifyIcon.ShowBalloonTip(10000, title, text, icon);
    }

    /// <summary>
    /// Without PawnIO, CPU temperature/power and motherboard sensors silently read nothing. Tell the
    /// user once (the dashboard keeps a banner for it).
    /// </summary>
    void NoticeMissingPawnIo()
    {
        if (!_isAdmin || _settings.PawnIoNoticeShown || HardwareService.PawnIoStatus().Installed != false) return;
        _settings.PawnIoNoticeShown = true;
        _settings.Save();
        ShowNotification("CPU temperatures need PawnIO",
                         "Install the free PawnIO driver (click here), then restart PC Vitals to see CPU temperature, power and motherboard sensors.",
                         ToolTipIcon.Info, null, () =>
                         {
                             try { Process.Start(new ProcessStartInfo(DashboardView.PawnIoUrl) { UseShellExecute = true }); }
                             catch { }
                         });
    }

    // ---- GPU power calibration --------------------------------------------------------------

    void SavePowerCalibration()
    {
        lock (_hw.PowerPerPercent)
            _settings.GpuPowerPerPercent = new Dictionary<string, float>(_hw.PowerPerPercent);
        _settings.Save();
    }

    /// <summary>
    /// First run on an NVIDIA card: learn its watts-per-percent ratio in a short-lived child process
    /// that loads NVML, so this long-running process never has to (see HardwareService).
    /// </summary>
    void StartPowerCalibrationIfNeeded(Snapshot snap, bool force = false)
    {
        var missing = snap.Hardware.Where(h => h.Type == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia)
                                   .Any(h => force || !_settings.GpuPowerPerPercent.ContainsKey(_hw.GpuKey(h.Id, h.Name)));
        if (_calibrationStarted || !_hw.GpuPowerEstimated || !missing) return;
        _calibrationStarted = true;
        var file = Path.Combine(Path.GetTempPath(), $"pcvitals-calibration-{Environment.ProcessId}.txt");
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--calibrate-gpu-power \"{file}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null)
            {
                _calibrationStarted = false;
                return;
            }
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                try
                {
                    foreach (var line in File.ReadAllLines(file))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length == 2 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                                                 System.Globalization.CultureInfo.InvariantCulture, out var ratio) && ratio > 0)
                            lock (_hw.PowerPerPercent) _hw.PowerPerPercent[parts[0]] = ratio;
                    }
                    File.Delete(file);
                    _dash.BeginInvoke(SavePowerCalibration);
                }
                catch
                {
                }
                _calibrationStarted = false;
                p.Dispose();
            };
        }
        catch (Exception ex)
        {
            // Without calibration GPU watts just stay "--" until the dashboard is opened.
            _calibrationStarted = false;
            Program.Log("calibration failed to start: " + ex);
        }
    }

    /// <summary>Lets a newer build replace this instance (see Program's --replace).</summary>
    void ListenForExitSignal()
    {
        var signal = Program.CreateExitSignal();
        if (signal == null) return;
        new Thread(() =>
        {
            signal.WaitOne();
            if (_exiting) return;
            try { _dash.BeginInvoke(Exit); }
            catch (InvalidOperationException) { }
        }) { IsBackground = true, Name = "Exit signal" }.Start();
    }

    // ---- ISettingsHost ----------------------------------------------------------------------

    public AppSettings Settings => _settings;
    public bool IsAdmin => _isAdmin;
    public Snapshot? Latest => _hw.Latest;
    public string? WidgetFitNote => _widget.FitNote;
    public bool GpuPowerEstimated => _hw.GpuPowerEstimated;

    public IReadOnlyDictionary<string, float> PowerRatios
    {
        get
        {
            lock (_hw.PowerPerPercent) return new Dictionary<string, float>(_hw.PowerPerPercent);
        }
    }

    public void SettingsChanged()
    {
        ApplyToService();
        ApplyHistory();
        ApplyLatency();
        _hw.Poke();
        Fmt.Fahrenheit = _settings.Fahrenheit;
        ApplyHotkey();
        SyncIcons();
        foreach (var metric in _icons.Keys) UpdateIcon(metric, _hw.Latest?.Summary, force: true);
        _widget.Redraw(force: true);
        SyncExtraWidgets();
        foreach (var extra in _extraWidgets.Values) extra.Redraw(force: true);
        _dash.ApplyTheme();
        _widgetItem.Checked = _settings.WidgetEnabled;
        foreach (var (value, item) in _intervalItems) item.Checked = value == _settings.IntervalMs;
        // Last: may rebuild the settings window in the new colours.
        Theme.Apply(_settings.ColorTheme);
    }

    public int ExportHistory(string path) => _hw.ExportCsv(path);

    public void RecalibrateGpuPower()
    {
        if (_hw.Latest is { } snap) StartPowerCalibrationIfNeeded(snap, force: true);
    }

    public void ReopenSettings(string page)
    {
        if (_settingsForm is not { IsDisposed: false } form) return;
        var bounds = form.Bounds;
        form.Close();
        OpenSettings(page, bounds);
    }

    // ---- Tray icons -------------------------------------------------------------------------

    void SyncIcons()
    {
        foreach (var metric in _icons.Keys.ToList())
        {
            if (_settings.TrayMetrics.Contains(metric)) continue;
            _icons[metric].Visible = false;
            _icons[metric].Dispose();
            _icons.Remove(metric);
            if (_hIcons.Remove(metric, out var h)) TrayIconRenderer.Destroy(h.Handle);
        }

        // Windows orders tray icons by creation, so (re)create in the canonical metric order.
        foreach (var metric in Enum.GetValues<TrayMetric>())
        {
            if (!_settings.TrayMetrics.Contains(metric) || _icons.ContainsKey(metric)) continue;
            var icon = CreateNotifyIcon();
            _icons[metric] = icon;
            UpdateIcon(metric, _hw.Latest?.Summary, force: true);
            icon.Visible = true;
        }

        // Always keep one way into the app: the logo icon when no metric icons are shown.
        bool wantLogo = _icons.Count == 0;
        if (wantLogo && _logo == null)
        {
            _logoHandle = TrayIconRenderer.RenderLogo(SystemInformation.SmallIconSize.Width);
            _logo = CreateNotifyIcon();
            _logo.Icon = Icon.FromHandle(_logoHandle);
            UpdateLogoTooltip(_hw.Latest?.Summary);
            _logo.Visible = true;
        }
        else if (!wantLogo && _logo != null)
        {
            _logo.Visible = false;
            _logo.Dispose();
            _logo = null;
            TrayIconRenderer.Destroy(_logoHandle);
            _logoHandle = IntPtr.Zero;
        }
    }

    NotifyIcon CreateNotifyIcon()
    {
        var icon = new NotifyIcon { ContextMenuStrip = _menu, Text = "PC Vitals" };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleDashboard(null);
        };
        icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonClick is { } click) click();
            else if (_alertPage is { } page) ShowDashboard(page);
        };
        return icon;
    }

    void UpdateLogoTooltip(Summary? s)
    {
        if (_logo != null) _logo.Text = Truncate(BuildTooltip(null, s), 127);
    }

    void UpdateIcon(TrayMetric metric, Summary? s, bool force = false)
    {
        if (!_icons.TryGetValue(metric, out var icon)) return;
        var info = Metrics[metric];

        float? value = metric switch
        {
            TrayMetric.CpuTemp => s?.CpuTemp,
            TrayMetric.CpuLoad => s?.CpuLoad,
            TrayMetric.GpuTemp => s?.Gpu?.Temp,
            TrayMetric.GpuLoad => s?.Gpu?.Load,
            TrayMetric.RamLoad => s?.RamLoad,
            TrayMetric.Power => s?.TotalPower,
            _ => null,
        };
        bool isTemp = metric is TrayMetric.CpuTemp or TrayMetric.GpuTemp;
        float max = metric == TrayMetric.Power ? 600 : 100;
        bool withLabel = _settings.TrayStyle == TrayIconStyle.LabelAndNumber;

        var accent = Theme.OnTaskbar(info.Accent, _taskbarLight);
        var normal = _taskbarLight ? Color.FromArgb(20, 20, 24) : Color.White;
        var textColor = isTemp ? Theme.TaskbarTemp(value, _taskbarLight, normal) : normal;
        var shown = isTemp && value is { } c ? Fmt.TempValue(c) : value;
        var text = shown is { } v ? Math.Min(v, 999).ToString("0") : "--";
        float? level = withLabel ? null : value / max;

        // Only rebuild the HICON when what it shows actually changes.
        var key = $"{text}|{textColor.ToArgb()}|{level:0.00}|{withLabel}|{_taskbarLight}";
        if (!force && _hIcons.TryGetValue(metric, out var cur) && cur.Key == key) return;

        var hIcon = TrayIconRenderer.Render(withLabel ? info.Short : null, text, textColor, accent, level,
                                            SystemInformation.SmallIconSize.Width);
        icon.Icon = Icon.FromHandle(hIcon);
        if (_hIcons.TryGetValue(metric, out var old)) TrayIconRenderer.Destroy(old.Handle);
        _hIcons[metric] = (hIcon, key);

        icon.Text = Truncate(BuildTooltip(info.Label, s), 127);
    }

    static string BuildTooltip(string? label, Summary? s)
    {
        if (s == null) return "PC Vitals\nReading sensors…";
        var lines = new List<string>();
        if (label != null) lines.Add($"PC Vitals · {label}");
        lines.Add($"CPU {s.CpuShortName}: {Fmt.Num(s.CpuLoad)}% · {Fmt.Temp(s.CpuTemp)} · {Fmt.Num(s.CpuPower)} W");
        if (s.Gpu is { Sleeping: true } asleep)
            lines.Add($"GPU {asleep.ShortName}: sleeping");
        else if (s.Gpu is { } g)
            lines.Add($"GPU {g.ShortName}{(g.Integrated ? " (iGPU)" : "")}: {Fmt.Num(g.Load)}% · {Fmt.Temp(g.Temp)} · {Fmt.Num(g.Power)} W");
        lines.Add($"RAM {Fmt.Num(s.RamUsedGb, "0.0")}/{Fmt.Num(s.RamTotalGb, "0")} GB ({Fmt.Num(s.RamLoad)}%)");
        if (s.ChipsetTemp is { } chipset) lines.Add($"Chipset {Fmt.Temp(chipset)}");
        return string.Join("\n", lines);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---- Theme ------------------------------------------------------------------------------

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        // "System" follows Windows' app theme; re-evaluate it.
        Theme.Apply(_settings.ColorTheme);
        var light = Theme.TaskbarIsLight();
        if (light == _taskbarLight) return;
        _taskbarLight = light;
        _widget.LightTaskbar = light;
        _widget.Redraw(force: true);
        foreach (var extra in _extraWidgets.Values)
        {
            extra.LightTaskbar = light;
            extra.Redraw(force: true);
        }
        foreach (var metric in _icons.Keys) UpdateIcon(metric, _hw.Latest?.Summary, force: true);
    }

    void OnThemeChanged()
    {
        _dash.ApplyTheme();
        foreach (ToolStripItem item in _menu.Items) ApplyDark(item);
        if (_settingsForm is { IsDisposed: false } form) form.ApplyTheme(); // recoloured in place, no reopen
        // The floating bar (auto-hidden taskbar) uses the app theme.
        _widget.Redraw(force: true);
        foreach (var extra in _extraWidgets.Values) extra.Redraw(force: true);
    }

    void BeginInvokeSafe(Action action)
    {
        try { _dash.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    // ---- Dashboard / settings ---------------------------------------------------------------

    /// <summary>
    /// Taskbar item, tray icon or shortcut: bring the dashboard up on that page. If it is already the
    /// window in front and showing that page, minimise it, so the same click toggles it like a
    /// taskbar button.
    /// </summary>
    void ToggleDashboard(WidgetKind? page, bool fromHotkey = false, string? pageKey = null)
    {
        bool switched = page != null && _dash.OnScreen && _dash.SwitchTo(page.Value, pageKey);
        if (_dash.IsForeground && !switched)
        {
            _dash.WindowState = FormWindowState.Minimized;
            return;
        }
        ShowDashboard(page, fromHotkey, pageKey);
    }

    void ShowDashboard(WidgetKind? page = null, bool nearTray = false, string? pageKey = null)
    {
        if (_hw.Latest is { } snap) _dash.SetSnapshot(snap);
        var anchor = Cursor.Position;
        if (nearTray && Screen.PrimaryScreen is { } screen)
            anchor = new Point(screen.WorkingArea.Right - 20, screen.WorkingArea.Bottom - 20);
        _dash.ShowAndActivate(anchor, page, pageKey);
    }

    void OpenSettings(string? page = null, Rectangle? bounds = null)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(this, page);
        if (bounds is { } b)
        {
            _settingsForm.StartPosition = FormStartPosition.Manual;
            _settingsForm.Bounds = b;
        }
        _settingsForm.FormClosed += (sender, _) =>
        {
            if (ReferenceEquals(_settingsForm, sender)) _settingsForm = null;
            Native.TrimWorkingSet();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    // ---- Menu -------------------------------------------------------------------------------

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };

        var open = new ToolStripMenuItem("Open dashboard", null, (_, _) => ShowDashboard());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());

        _widgetItem = new ToolStripMenuItem("Show stats on taskbar") { Checked = _settings.WidgetEnabled };
        _widgetItem.Click += (_, _) =>
        {
            _settings.WidgetEnabled = !_settings.WidgetEnabled;
            _settings.Save();
            SettingsChanged();
        };
        menu.Items.Add(_widgetItem);

        var interval = new ToolStripMenuItem("Refresh every");
        foreach (var ms in new[] { 500, 1000, 2000, 5000 })
        {
            var item = new ToolStripMenuItem(ms < 1000 ? $"{ms / 1000.0:0.0} seconds" : $"{ms / 1000} second{(ms > 1000 ? "s" : "")}")
            {
                Checked = _settings.IntervalMs == ms,
            };
            item.Click += (_, _) =>
            {
                _settings.IntervalMs = ms;
                _settings.Save();
                SettingsChanged();
            };
            _intervalItems[ms] = item;
            interval.DropDownItems.Add(item);
        }
        menu.Items.Add(interval);

        _autostartItem = new ToolStripMenuItem(_isAdmin ? "Start with Windows" : "Start with Windows (needs admin)")
        {
            Enabled = _isAdmin,
        };
        _autostartItem.Click += (_, _) => ToggleAutostart();
        menu.Items.Add(_autostartItem);
        menu.Opening += (_, _) => { if (_isAdmin) _autostartItem.Checked = Autostart.IsEnabled(); };

        if (!_isAdmin)
            menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null, (_, _) => RestartElevated()));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        foreach (ToolStripItem item in menu.Items) ApplyDark(item);
        return menu;
    }

    static void ApplyDark(ToolStripItem item)
    {
        item.ForeColor = Theme.Text;
        if (item.Owner != null) item.Owner.BackColor = Theme.Surface;
        if (item is ToolStripMenuItem mi)
        {
            mi.DropDown.BackColor = Theme.Surface;
            if (mi.DropDown is ToolStripDropDownMenu dd)
            {
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
                dd.Renderer = new DarkMenuRenderer();
            }
            foreach (ToolStripItem child in mi.DropDownItems) ApplyDark(child);
        }
    }

    void ToggleAutostart()
    {
        bool ok = Autostart.IsEnabled() ? Autostart.Disable() : Autostart.Enable();
        if (!ok)
            MessageBox.Show("Couldn't update the startup task.", "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    void RestartElevated()
    {
        try
        {
            Program.ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Program.AcquireSingleInstance(); // user declined UAC; keep running as we are
        }
    }

    void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _placementTimer.Stop();
        Theme.Changed -= OnThemeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _hotkeys.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Visible = false;
            icon.Dispose();
        }
        foreach (var h in _hIcons.Values) TrayIconRenderer.Destroy(h.Handle);
        _icons.Clear();
        _hIcons.Clear();
        if (_logo != null)
        {
            _logo.Visible = false;
            _logo.Dispose();
            TrayIconRenderer.Destroy(_logoHandle);
        }
        _settingsForm?.Close();
        foreach (var extra in _extraWidgets.Values)
        {
            extra.AllowClose = true;
            extra.Close();
            extra.Dispose();
        }
        _extraWidgets.Clear();
        _widget.AllowClose = true;
        _dash.AllowClose = true;
        if (!_widget.IsDisposed) _widget.Close();
        _widget.Dispose();
        _hw.Dispose();
        _recorder?.Dispose();
        _latency?.Dispose();
        if (!_dash.IsDisposed) _dash.Close();
        _dash.Dispose();
        ExitThread();
    }
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new ThemeColors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.TextDim;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using var pen = new Pen(Theme.Cpu, Math.Max(1.5f, r.Height / 8f));
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
            new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f),
        });
    }

    sealed class ThemeColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfaceHover;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.Surface;
        public override Color CheckSelectedBackground => Theme.SurfaceHover;
        public override Color CheckPressedBackground => Theme.SurfaceHover;
        public override Color ButtonSelectedBorder => Theme.SurfaceHover;
    }
}
