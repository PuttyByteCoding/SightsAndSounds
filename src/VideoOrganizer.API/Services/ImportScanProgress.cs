namespace VideoOrganizer.API.Services;

// Live progress for an in-flight import directory scan (issue #27).
//
// The /import/browse recursive walk calls Increment() for each video file
// it discovers; the Import page polls GET /import/scan-progress to show a
// climbing "Discovered N video files…" count while a source loads, instead
// of a blind spinner.
//
// Discovered resets when a fresh scan starts (active count goes 0 -> 1)
// and otherwise holds its last value, so the final poll after a scan ends
// still reports the total. Overlapping scans share the counter — an
// acceptable approximation for a progress indicator. Restart-resets-to-
// empty is fine (this is in-memory only).
public sealed class ImportScanProgress
{
    private readonly object _lock = new();
    private int _active;
    private int _discovered;

    // Mark the start of a scan. The first concurrent scan zeroes the
    // counter so each fresh load starts from 0.
    public void Begin()
    {
        lock (_lock)
        {
            if (_active == 0) Interlocked.Exchange(ref _discovered, 0);
            _active++;
        }
    }

    // Mark the end of a scan. Discovered is left as-is so a final poll
    // still sees the total.
    public void End()
    {
        lock (_lock)
        {
            if (_active > 0) _active--;
        }
    }

    // Called per discovered video file — hot path, so interlocked rather
    // than taking the lock.
    public void Increment() => Interlocked.Increment(ref _discovered);

    // Bulk bump for a folder whose recursive count was served from the
    // scan cache (issue #4): the walk is skipped, but the discovered total
    // should still reflect those files so the count stays meaningful.
    public void Add(int n) => Interlocked.Add(ref _discovered, n);

    public (bool Scanning, int Discovered) Snapshot()
    {
        lock (_lock)
        {
            return (_active > 0, Volatile.Read(ref _discovered));
        }
    }
}
