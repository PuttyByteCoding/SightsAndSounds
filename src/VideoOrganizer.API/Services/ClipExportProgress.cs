namespace VideoOrganizer.API.Services;

// Live state of a clip-export run (issue #69), polled by the clips-export page.
// One run at a time (it's CPU/disk work and user-initiated) — a single
// current-run model, mirroring FileMoveProgress / OcrScanProgress. In-memory
// only; the durable result is the new files + their Video rows + each source
// clip's ClipExported flag.
public sealed class ClipExportProgress
{
    private readonly object _lock = new();
    private int _total;
    private int _done;
    private string _current = "";
    private bool _active;
    private bool _stopRequested;
    private string _phase = "idle";   // idle | exporting | stopping | done | error
    private readonly List<string> _errors = new();

    public void Begin(int total)
    {
        lock (_lock)
        {
            _total = total;
            _done = 0;
            _current = "";
            _active = true;
            _stopRequested = false;
            _phase = "exporting";
            _errors.Clear();
        }
    }

    public void SetCurrent(string name)
    {
        lock (_lock) { _current = name; }
    }

    public void CompletedOne()
    {
        lock (_lock) { _done++; }
    }

    public void AddError(string message)
    {
        lock (_lock) { _errors.Add(message); }
    }

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

    public (bool Active, int Total, int Done, string Current, string Phase, IReadOnlyList<string> Errors) Snapshot()
    {
        lock (_lock)
        {
            return (_active, _total, _done, _current, _phase, _errors.ToArray());
        }
    }
}
