namespace VideoOrganizer.API.Services;

// In-memory snapshot of what ThumbnailWarmingService is doing right now.
// Mirrors Md5BackfillProgressTracker so the Background Tasks page can show
// the current video, the upcoming queue, and offer a "skip" action when a
// generation hangs. Restart-resets-to-empty is fine.
public sealed class ThumbnailWarmingProgressTracker
{
    private readonly object _lock = new();
    private Guid? _currentId;
    private string? _currentPath;
    private DateTime? _startedAt;
    private List<string> _queuePaths = new();
    private bool _skipRequested;

    public void Start(Guid id, string path)
    {
        lock (_lock)
        {
            _currentId = id;
            _currentPath = path;
            _startedAt = DateTime.UtcNow;
            _skipRequested = false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _currentId = null;
            _currentPath = null;
            _startedAt = null;
            _skipRequested = false;
        }
    }

    public void SetQueue(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _queuePaths = paths.ToList();
        }
    }

    public void Dequeue(string path)
    {
        lock (_lock)
        {
            var idx = _queuePaths.IndexOf(path);
            if (idx >= 0) _queuePaths.RemoveAt(idx);
        }
    }

    // User-requested skip: the worker polls this between ffmpeg phases (or
    // its cancellation token tied to the current job is cancelled) and
    // moves on. Resets on Start().
    public void RequestSkip()
    {
        lock (_lock) { _skipRequested = true; }
    }

    public bool IsSkipRequested
    {
        get { lock (_lock) { return _skipRequested; } }
    }

    public (Guid? Id, string? Path, DateTime? StartedAt, IReadOnlyList<string> QueuePaths) Snapshot()
    {
        lock (_lock)
        {
            return (_currentId, _currentPath, _startedAt, _queuePaths.ToArray());
        }
    }
}
