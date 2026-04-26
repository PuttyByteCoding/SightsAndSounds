namespace VideoOrganizer.API.Services;

// Centralized pause flags for the three background workers. Each worker
// reads its flag before starting a new item and bails out (or waits) when
// paused. Toggled via /api/{worker}/pause and /resume endpoints.
//
// Granularity: paused workers finish their current item, then stop. They
// don't cancel mid-flight — that's what the per-card "Skip" buttons are
// for. Resume immediately wakes the worker so it picks the next item.
public sealed class WorkerPauseStatus
{
    private volatile bool _importPaused;
    private volatile bool _thumbnailsPaused;
    private volatile bool _md5Paused;

    public bool ImportPaused
    {
        get => _importPaused;
        set => _importPaused = value;
    }

    public bool ThumbnailsPaused
    {
        get => _thumbnailsPaused;
        set => _thumbnailsPaused = value;
    }

    public bool Md5Paused
    {
        get => _md5Paused;
        set => _md5Paused = value;
    }
}
