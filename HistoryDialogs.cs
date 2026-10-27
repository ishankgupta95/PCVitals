using System.Diagnostics;

namespace PCVitals;

/// <summary>Export saved history as CSV: which period, which readings, at what resolution.</summary>
sealed class ExportDialog : Form
{
    static readonly string[] RangeNames = { "Last 24 hours", "Last 7 days", "Last 30 days", "Everything saved", "Chosen days" };
    static readonly string[] SeriesNames = { "Headline readings (CPU, GPU, memory, power, network, drives)", "Favourite and pinned sensors", "Everything saved" };
    static readonly string[] ResolutionNames = { "1 minute", "5 minutes", "1 hour" };
    static readonly int[] Resolutions = { 1, 5, 60 };

    readonly HistoryArchive _archive;
    readonly ISettingsHost _host;
    readonly Dropdown _range, _series, _resolution, _from, _to;
    readonly ToggleSwitch _minMax = new();
    readonly List<DateOnly> _days;

    ExportDialog(HistoryArchive archive, ISettingsHost host)
    {
        _archive = archive;
        _host = host;
        _days = archive.Days().Select(d => d.Day).ToList();
        if (_days.Count == 0) _days.Add(DateOnly.FromDateTime(DateTime.Now));
        var dayNames = _days.Select(d => d.ToString("ddd d MMM yyyy")).ToArray();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Export history";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 380);

        _range = new Dropdown(RangeNames, 0, 300) { AccessibleName = "Period" };
        _from = new Dropdown(dayNames, Math.Max(0, _days.Count - 1), 145) { AccessibleName = "From day" };
        _to = new Dropdown(dayNames, _days.Count - 1, 145) { AccessibleName = "To day" };
        _series = new Dropdown(SeriesNames, 0, 300) { AccessibleName = "Readings" };
        _resolution = new Dropdown(ResolutionNames, 0, 300) { AccessibleName = "Resolution" };
        _minMax.AccessibleName = "Include minimum and maximum";

        int y = 20;
        void Row(string label, Control control)
        {
            Controls.Add(new Label { Text = label, AutoSize = false, Location = new Point(20, y + 6), Size = new Size(170, 22), ForeColor = Theme.TextDim });
            control.Location = new Point(200, y);
            Controls.Add(control);
            y += 46;
        }
        Row("Period", _range);
        Controls.Add(_from);
        Controls.Add(_to);
        _from.Location = new Point(200, y);
        _to.Location = new Point(355, y);
        y += 46;
        Row("Readings", _series);
        Row("Resolution", _resolution);
        Row("Include min and max", _minMax);
        var note = new Label
        {
            Text = $"Saved history: {_days.Count} day{(_days.Count == 1 ? "" : "s")}, {Fmt.Bytes(archive.SizeBytes())}. Days older than a week are kept at 5-minute resolution.",
            AutoSize = false, Location = new Point(20, y), Size = new Size(480, 40), ForeColor = Theme.TextFaint,
        };
        Controls.Add(note);
        var export = new PillButton("Export CSV…", Export, 130) { Accent = true, Location = new Point(270, 324) };
        var cancel = new PillButton("Cancel", Close, 96) { Location = new Point(404, 324) };
        Controls.AddRange(new Control[] { export, cancel });
        _range.SelectedIndexChanged += (_, _) => UpdateDays();
        UpdateDays();
        ResumeLayout(false);
        PerformLayout();
    }

    void UpdateDays() => _from.Enabled = _to.Enabled = _range.SelectedIndex == 4;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyRoundedDarkFrame(Handle);
    }

    (DateTime From, DateTime To) Period()
    {
        var now = DateTime.Now;
        return _range.SelectedIndex switch
        {
            0 => (now.AddHours(-24), now),
            1 => (now.AddDays(-7), now),
            2 => (now.AddDays(-30), now),
            3 => (_days[0].ToDateTime(TimeOnly.MinValue), now),
            _ => (_days[Math.Min(_from.SelectedIndex, _to.SelectedIndex)].ToDateTime(TimeOnly.MinValue),
                  _days[Math.Max(_from.SelectedIndex, _to.SelectedIndex)].AddDays(1).ToDateTime(TimeOnly.MinValue)),
        };
    }

    void Export()
    {
        var (from, to) = Period();
        var snap = _host.Latest;
        var sensors = _host.Settings.TrackedSensors(snap).Select(k => k.Id).ToHashSet();
        var columns = HistoryCsv.Columns(_archive.Series.All, (HistoryCsv.Selection)_series.SelectedIndex, snap?.Summary, sensors);
        if (columns.Count == 0)
        {
            MessageBox.Show(this, _series.SelectedIndex == 1 ? "No favourite or pinned sensor has saved history yet." : "There's no saved history yet.",
                            "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            FileName = $"PC Vitals history {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.csv",
            Filter = "CSV file|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            var range = _archive.Load(from, to, Resolutions[_resolution.SelectedIndex], columns.Select(c => c.Key).ToHashSet());
            int rows;
            using (var w = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true)))
                rows = HistoryCsv.Write(w, range, columns, _minMax.Checked);
            if (rows == 0)
            {
                MessageBox.Show(this, "Nothing was saved in that period.", "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't export: " + ex.Message, "PC Vitals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    public static void Show(IWin32Window owner, HistoryArchive archive, ISettingsHost host)
    {
        using var dialog = new ExportDialog(archive, host);
        dialog.ShowDialog(owner);
    }
}
