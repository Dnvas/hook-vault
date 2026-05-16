namespace HookVault.Services;

// Singleton mirror of the EventRetentionWorker's most recent sweep.
// Writes happen on the worker's thread; reads happen on request threads.
// Protected by a simple lock — sweeps are infrequent (>= 5min) so contention
// is negligible.
public sealed class RetentionStats
{
    private readonly object _lock = new();
    private DateTimeOffset? _lastSweepAt;
    private int _lastSweepDeleted;

    public int? MaxEvents { get; init; }
    public TimeSpan? Retention { get; init; }

    public DateTimeOffset? LastSweepAt
    {
        get { lock (_lock) return _lastSweepAt; }
    }

    public int LastSweepDeleted
    {
        get { lock (_lock) return _lastSweepDeleted; }
    }

    public void RecordSweep(int deleted)
    {
        lock (_lock)
        {
            _lastSweepAt = DateTimeOffset.UtcNow;
            _lastSweepDeleted = deleted;
        }
    }
}
