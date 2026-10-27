using Xunit;

namespace PCVitals.Tests;

public sealed class HistoryArchiveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcvitals-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static (string, string) Describe(string key) => (key.ToUpperInvariant(), "%");

    static readonly DateTime Noon = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Records_round_trip()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "a.pvh");
        var records = new[] { new HistoryRecord(100, 0, 1.5f, 1f, 2f), new HistoryRecord(101, 3, -4f, -5f, float.NaN) };
        HistoryFile.Append(path, records);
        HistoryFile.Append(path, new[] { new HistoryRecord(102, 1, 7f, 7f, 7f) });

        var (resolution, back) = HistoryFile.Read(path);
        Assert.Equal(1, resolution);
        Assert.Equal(3, back.Count);
        Assert.Equal(records[0], back[0]);
        Assert.Equal(-5f, back[1].Min);
        Assert.True(float.IsNaN(back[1].Max));
        Assert.Equal(HistoryFile.HeaderSize + 3 * HistoryFile.RecordSize, new FileInfo(path).Length);
    }

    [Fact]
    public void Truncated_last_record_is_ignored_and_trimmed_before_the_next_append()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "a.pvh");
        HistoryFile.Append(path, new[] { new HistoryRecord(1, 0, 1, 1, 1), new HistoryRecord(2, 0, 2, 2, 2) });
        // A crash mid-write leaves part of a record.
        using (var fs = new FileStream(path, FileMode.Open)) fs.SetLength(fs.Length - 7);

        Assert.Single(HistoryFile.Read(path).Records);
        HistoryFile.Append(path, new[] { new HistoryRecord(3, 0, 3, 3, 3) });
        Assert.Equal(new[] { 1, 3 }, HistoryFile.Read(path).Records.Select(r => r.Minute));
    }

    [Fact]
    public void Foreign_or_damaged_files_read_as_empty_and_are_restarted()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "a.pvh");
        File.WriteAllText(path, "not a history file at all");
        Assert.Empty(HistoryFile.Read(path).Records);
        HistoryFile.Append(path, new[] { new HistoryRecord(5, 0, 1, 1, 1) });
        Assert.Single(HistoryFile.Read(path).Records);
        Assert.Empty(HistoryFile.Read(Path.Combine(_dir, "missing.pvh")).Records);
    }

    [Fact]
    public void Downsampling_keeps_mean_min_and_max_per_period()
    {
        var records = Enumerable.Range(0, 10).Select(i => new HistoryRecord(1000 + i, 0, i, i - 1, i + 1)).ToList();
        var five = HistoryFile.Downsample(records, 5);
        Assert.Equal(2, five.Count);
        Assert.Equal(new HistoryRecord(1000, 0, 2f, -1f, 5f), five[0]);
        Assert.Equal(new HistoryRecord(1005, 0, 7f, 4f, 10f), five[1]);
    }

    [Fact]
    public void Archive_appends_by_local_day_and_loads_buckets()
    {
        var archive = new HistoryArchive(_dir);
        for (int i = 0; i < 10; i++)
            archive.Append(Noon.AddMinutes(i), new[] { new MinuteSummary("cpu", i, i, i + 0.5f), new MinuteSummary("/gpu/0/load/0", 50, 40, 60) }, Describe);

        Assert.Single(archive.Days());
        Assert.Equal("CPU", archive.Series[archive.Series.Find("cpu")!.Value]!.Name);

        var range = archive.Load(Noon, Noon.AddMinutes(10), 5);
        Assert.Equal(2, range.Count);
        Assert.Equal(new[] { 2f, 7f }, range.Series["cpu"].Avg);
        Assert.Equal(new[] { 0f, 5f }, range.Series["cpu"].Min);
        Assert.Equal(new[] { 4.5f, 9.5f }, range.Series["cpu"].Max);
        Assert.Equal(Noon.AddMinutes(5), range.TimeOf(1));

        var only = archive.Load(Noon, Noon.AddMinutes(10), 1, new HashSet<string> { "/gpu/0/load/0" });
        Assert.Equal(new[] { "/gpu/0/load/0" }, only.Series.Keys);
    }

    [Fact]
    public void Gaps_load_as_nan()
    {
        var archive = new HistoryArchive(_dir);
        archive.Append(Noon, new[] { new MinuteSummary("cpu", 10, 10, 10) }, Describe);
        archive.Append(Noon.AddMinutes(3), new[] { new MinuteSummary("cpu", 40, 40, 40) }, Describe);
        var avg = archive.Load(Noon, Noon.AddMinutes(4), 1).Series["cpu"].Avg;
        Assert.Equal(10f, avg[0]);
        Assert.True(float.IsNaN(avg[1]) && float.IsNaN(avg[2]));
        Assert.Equal(40f, avg[3]);
    }

    [Fact]
    public void Series_map_survives_reopening()
    {
        var archive = new HistoryArchive(_dir);
        archive.Append(Noon, new[] { new MinuteSummary("ram", 1, 1, 1), new MinuteSummary("cpu", 2, 2, 2) }, Describe);
        var reopened = new HistoryArchive(_dir);
        Assert.Equal(archive.Series.Find("cpu"), reopened.Series.Find("cpu"));
        Assert.Equal(2f, reopened.Load(Noon, Noon.AddMinutes(1), 1).Series["cpu"].Avg[0]);
    }

    [Fact]
    public void Maintenance_deletes_old_days_and_compacts_last_week()
    {
        var archive = new HistoryArchive(_dir);
        var today = new DateOnly(2026, 10, 27);
        foreach (int age in new[] { 0, 7, 8, 29, 30 })
        {
            var day = today.AddDays(-age).ToDateTime(new TimeOnly(10, 0), DateTimeKind.Local);
            for (int m = 0; m < 10; m++) archive.Append(day.AddMinutes(m), new[] { new MinuteSummary("cpu", m, m, m) }, Describe);
        }

        archive.Maintain(today, retentionDays: 30);

        var days = archive.Days().ToDictionary(d => today.DayNumber - d.Day.DayNumber, d => d.Path);
        Assert.Equal(new[] { 0, 7, 8, 29 }, days.Keys.Order());
        Assert.Equal((1, 10), (HistoryFile.Read(days[7]).Resolution, HistoryFile.Read(days[7]).Records.Count));
        var compacted = HistoryFile.Read(days[8]);
        Assert.Equal(5, compacted.Resolution);
        Assert.Equal(new[] { 2f, 7f }, compacted.Records.Select(r => r.Avg));

        // Compacted days still load, weighted as five minutes each.
        var start = today.AddDays(-8).ToDateTime(new TimeOnly(10, 0), DateTimeKind.Local);
        Assert.Equal(4.5f, archive.Load(start, start.AddMinutes(10), 10).Series["cpu"].Avg[0]);
    }

    [Fact]
    public void Delete_all_clears_files_and_series()
    {
        var archive = new HistoryArchive(_dir);
        archive.Append(Noon, new[] { new MinuteSummary("cpu", 1, 1, 1) }, Describe);
        archive.DeleteAll();
        Assert.Empty(archive.Days());
        Assert.Equal(0, archive.Series.Count);
        Assert.Equal(0, archive.SizeBytes());
    }
}

public class HistoryStoreMinuteTests
{
    static readonly DateTime T = new(2026, 10, 26, 12, 0, 0);

    [Fact]
    public void Closed_minutes_report_average_min_and_max_under_the_pushed_name()
    {
        var store = new HistoryStore(10, 60);
        var got = new List<(DateTime, IReadOnlyList<MinuteSummary>)>();
        store.MinuteClosed += (m, v) => got.Add((m, v));
        store.RollMinute(T);
        store.Push("cpu", 10, "/amdcpu/0/load/0");
        store.Push("cpu", 30, "/amdcpu/0/load/0");
        store.Push("power", null);
        store.RollMinute(T.AddMinutes(1));

        var (minute, values) = Assert.Single(got);
        Assert.Equal(T, minute);
        Assert.Equal(new MinuteSummary("cpu", 20, 10, 30), Assert.Single(values));
    }

    [Fact]
    public void Missed_minutes_become_gaps_so_series_stay_aligned()
    {
        var store = new HistoryStore(10, 60);
        store.RollMinute(T);
        store.Push("cpu", 10);
        store.Push("ram", 50);
        store.RollMinute(T.AddMinutes(1));
        store.Push("cpu", 20); // ram stopped reporting
        store.RollMinute(T.AddMinutes(4)); // asleep for two minutes

        var cpu = store.GetMinutes("cpu", 10);
        var ram = store.GetMinutes("ram", 10);
        Assert.Equal(4, cpu.Length);
        Assert.Equal(new[] { 10f, 20f }, cpu[..2]);
        Assert.True(float.IsNaN(cpu[2]) && float.IsNaN(cpu[3]));
        Assert.Equal(4, ram.Length);
        Assert.True(float.IsNaN(ram[1]));
        Assert.Equal(new[] { T, T.AddMinutes(1), T.AddMinutes(2), T.AddMinutes(3) }, store.MinuteTable(new[] { "cpu" }).Minutes);
    }

    [Fact]
    public void Seeded_history_moves_to_the_sensor_when_its_alias_binds()
    {
        var store = new HistoryStore(10, 60);
        var range = new HistoryRange { StartMinute = HistoryFile.ToMinute(T), BucketMinutes = 1, Count = 3 };
        range.Series["cpu"] = (new[] { 5f, 6f, float.NaN }, new float[3], new float[3]);
        store.Seed(range);

        store.RollMinute(T.AddMinutes(5));
        store.Push("cpu", 50, "/amdcpu/0/load/0");
        store.RollMinute(T.AddMinutes(6));

        var cpu = store.GetMinutes("cpu", 60);
        // Seeded 2 minutes, a gap for minutes 2-4, then the live minute 5.
        Assert.Equal(6, cpu.Length);
        Assert.Equal(new[] { 5f, 6f }, cpu[..2]);
        Assert.Equal(50f, cpu[^1]);
        Assert.Equal("/amdcpu/0/load/0", store.Resolve("cpu"));
        Assert.Equal(cpu, store.GetMinutes("/amdcpu/0/load/0", 60));
    }
}
