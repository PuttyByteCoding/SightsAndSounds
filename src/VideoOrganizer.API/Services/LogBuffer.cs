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
}
