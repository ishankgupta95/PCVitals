using Xunit;

namespace PCVitals.Tests;

public class HistoryCsvTests
{
    static readonly DateTime T = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Local);

    static HistoryRange Range()
    {
        var r = new HistoryRange { StartMinute = HistoryFile.ToMinute(T), BucketMinutes = 60, Count = 3 };
        r.Series["cpu"] = (new[] { 12.345f, float.NaN, 30f }, new[] { 1f, float.NaN, 20f }, new[] { 50f, float.NaN, 40f });
        r.Series["ram"] = (new[] { 40f, float.NaN, float.NaN }, new[] { 39f, float.NaN, float.NaN }, new[] { 41f, float.NaN, float.NaN });
        return r;
    }

    [Fact]
    public void Writes_time_and_columns_and_skips_empty_periods()
    {
        var w = new StringWriter();
        int rows = HistoryCsv.Write(w, Range(), new[] { ("cpu", "CPU usage (%)"), ("ram", "Memory, used (%)"), ("missing", "Gone") }, minMax: false);
        Assert.Equal(2, rows);
        var lines = w.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time,CPU usage (%),\"Memory, used (%)\"", lines[0]);
        Assert.Equal("2026-10-20 09:00,12.35,40", lines[1]);
        Assert.Equal("2026-10-20 11:00,30,", lines[2]);
    }

    [Fact]
    public void Min_and_max_columns_follow_each_series()
    {
        var w = new StringWriter();
        HistoryCsv.Write(w, Range(), new[] { ("cpu", "CPU") }, minMax: true);
        var lines = w.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time,CPU,CPU min,CPU max", lines[0]);
        Assert.Equal("2026-10-20 09:00,12.35,1,50", lines[1]);
    }

    [Fact]
    public void Column_selection()
    {
        var recorded = new List<SeriesRegistry.Entry>
        {
            new("/amdcpu/0/temperature/2", "Ryzen · CCD1", "°C"), new("ram", "Memory usage", "%"), new("cpu", "CPU usage", "%"),
            new("gpu:/gpu-nvidia/9", "Old GPU usage", "%"), new("/lpc/0/fan/1", "Fan", "RPM"),
        };
        var fav = new HashSet<string> { "/amdcpu/0/temperature/2" };

        Assert.Equal(new[] { "cpu", "ram", "gpu:/gpu-nvidia/9" },
                     HistoryCsv.Columns(recorded, HistoryCsv.Selection.Headline, null, fav).Select(c => c.Key));
        Assert.Equal(new[] { "/amdcpu/0/temperature/2" }, HistoryCsv.Columns(recorded, HistoryCsv.Selection.Sensors, null, fav).Select(c => c.Key));
        Assert.Equal(5, HistoryCsv.Columns(recorded, HistoryCsv.Selection.All, null, fav).Count);
        Assert.Equal("CPU usage (%)", HistoryCsv.Columns(recorded, HistoryCsv.Selection.Headline, null, fav)[0].Title);
    }

    [Fact]
    public void Headline_names_describe_devices()
    {
        Assert.Equal(("CPU temperature", "°C"), HeadlineSeries.Describe("cputemp", null));
        Assert.Equal(("GPU power", "W"), HeadlineSeries.Describe("gpupower:/gpu-nvidia/0", null));
        Assert.Equal(("/x/0/fan/0", ""), HeadlineSeries.Describe("/x/0/fan/0", null));
    }
}
