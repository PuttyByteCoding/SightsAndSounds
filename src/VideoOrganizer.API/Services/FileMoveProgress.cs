namespace VideoOrganizer.API.Services;

// Live byte-progress for an in-flight file move or undo (issue #4), polled
// by the move dialog via GET /api/videos/{id}/move-progress. Single
// current-move model keyed by video id — moves are user-initiated one at a
// time. A cross-volume copy updates BytesCopied per chunk; a same-volume
// move is an instant rename that never reports intermediate bytes.
// In-memory only; restart-resets-to-idle is fine.
public sealed class FileMoveProgress
{
    private readonly object _lock = new();
    private Guid _videoId;
    private long _bytesCopied;
    private long _totalBytes;
    private bool _active;
    private string _phase = "idle";

    public void Begin(Guid videoId, long totalBytes)
    {
        lock (_lock)
        {
            _videoId = videoId;
            _bytesCopied = 0;
            _totalBytes = totalBytes;
            _active = true;
            _phase = "copying";
        }
    }

    public void Report(long bytesCopied)
    {
        lock (_lock) { _bytesCopied = bytesCopied; }
    }

    public void SetPhase(string phase)
    {
        lock (_lock) { _phase = phase; }
    }

    public void End()
    {
        lock (_lock)
        {
            _active = false;
            _phase = "done";
        }
    }

    public (bool Active, long BytesCopied, long TotalBytes, string Phase, Guid VideoId) Snapshot()
    {
        lock (_lock)
        {
            return (_active, _bytesCopied, _totalBytes, _phase, _videoId);
        }
    }
}
