using System.Buffers.Binary;
using System.Text.Json;

namespace PCVitals.Core;

/// <summary>One series' one-minute (or coarser) summary: average, minimum and maximum.</summary>
/// <param name="Minute">Start of the period, in minutes since 1970-01-01 UTC.</param>
public readonly record struct HistoryRecord(int Minute, ushort Series, float Avg, float Min, float Max);

/// <summary>
/// One day of history on disk (<c>YYYY-MM-DD.pvh</c>, local date): a 16-byte header, then
/// append-only 18-byte records. Writes are whole records; a record cut short by a crash is
/// ignored when reading and trimmed before the next append.
/// </summary>
public static class HistoryFile
{
    public const int HeaderSize = 16, RecordSize = 18;
    public const ushort FormatVersion = 1;
    static ReadOnlySpan<byte> Magic => "PVH1"u8;

    public static int ToMinute(DateTime time) => (int)((time.ToUniversalTime() - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMinute);

    public static DateTime FromMinute(int minute) => DateTime.UnixEpoch.AddMinutes(minute).ToLocalTime();

    public static string PathFor(string dir, DateOnly day) => Path.Combine(dir, $"{day:yyyy-MM-dd}.pvh");

    public static bool TryParseDay(string path, out DateOnly day) =>
        DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", out day) && Path.GetExtension(path) == ".pvh";

    /// <summary>Appends records, creating the file (with the given resolution in minutes) if needed.</summary>
    public static void Append(string path, IReadOnlyList<HistoryRecord> records, int resolution = 1)
    {
        if (records.Count == 0) return;
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length < HeaderSize || !HeaderOk(fs, out _))
        {
            // Empty, or not ours / damaged beyond its header: start the day again.
            fs.SetLength(0);
            WriteHeader(fs, resolution);
        }
        long aligned = HeaderSize + (fs.Length - HeaderSize) / RecordSize * RecordSize;
        if (aligned != fs.Length) fs.SetLength(aligned); // drop a record a crash cut short
        fs.Position = aligned;
        fs.Write(Encode(records));
        fs.Flush();
    }

    /// <summary>Replaces a file's contents atomically (write to a temporary file, then rename).</summary>
    public static void Write(string path, int resolution, IReadOnlyList<HistoryRecord> records)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteHeader(fs, resolution);
            fs.Write(Encode(records));
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The file's resolution in minutes and its whole records; (1, empty) for a missing or foreign file.</summary>
    public static (int Resolution, List<HistoryRecord> Records) Read(string path)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (1, new List<HistoryRecord>());
        }
        return Parse(bytes);
    }

    internal static (int Resolution, List<HistoryRecord> Records) Parse(ReadOnlySpan<byte> bytes)
    {
        var records = new List<HistoryRecord>();
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != FormatVersion)
            return (1, records);
        int resolution = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
        int count = (bytes.Length - HeaderSize) / RecordSize;
        records.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            var r = bytes.Slice(HeaderSize + i * RecordSize, RecordSize);
            records.Add(new HistoryRecord(
                BinaryPrimitives.ReadInt32LittleEndian(r),
                BinaryPrimitives.ReadUInt16LittleEndian(r[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[6..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[10..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[14..])));
        }
        return (resolution, records);
    }

    /// <summary>
    /// Folds records into <paramref name="resolution"/>-minute periods per series: mean of the
    /// averages, lowest minimum, highest maximum.
    /// </summary>
    public static List<HistoryRecord> Downsample(IEnumerable<HistoryRecord> records, int resolution)
    {
        var acc = new Dictionary<(int Minute, ushort Series), (double Sum, int Count, float Min, float Max)>();
        foreach (var r in records)
        {
            var key = (r.Minute - ((r.Minute % resolution) + resolution) % resolution, r.Series);
            acc[key] = acc.TryGetValue(key, out var a)
                ? (a.Sum + r.Avg, a.Count + 1, Math.Min(a.Min, r.Min), Math.Max(a.Max, r.Max))
                : (r.Avg, 1, r.Min, r.Max);
        }
        return acc.OrderBy(kv => kv.Key.Minute).ThenBy(kv => kv.Key.Series)
                  .Select(kv => new HistoryRecord(kv.Key.Minute, kv.Key.Series, (float)(kv.Value.Sum / kv.Value.Count), kv.Value.Min, kv.Value.Max))
                  .ToList();
    }

    static bool HeaderOk(FileStream fs, out int resolution)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        fs.Position = 0;
        fs.ReadExactly(header);
        resolution = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        return header[..4].SequenceEqual(Magic) && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == FormatVersion;
    }

    static void WriteHeader(Stream s, int resolution)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)resolution);
        s.Write(header);
    }

    static byte[] Encode(IReadOnlyList<HistoryRecord> records)
    {
        var buf = new byte[records.Count * RecordSize];
        for (int i = 0; i < records.Count; i++)
        {
            var r = buf.AsSpan(i * RecordSize, RecordSize);
            BinaryPrimitives.WriteInt32LittleEndian(r, records[i].Minute);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], records[i].Series);
            BinaryPrimitives.WriteSingleLittleEndian(r[6..], records[i].Avg);
            BinaryPrimitives.WriteSingleLittleEndian(r[10..], records[i].Min);
            BinaryPrimitives.WriteSingleLittleEndian(r[14..], records[i].Max);
        }
        return buf;
    }
}

/// <summary>
/// <c>series.json</c>: maps the record's series index to what it is (sensor id or headline alias),
/// with a display name and unit for exports. Entries are only ever added, so old files stay valid.
/// </summary>
public sealed class SeriesRegistry
{
    public sealed record Entry(string Key, string Name, string Unit);

    readonly string _path;
    readonly List<Entry> _entries = new();
    readonly Dictionary<string, ushort> _index = new(StringComparer.Ordinal);
    bool _dirty;

    SeriesRegistry(string path) => _path = path;

    public static SeriesRegistry Load(string path)
    {
        var registry = new SeriesRegistry(path);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) is { } entries)
                foreach (var e in entries) registry.Add(e);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Without the map the day files can't be read; keep the damaged copy for inspection.
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch { }
        }
        return registry;
    }

    void Add(Entry e)
    {
        _index[e.Key] = (ushort)_entries.Count;
        _entries.Add(e);
    }

    public int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    public Entry? this[ushort index]
    {
        get { lock (_entries) return index < _entries.Count ? _entries[index] : null; }
    }

    public ushort? Find(string key)
    {
        lock (_entries) return _index.TryGetValue(key, out var i) ? i : null;
    }

    public IReadOnlyList<Entry> All
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    /// <summary>The series' index, adding it (described by <paramref name="describe"/>) the first time; refreshes a changed name.</summary>
    public ushort IndexOf(string key, Func<string, (string Name, string Unit)> describe)
    {
        lock (_entries)
        {
            if (_index.TryGetValue(key, out var i)) return i;
            if (_entries.Count >= ushort.MaxValue) throw new InvalidOperationException("Too many history series.");
            var (name, unit) = describe(key);
            Add(new Entry(key, name, unit));
            _dirty = true;
            return (ushort)(_entries.Count - 1);
        }
    }

    /// <summary>Forgets every series (after the history files were deleted).</summary>
    public void Clear()
    {
        lock (_entries)
        {
            _entries.Clear();
            _index.Clear();
            _dirty = false;
        }
    }

    public void Save()
    {
        List<Entry> copy;
        lock (_entries)
        {
            if (!_dirty) return;
            copy = _entries.ToList();
            _dirty = false;
        }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Series bucketed onto a regular time grid; NaN where nothing was recorded.</summary>
public sealed class HistoryRange
{
    public required int StartMinute { get; init; }
    public required int BucketMinutes { get; init; }
    public required int Count { get; init; }
    public Dictionary<string, (float[] Avg, float[] Min, float[] Max)> Series { get; } = new();

    public DateTime TimeOf(int bucket) => HistoryFile.FromMinute(StartMinute + bucket * BucketMinutes);
}

/// <summary>The history folder: day files, the series map, and reading, pruning and compacting them.</summary>
public sealed class HistoryArchive
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCVitals", "history");

    /// <summary>Days older than this are kept at 5-minute resolution.</summary>
    public const int FullResolutionDays = 7;
    public const int CompactedResolution = 5;

    public string Dir { get; }
    public SeriesRegistry Series { get; }

    public HistoryArchive(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        Series = SeriesRegistry.Load(Path.Combine(dir, "series.json"));
    }

    public IEnumerable<(DateOnly Day, string Path)> Days() =>
        Directory.EnumerateFiles(Dir, "*.pvh")
                 .Select(p => (Ok: HistoryFile.TryParseDay(p, out var d), Day: d, Path: p))
                 .Where(x => x.Ok).OrderBy(x => x.Day).Select(x => (x.Day, x.Path));

    public long SizeBytes()
    {
        try { return new DirectoryInfo(Dir).EnumerateFiles().Sum(f => f.Length); }
        catch (IOException) { return 0; }
    }

    /// <summary>Appends one minute of summaries (from <see cref="HistoryStore.MinuteClosed"/>).</summary>
    public void Append(DateTime minute, IReadOnlyList<MinuteSummary> values, Func<string, (string Name, string Unit)> describe)
    {
        int m = HistoryFile.ToMinute(minute);
        var records = new List<HistoryRecord>(values.Count);
        foreach (var v in values) records.Add(new HistoryRecord(m, Series.IndexOf(v.Key, describe), v.Avg, v.Min, v.Max));
        Series.Save(); // before the records that refer to new entries
        HistoryFile.Append(HistoryFile.PathFor(Dir, DateOnly.FromDateTime(minute.ToLocalTime())), records);
    }

    /// <summary>Deletes days past retention and folds days older than a week into 5-minute periods.</summary>
    public void Maintain(DateOnly today, int retentionDays)
    {
        foreach (var (day, path) in Days().ToList())
        {
            int age = today.DayNumber - day.DayNumber;
            if (age >= retentionDays)
            {
                File.Delete(path);
                continue;
            }
            if (age <= FullResolutionDays) continue;
            var (resolution, records) = HistoryFile.Read(path);
            if (resolution >= CompactedResolution) continue;
            HistoryFile.Write(path, CompactedResolution, HistoryFile.Downsample(records, CompactedResolution));
        }
    }

    public void DeleteAll()
    {
        foreach (var f in Directory.EnumerateFiles(Dir)) File.Delete(f);
        Series.Clear();
    }

    /// <summary>
    /// Every series (or just <paramref name="keys"/>) between <paramref name="from"/> and
    /// <paramref name="to"/>, in <paramref name="bucketMinutes"/>-minute buckets.
    /// </summary>
    public HistoryRange Load(DateTime from, DateTime to, int bucketMinutes, IReadOnlySet<string>? keys = null)
    {
        int fromMinute = HistoryFile.ToMinute(from), toMinute = HistoryFile.ToMinute(to);
        int start = fromMinute - Mod(fromMinute, bucketMinutes);
        int count = Math.Max(0, (toMinute - start + bucketMinutes - 1) / bucketMinutes);
        var range = new HistoryRange { StartMinute = start, BucketMinutes = bucketMinutes, Count = count };
        var acc = new Dictionary<ushort, (double[] Sum, float[] Weight, float[] Min, float[] Max)>();

        var firstDay = DateOnly.FromDateTime(from.ToLocalTime()).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(to.ToLocalTime()).AddDays(1);
        foreach (var (day, path) in Days())
        {
            if (day < firstDay || day > lastDay) continue;
            var (resolution, records) = HistoryFile.Read(path);
            foreach (var r in records)
            {
                if (r.Minute < start || r.Minute >= toMinute) continue;
                if (!acc.TryGetValue(r.Series, out var a))
                {
                    if (keys != null && (Series[r.Series] is not { } e || !keys.Contains(e.Key))) continue;
                    a = (new double[count], new float[count], Filled(count, float.PositiveInfinity), Filled(count, float.NegativeInfinity));
                    acc[r.Series] = a;
                }
                int b = (r.Minute - start) / bucketMinutes;
                if (b >= count || !float.IsFinite(r.Avg)) continue;
                a.Sum[b] += r.Avg * (double)resolution; // a 5-minute record stands for 5 one-minute ones
                a.Weight[b] += resolution;
                a.Min[b] = Math.Min(a.Min[b], r.Min);
                a.Max[b] = Math.Max(a.Max[b], r.Max);
            }
        }
        foreach (var (index, a) in acc)
        {
            if (Series[index] is not { } entry) continue;
            var avg = new float[count];
            for (int i = 0; i < count; i++)
            {
                bool any = a.Weight[i] > 0;
                avg[i] = any ? (float)(a.Sum[i] / a.Weight[i]) : float.NaN;
                if (!any) a.Min[i] = a.Max[i] = float.NaN;
            }
            range.Series[entry.Key] = (avg, a.Min, a.Max);
        }
        return range;
    }

    static int Mod(int a, int m) => ((a % m) + m) % m;

    static float[] Filled(int n, float v)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }
}

/// <summary>One series' summary of one minute.</summary>
public readonly record struct MinuteSummary(string Key, float Avg, float Min, float Max);
