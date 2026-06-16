namespace VideoOrganizer.API.Services;

// Live state of an "optimize for streaming" run (issue #166), polled by the
// Optimize page. One run at a time — mirrors the other job progress singletons.
// Skipped counts files that were already faststart (or not MP4) and so needed
// no work.
public sealed class StreamingOptimizeProgress
{
    private readonly object _lock = new();
    private int _total;
    private int _done;
    private int _optimized;
    private int _skipped;
    private string _current = "";
    private bool _active;
    private bool _stopRequested;
    private string _phase = "idle";   // idle | optimizing | stopping | done | error
    private readonly List<string> _errors = new();

    public void Begin(int total)
    {
        lock (_lock)
        {
            _total = total;
            _done = 0;
            _optimized = 0;
            _skipped = 0;
            _current = "";
            _active = true;
            _stopRequested = false;
            _phase = "optimizing";
            _errors.Clear();
        }
    }

    public void SetCurrent(string name) { lock (_lock) { _current = name; } }
    public void CompletedOne() { lock (_lock) { _done++; } }
    public void RecordOptimized() { lock (_lock) { _optimized++; } }
    public void RecordSkipped() { lock (_lock) { _skipped++; } }
    public void AddError(string message) { lock (_lock) { _errors.Add(message); } }

    public void RequestStop()
    {
        lock (_lock) { if (_active) { _stopRequested = true; _phase = "stopping"; } }
    }

    public bool StopRequested { get { lock (_lock) { return _stopRequested; } } }
    public bool IsActive { get { lock (_lock) { return _active; } } }

    public void End(string phase)
    {
        lock (_lock)
        {
            _active = false;
            _stopRequested = false;
            _phase = _errors.Count > 0 && phase == "done" ? "error" : phase;
        }
    }

    public (bool Active, int Total, int Done, int Optimized, int Skipped, string Current, string Phase, IReadOnlyList<string> Errors) Snapshot()
    {
        lock (_lock) { return (_active, _total, _done, _optimized, _skipped, _current, _phase, _errors.ToArray()); }
    }
}
