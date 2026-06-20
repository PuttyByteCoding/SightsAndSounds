namespace VideoOrganizer.API.Services;

// Live state of the on-screen-text OCR scan (issue #5), polled by the player's
// "Scan for text" panel via GET /api/videos/{id}/ocr-scan. One scan runs at a
// time (scans are user-initiated and CPU-heavy), so a single current-scan model
// keyed by video id is enough — mirrors FileMoveProgress. In-memory only;
// restart-resets-to-idle is fine, and the durable resume position lives on
// Video.OcrScannedThroughSeconds.
public sealed class OcrScanProgress
{
    private readonly object _lock = new();
    private Guid _videoId;
    private double _scannedThroughSeconds;
    private double _durationSeconds;
    private int _hits;
    private bool _active;
    private bool _stopRequested;
    private string _phase = "idle";   // idle | scanning | stopping | done | error
    private string? _error;

    public void Begin(Guid videoId, double durationSeconds, double startFromSeconds)
    {
        lock (_lock)
        {
            _videoId = videoId;
            _durationSeconds = durationSeconds;
            _scannedThroughSeconds = startFromSeconds;
            _hits = 0;
            _active = true;
            _stopRequested = false;
            _phase = "scanning";
            _error = null;
        }
    }

    // Called as the scan advances: how far it has reached and the running hit
    // count (rows written this scan).
    public void Report(double scannedThroughSeconds, int hits)
    {
        lock (_lock)
        {
            _scannedThroughSeconds = scannedThroughSeconds;
            _hits = hits;
        }
    }

    // Cooperative stop: the scan loop checks StopRequested between frames.
    public void RequestStop()
    {
        lock (_lock)
        {
            if (_active) { _stopRequested = true; _phase = "stopping"; }
        }
    }

    public bool StopRequested
    {
        get { lock (_lock) { return _stopRequested; } }
    }

    public bool IsActive
    {
        get { lock (_lock) { return _active; } }
    }

    public void End(string phase, string? error = null)
    {
        lock (_lock)
        {
            _active = false;
            _stopRequested = false;
            _phase = phase;
            _error = error;
        }
    }

    public (bool Active, Guid VideoId, double ScannedThroughSeconds, double DurationSeconds,
        int Hits, string Phase, string? Error) Snapshot()
    {
        lock (_lock)
        {
            return (_active, _videoId, _scannedThroughSeconds, _durationSeconds, _hits, _phase, _error);
        }
    }
}
