namespace VideoOrganizer.API.Services;

// One log entry snapshot for the /api/logs feed. Shape mirrors what the
// Logs page renders — timestamp, level, category, message, optional
// exception string.
public record LogEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception);

// Thread-safe, time-windowed buffer of log events. Writers (any ILogger
// callsite) push via Add(); readers (GET /api/logs) snapshot the whole
// window. Entries older than `Retention` are evicted on every write.
public sealed class LogBuffer
{
    private readonly LinkedList<LogEvent> _events = new();
    private readonly object _lock = new();
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(48);

    public void Add(LogEvent evt)
    {
        lock (_lock)
        {
            _events.AddLast(evt);
            var cutoff = DateTimeOffset.UtcNow - Retention;
            while (_events.First is { Value: var head } && head.Timestamp < cutoff)
                _events.RemoveFirst();
        }
    }

    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }

    /// <summary>
    /// Returns up to <paramref name="take"/> of the most recent events
    /// whose timestamp is at or after <paramref name="since"/>, in
    /// ascending order. Walks the tail backwards so we can stop early
    /// — the cost is O(min(take, eventsInWindow)) rather than O(N)
    /// even on a buffer that's been collecting for 48 hours. Used by
    /// GET /api/logs so the Logs page doesn't blow its first paint
    /// budget on thousands of stale entries.
    /// </summary>
    public IReadOnlyList<LogEvent> SnapshotRecent(DateTimeOffset since, int take)
    {
        if (take <= 0) return Array.Empty<LogEvent>();
        lock (_lock)
        {
            var result = new List<LogEvent>(Math.Min(take, _events.Count));
            for (var node = _events.Last; node != null; node = node.Previous)
            {
                if (node.Value.Timestamp < since) break;
                result.Add(node.Value);
                if (result.Count >= take) break;
            }
            result.Reverse();
            return result;
        }
    }
}
