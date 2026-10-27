using System.Collections.Concurrent;

namespace PCVitals.Core;

/// <summary>
/// Saves each minute's summaries to the <see cref="HistoryArchive"/> on a background thread, and
/// prunes and compacts old days once a day. The poll thread only enqueues (one small batch a minute).
/// </summary>
public sealed class HistoryRecorder : IDisposable
{
    readonly HistoryArchive _archive;
    readonly Func<string, (string Name, string Unit)> _describe;
    readonly BlockingCollection<Action> _queue = new(boundedCapacity: 1440);
    readonly Thread _thread;
    DateOnly _maintained;

    /// <summary>Days of history to keep; takes effect at the next daily clean-up (or <see cref="MaintainNow"/>).</summary>
    public int RetentionDays { get; set; } = 30;

    /// <param name="describe">Display name and unit for a series key, the first time it is saved (called on the writer thread).</param>
    public HistoryRecorder(HistoryArchive archive, Func<string, (string Name, string Unit)> describe)
    {
        _archive = archive;
        _describe = describe;
        _thread = new Thread(Run) { IsBackground = true, Name = "History writer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Queues one minute for saving; matches <see cref="HistoryStore.MinuteClosed"/>.</summary>
    public void Enqueue(DateTime minute, IReadOnlyList<MinuteSummary> values)
    {
        // If the disk stalls for a day, drop new minutes rather than grow without bound.
        if (values.Count > 0) _queue.TryAdd(() => _archive.Append(minute, values, _describe));
    }

    /// <summary>Runs the clean-up again soon (e.g. after the retention setting changed).</summary>
    public void MaintainNow()
    {
        _maintained = default;
        _queue.TryAdd(() => { });
    }

    /// <summary>Deletes every saved day (on the writer thread, so it can't race an append).</summary>
    public Task DeleteAllAsync()
    {
        var done = new TaskCompletionSource();
        _queue.Add(() =>
        {
            try
            {
                _archive.DeleteAll();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    void Run()
    {
        Maintain();
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                HardwareService.LogOnce("history-write:" + ex.GetType().Name, "Couldn't save history (continuing): " + ex);
            }
            Maintain();
        }
    }

    void Maintain()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_maintained == today) return;
        _maintained = today;
        try
        {
            _archive.Maintain(today, RetentionDays);
        }
        catch (Exception ex)
        {
            HardwareService.LogOnce("history-maintain:" + ex.GetType().Name, "History clean-up failed: " + ex);
        }
    }

    /// <summary>Stops after writing what is queued (up to a few seconds).</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(3000);
    }
}
