namespace VideoOrganizer.API.Services;

// In-memory snapshot of what Md5BackfillService is hashing right now. Shared
// via DI with the status endpoint so the Background Tasks page can show a
// live progress bar. Everything here is best-effort / non-authoritative —
// restart-resets-to-empty is fine.
public sealed class Md5BackfillProgressTracker
{
    private readonly object _lock = new();
    private string? _fileName;
    private string? _filePath;
    private long _bytesProcessed;
    private long _totalBytes;
    // Snapshot of the current batch the service is working through, so the
    // Background Tasks page can show "what's next". The head of the list is
    // the currently-hashing file; everything after it is queued.
    private List<string> _queuePaths = new();
    private bool _skipRequested;

    public void Start(string fileName, string filePath, long totalBytes)
    {
        lock (_lock)
        {
            _fileName = fileName;
            _filePath = filePath;
            _bytesProcessed = 0;
            _totalBytes = totalBytes;
            _skipRequested = false;
        }
    }

    // User-requested skip from the Background Tasks page. The hash loop polls
    // this between buffer reads and bails. Reset on the next Start().
    public void RequestSkip()
    {
        lock (_lock) { _skipRequested = true; }
    }

    public bool IsSkipRequested
    {
        get { lock (_lock) { return _skipRequested; } }
    }

    public void Update(long bytesProcessed)
    {
        lock (_lock)
        {
            _bytesProcessed = bytesProcessed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _fileName = null;
            _filePath = null;
            _bytesProcessed = 0;
            _totalBytes = 0;
            _queuePaths = new();
        }
    }

    // Replace the queue snapshot at the start of each batch. Caller passes
    // the full list of paths in the order they'll be processed; the tracker
    // stores a defensive copy.
    public void SetQueue(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _queuePaths = paths.ToList();
        }
    }

    // Drop the given path from the queue after it's been consumed. Matching
    // is ordinal — the service stores the exact string it fed in.
    public void Dequeue(string path)
    {
        lock (_lock)
        {
            var idx = _queuePaths.IndexOf(path);
            if (idx >= 0) _queuePaths.RemoveAt(idx);
        }
    }

    public (string? FileName, string? FilePath, long BytesProcessed, long TotalBytes, IReadOnlyList<string> QueuePaths) Snapshot()
    {
        lock (_lock)
        {
            return (_fileName, _filePath, _bytesProcessed, _totalBytes, _queuePaths.ToArray());
        }
    }
}
