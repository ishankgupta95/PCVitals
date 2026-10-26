namespace PCVitals.Core;

/// <summary>
/// Receives every snapshot on the poll thread, in the order consumers were added
/// (<see cref="HardwareService.AddConsumer"/>). Must return quickly: batch anything heavy
/// (disk writes, UI work) and hand it off to another thread.
/// </summary>
public interface ISnapshotConsumer
{
    void OnSnapshot(Snapshot snapshot);
}

/// <summary>A consumer from a delegate, e.g. to marshal snapshots onto the UI thread.</summary>
public sealed class SnapshotConsumer(Action<Snapshot> onSnapshot) : ISnapshotConsumer
{
    public void OnSnapshot(Snapshot snapshot) => onSnapshot(snapshot);
}
