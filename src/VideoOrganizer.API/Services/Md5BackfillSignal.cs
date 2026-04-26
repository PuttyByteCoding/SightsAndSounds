namespace VideoOrganizer.API.Services;

// Wake signal for Md5BackfillService, mirror of ThumbnailWarmingSignal. The
// service waits indefinitely on this when idle — no auto-rescan timer.
// Signals come from finished imports, "Scan now", and "Retry failed".
public sealed class Md5BackfillSignal
{
    private readonly SemaphoreSlim _sem = new(0, 1);

    // Set during the post-wake grace window so the UI can render
    // "Import detected — starting in Xs". Null otherwise.
    public DateTime? NextScanAt { get; set; }

    // Set by the worker when it enters the post-signal grace window and
    // cleared when that window ends. Exposed on the status endpoint so the
    // UI can show "Import detected — starting in Xs". Not set when the
    // worker is already mid-batch (no grace applies there).
    public DateTime? ImportDetectedAt { get; set; }

    public void Signal()
    {
        if (_sem.CurrentCount == 0)
        {
            try { _sem.Release(); } catch (SemaphoreFullException) { /* already signaled, ignore */ }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _sem.WaitAsync(timeout, ct);
}
