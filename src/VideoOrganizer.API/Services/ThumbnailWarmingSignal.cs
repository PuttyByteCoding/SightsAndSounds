namespace VideoOrganizer.API.Services;

// Wake signal for ThumbnailWarmingService. The service waits indefinitely
// when its pending list is empty — there is no auto-rescan timer. Firing
// this signal is the only thing that wakes it. Sources: finished import,
// the user clicking "Scan now", or "Retry failed" clearing rows.
public sealed class ThumbnailWarmingSignal
{
    // Cap at 1 so repeated signals during a single idle window collapse into
    // one wake-up — we don't need a counted queue, just a nudge.
    private readonly SemaphoreSlim _sem = new(0, 1);

    // Set during the post-wake grace window (DateTime.UtcNow + ImportGrace)
    // so the UI can render "Import detected — starting in Xs". Null
    // otherwise. Outside the grace window, idle vs running is determined by
    // the pending count + the progress tracker's current job.
    public DateTime? NextScanAt { get; set; }

    // Set by the worker during the post-signal grace window so the UI can
    // render "Import detected — starting in Xs". Only meaningful when the
    // worker was sleeping; a signal received while the worker is already
    // processing doesn't add a grace.
    public DateTime? ImportDetectedAt { get; set; }

    public void Signal()
    {
        if (_sem.CurrentCount == 0)
        {
            try { _sem.Release(); } catch (SemaphoreFullException) { /* already signaled, ignore */ }
        }
    }

    // Returns true if woken by a Signal() call, false on timeout. Either way
    // the service should run its scan pass.
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _sem.WaitAsync(timeout, ct);
}
