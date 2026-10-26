namespace PCVitals.Core;

/// <summary>
/// In-memory history: one sample per refresh (15 minutes deep) and one-minute averages (24 hours).
/// Series are keyed by sensor id; headline series ("cpu", "gpu:&lt;hardware id&gt;"…) are aliases that
/// point at the sensor they're currently read from, or are keys of their own when computed from
/// several sensors. Written on the poll thread, read from any thread.
/// </summary>
public sealed class HistoryStore
{
    readonly int _longLength, _minuteLength;
    readonly Dictionary<string, Ring> _history = new();
    readonly Dictionary<string, Ring> _minutes = new();
    readonly Dictionary<string, (double Sum, int Count)> _minuteAcc = new();
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
            }
            else
            {
                _minuteAcc.TryAdd(key, (0, 0));
            }
        }
    }

    /// <summary>
    /// Folds the samples of the minute that just ended into the long-term tier: one average per
    /// series per minute. Call before pushing the first sample of a refresh.
    /// </summary>
    public void RollMinute(DateTime now)
    {
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        if (_currentMinute == default)
        {
            _currentMinute = minute;
            return;
        }
        if (minute == _currentMinute) return;
        lock (_history)
        {
            foreach (var (key, acc) in _minuteAcc)
            {
                if (!_minutes.TryGetValue(key, out var ring)) _minutes[key] = ring = new Ring(_minuteLength);
                ring.Add(acc.Count > 0 ? (float)(acc.Sum / acc.Count) : float.NaN);
            }
            _minuteAcc.Clear();
            _minuteStamps[_minuteNext] = _currentMinute;
            _minuteNext = (_minuteNext + 1) % _minuteLength;
            _minuteStampCount = Math.Min(_minuteStampCount + 1, _minuteLength);
        }
        _currentMinute = minute;
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
