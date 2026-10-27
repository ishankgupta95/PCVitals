using System.Globalization;

namespace PCVitals.Core;

/// <summary>Writes saved history as CSV: a time column, then one column per series (optionally with min and max).</summary>
public static class HistoryCsv
{
    /// <returns>The number of rows written (periods with no data in any column are left out).</returns>
    public static int Write(TextWriter w, HistoryRange range, IReadOnlyList<(string Key, string Title)> columns, bool minMax)
    {
        var inv = CultureInfo.InvariantCulture;
        var present = columns.Where(c => range.Series.ContainsKey(c.Key)).ToList();
        var header = new List<string> { "Time" };
        foreach (var (_, title) in present)
        {
            header.Add(Quote(title));
            if (minMax)
            {
                header.Add(Quote(title + " min"));
                header.Add(Quote(title + " max"));
            }
        }
        w.WriteLine(string.Join(",", header));

        static string Cell(float v, CultureInfo inv) => float.IsFinite(v) ? v.ToString("0.##", inv) : "";
        int rows = 0;
        for (int i = 0; i < range.Count; i++)
        {
            if (present.All(c => float.IsNaN(range.Series[c.Key].Avg[i]))) continue;
            var cells = new List<string> { range.TimeOf(i).ToString("yyyy-MM-dd HH:mm", inv) };
            foreach (var (key, _) in present)
            {
                var (avg, min, max) = range.Series[key];
                cells.Add(Cell(avg[i], inv));
                if (minMax)
                {
                    cells.Add(Cell(min[i], inv));
                    cells.Add(Cell(max[i], inv));
                }
            }
            w.WriteLine(string.Join(",", cells));
            rows++;
        }
        return rows;
    }

    public enum Selection { Headline, Sensors, All }

    /// <summary>
    /// Columns for an export from the series that have been saved: the headline readings (current
    /// devices first, in the usual order), the given sensors (favourites and pinned), or both.
    /// </summary>
    public static List<(string Key, string Title)> Columns(IReadOnlyList<SeriesRegistry.Entry> recorded, Selection which,
                                                           Summary? summary, IReadOnlyCollection<string> sensorIds)
    {
        var byKey = recorded.ToDictionary(e => e.Key);
        var keys = new List<string>();
        if (which != Selection.Sensors)
        {
            keys.AddRange(HeadlineSeries.Keys(summary).Where(byKey.ContainsKey));
            keys.AddRange(recorded.Where(e => HeadlineSeries.IsHeadline(e.Key) && !keys.Contains(e.Key)).Select(e => e.Key));
        }
        if (which != Selection.Headline)
            keys.AddRange(recorded.Where(e => !HeadlineSeries.IsHeadline(e.Key) && (which == Selection.All || sensorIds.Contains(e.Key))).Select(e => e.Key));
        return keys.Select(k => (k, byKey[k].Unit.Length > 0 ? $"{byKey[k].Name} ({byKey[k].Unit})" : byKey[k].Name)).ToList();
    }

    static string Quote(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
