namespace PCVitals.Core;

/// <summary>
/// In-memory history: one sample per refresh (15 minutes deep) and one-minute averages (24 hours).
/// Series are keyed by sensor id; headline series ("cpu", "gpu:&lt;hardware id&gt;"…) are aliases that
/// point at the sensor they're currently read from, or are keys of their own when computed from
/// several sensors. Written on the poll thread, read from any thread.
/// </summary>
/// <remarks>
/// The minute tier is a real timeline: every series gets one value per minute (NaN when it had no
/// reading, the PC slept or the app wasn't running), so all series line up with the minute stamps.
/// </remarks>
public sealed class HistoryStore
{
    readonly int _longLength, _minuteLength;
    readonly Dictionary<string, Ring> _history = new();
    readonly Dictionary<string, Ring> _minutes = new();
    readonly Dictionary<string, (double Sum, int Count)> _minuteAcc = new();
    /// <summary>Per pushed name (alias or sensor id): what <see cref="MinuteClosed"/> reports.</summary>
    readonly Dictionary<string, (double Sum, int Count, float Min, float Max)> _summaryAcc = new();
    readonly Dictionary<string, string> _aliases = new();
    readonly DateTime[] _minuteStamps;
    int _minuteNext, _minuteStampCount;
    DateTime _currentMinute;

    public HistoryStore(int longLength, int minuteLength)
    {
        _longLength = longLength;
        _minuteLength = minuteLength;
        _minuteStamps = new DateTime[minuteLength];
    }

    /// <summary>
    /// Raised on the poll thread when a minute ends, with the average, minimum and maximum of every
    /// series pushed during it (under the name it was pushed with: alias or sensor id).
    /// </summary>
    public event Action<DateTime, IReadOnlyList<MinuteSummary>>? MinuteClosed;

    /// <summary>The series key an alias currently points at (the alias itself if it has no source).</summary>
    public string Resolve(string key)
    {
        lock (_history) return _aliases.GetValueOrDefault(key, key);
    }

    /// <summary>
    /// Records one sample. With a <paramref name="sourceId"/> the sample is stored under that sensor
    /// id and <paramref name="alias"/> points there; without one it is stored under the alias.
    /// </summary>
    public void Push(string alias, float? value, string? sourceId = null)
    {
        lock (_history)
        {
            string key = alias;
            if (sourceId != null)
            {
                if (!_aliases.ContainsKey(alias) && !_minutes.ContainsKey(sourceId) && _minutes.Remove(alias, out var seeded))
                    _minutes[sourceId] = seeded; // history loaded from disk under the alias belongs to its sensor
                _aliases[alias] = sourceId;
                key = sourceId;
            }
            else
            {
                _aliases.Remove(alias);
            }
            if (!_history.TryGetValue(key, out var ring)) _history[key] = ring = new Ring(_longLength);
            ring.Add(value ?? float.NaN);
            if (value is { } v && float.IsFinite(v))
            {
                var acc = _minuteAcc.GetValueOrDefault(key);
                _minuteAcc[key] = (acc.Sum + v, acc.Count + 1);
                _summaryAcc[alias] = _summaryAcc.TryGetValue(alias, out var s)
                    ? (s.Sum + v, s.Count + 1, Math.Min(s.Min, v), Math.Max(s.Max, v))
                    : (v, 1, v, v);
            }
            else
            {
                _minuteAcc.TryAdd(key, (0, 0));
            }
        }
    }

    /// <summary>
    /// Folds the samples of the minute that just ended into the long-term tier: one average per
    /// series per minute, with NaN for minutes nothing ran. Call before pushing the first sample of
    /// a refresh.
    /// </summary>
    public void RollMinute(DateTime now)
    {
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        if (_currentMinute == default)
        {
            _currentMinute = minute;
            return;
        }
        if (minute <= _currentMinute) return; // same minute, or the clock went back
        var closed = _currentMinute;
        List<MinuteSummary> summaries;
        lock (_history)
        {
            CloseMinute(closed, _minuteAcc);
            _minuteAcc.Clear();
            // Asleep, or the app was busy: those minutes have no readings.
            int missed = (int)Math.Min(_minuteLength, (minute - closed).TotalMinutes - 1);
            for (int i = 1; i <= missed; i++) CloseMinute(minute.AddMinutes(-(missed - i + 1)), null);
            summaries = new List<MinuteSummary>(_summaryAcc.Count);
            foreach (var (key, s) in _summaryAcc) summaries.Add(new MinuteSummary(key, (float)(s.Sum / s.Count), s.Min, s.Max));
            _summaryAcc.Clear();
        }
        _currentMinute = minute;
        if (summaries.Count > 0) MinuteClosed?.Invoke(closed, summaries);
    }

    void CloseMinute(DateTime minute, Dictionary<string, (double Sum, int Count)>? acc)
    {
        if (acc != null)
            foreach (var key in acc.Keys)
                if (!_minutes.ContainsKey(key)) _minutes[key] = new Ring(_minuteLength);
        foreach (var (key, ring) in _minutes)
            ring.Add(acc != null && acc.TryGetValue(key, out var a) && a.Count > 0 ? (float)(a.Sum / a.Count) : float.NaN);
        _minuteStamps[_minuteNext] = minute;
        _minuteNext = (_minuteNext + 1) % _minuteLength;
        _minuteStampCount = Math.Min(_minuteStampCount + 1, _minuteLength);
    }

    /// <summary>
    /// Fills the minute tier from saved history (call once, before the first poll), so the 1 h and
    /// 24 h graphs survive a restart. Series are stored under the names they were saved with;
    /// aliases move to their sensor when it is first pushed.
    /// </summary>
    public void Seed(HistoryRange range)
    {
        if (range.BucketMinutes != 1 || range.Count == 0) return;
        lock (_history)
        {
            int last = -1;
            foreach (var (_, s) in range.Series)
                for (int i = range.Count - 1; i > last; i--)
                    if (!float.IsNaN(s.Avg[i]))
                    {
                        last = i;
                        break;
                    }
            if (last < 0) return;
            int first = Math.Max(0, last + 1 - _minuteLength);
            foreach (var (key, s) in range.Series)
            {
                var ring = _minutes[key] = new Ring(_minuteLength);
                for (int i = first; i <= last; i++) ring.Add(s.Avg[i]);
            }
            for (int i = first; i <= last; i++)
            {
                _minuteStamps[_minuteNext] = range.TimeOf(i);
                _minuteNext = (_minuteNext + 1) % _minuteLength;
                _minuteStampCount = Math.Min(_minuteStampCount + 1, _minuteLength);
            }
            // The next refresh closes this (empty) minute and marks the time the app was off as gaps.
            _currentMinute = range.TimeOf(last).AddMinutes(1);
        }
    }

    /// <summary>The newest <paramref name="count"/> samples of every series, under both ids and aliases.</summary>
    public Dictionary<string, float[]> Recent(int count)
    {
        lock (_history)
        {
            var result = new Dictionary<string, float[]>(_history.Count + _aliases.Count);
            foreach (var (key, ring) in _history) result[key] = ring.Last(count);
            foreach (var (alias, id) in _aliases)
                if (result.TryGetValue(id, out var values)) result[alias] = values;
            return result;
        }
    }

    /// <summary>The newest <paramref name="count"/> samples of a series (oldest first).</summary>
    public float[] Get(string key, int count)
    {
        lock (_history)
            return _history.TryGetValue(_aliases.GetValueOrDefault(key, key), out var ring) ? ring.Last(count) : Array.Empty<float>();
    }

    /// <summary>The newest <paramref name="count"/> one-minute averages of a series (oldest first).</summary>
    public float[] GetMinutes(string key, int count)
    {
        lock (_history)
            return _minutes.TryGetValue(_aliases.GetValueOrDefault(key, key), out var ring) ? ring.Last(count) : Array.Empty<float>();
    }

    /// <summary>
    /// The minutes recorded so far (oldest first) and, for each requested series that has any, its
    /// averages aligned to them: series that appeared later are shorter and end at the newest minute.
    /// </summary>
    public (DateTime[] Minutes, List<(string Key, float[] Values)> Series) MinuteTable(IEnumerable<string> keys)
    {
        lock (_history)
        {
            var stamps = new DateTime[_minuteStampCount];
            int start = (_minuteNext - _minuteStampCount + _minuteLength) % _minuteLength;
            for (int i = 0; i < stamps.Length; i++) stamps[i] = _minuteStamps[(start + i) % _minuteLength];
            var series = new List<(string, float[])>();
            foreach (var key in keys)
                if (_minutes.TryGetValue(_aliases.GetValueOrDefault(key, key), out var ring)) series.Add((key, ring.Last(stamps.Length)));
            return (stamps, series);
        }
    }

    /// <summary>Fixed-size ring buffer: no per-sample allocation however long the history.</summary>
    internal sealed class Ring
    {
        readonly float[] _data;
        int _next, _count;

        public Ring(int capacity) => _data = new float[capacity];

        public void Add(float v)
        {
            _data[_next] = v;
            _next = (_next + 1) % _data.Length;
            if (_count < _data.Length) _count++;
        }

        public float[] Last(int n)
        {
            n = Math.Clamp(n, 0, _count);
            var result = new float[n];
            int start = (_next - n + _data.Length) % _data.Length;
            for (int i = 0; i < n; i++) result[i] = _data[(start + i) % _data.Length];
            return result;
        }
    }
}
