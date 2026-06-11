using Microsoft.Extensions.Logging;

namespace VideoOrganizer.API.Services;

// Centralized pause flags for the three background workers. Each worker
// reads its flag before starting a new item and bails out (or waits) when
// paused. Toggled via /api/{worker}/pause and /resume endpoints.
//
// Granularity: paused workers finish their current item, then stop. They
// don't cancel mid-flight — that's what the per-card "Skip" buttons are
// for. Resume immediately wakes the worker so it picks the next item.
//
// Logging: every state change is recorded so the audit trail in Seq /
// the in-memory log buffer can answer "when did the import worker get
// paused, and was it still paused when X happened?" — useful when
// triaging stuck queues.
public sealed class WorkerPauseStatus
{
    private readonly ILogger<WorkerPauseStatus> _logger;
    private volatile bool _importPaused;
    private volatile bool _thumbnailsPaused;
    private volatile bool _md5Paused;

    public WorkerPauseStatus(ILogger<WorkerPauseStatus> logger)
    {
        _logger = logger;
    }

    public bool ImportPaused
    {
        get => _importPaused;
        set
        {
            if (_importPaused == value) return;
            _importPaused = value;
            LogTransition("Import", value);
        }
    }

    public bool ThumbnailsPaused
    {
        get => _thumbnailsPaused;
        set
        {
            if (_thumbnailsPaused == value) return;
            _thumbnailsPaused = value;
            LogTransition("Thumbnails", value);
        }
    }

    public bool Md5Paused
    {
        get => _md5Paused;
        set
        {
            if (_md5Paused == value) return;
            _md5Paused = value;
            LogTransition("Md5", value);
        }
    }

    // No-op writes (idempotent /pause requests on an already-paused worker)
    // are filtered above so they don't pollute the feed.
    private void LogTransition(string worker, bool paused) =>
        _logger.LogInformation("Worker {Worker} {Action}", worker, paused ? "paused" : "resumed");
}
