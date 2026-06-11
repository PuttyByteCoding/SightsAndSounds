using System.Threading.Channels;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API.Services;

// Single-consumer queue that serializes the Add-to-database phase across
// import requests. Each POST to /api/import/directory enqueues a job here
// and returns immediately; this service pulls one job at a time, runs the
// inner DirectoryImportService, marks the tracker complete, and signals
// the thumbnail / Md5 workers.
//
// Why serialize the import phase: the user wants strict FIFO so Import1
// finishes saving its Videos before Import2 starts. Combined with the
// downstream workers ordering by IngestDate, that gives the global order
// the user expects (Import1 thumbs → Import2 thumbs, Import1 Md5 →
// Import2 Md5).
public sealed record QueuedImport(Guid JobId, DirectoryImportRequest Request);

public sealed class ImportQueueService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ImportProgressTracker _progressTracker;
    private readonly ThumbnailWarmingSignal _thumbSignal;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly WorkerPauseStatus _pauseStatus;
    private readonly ILogger<ImportQueueService> _logger;
    // Unbounded channel — POST handlers must never block on enqueue. The
    // queue is in-memory only, so a long backlog just means more pending
    // jobs the user can see on Background Tasks.
    private readonly Channel<QueuedImport> _channel = Channel.CreateUnbounded<QueuedImport>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ImportQueueService(
        IServiceScopeFactory scopeFactory,
        ImportProgressTracker progressTracker,
        ThumbnailWarmingSignal thumbSignal,
        Md5BackfillSignal md5Signal,
        WorkerPauseStatus pauseStatus,
        ILogger<ImportQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _progressTracker = progressTracker;
        _thumbSignal = thumbSignal;
        _md5Signal = md5Signal;
        _pauseStatus = pauseStatus;
        _logger = logger;
    }

    // Returns false only if the channel is somehow closed (shutdown). On
    // success the job will be picked up in FIFO order. The caller should
    // already have created the job in the tracker via StartJob() so the
    // job is visible to /api/import/jobs as "queued" until we pull it.
    public bool Enqueue(QueuedImport item)
    {
        var ok = _channel.Writer.TryWrite(item);
        if (!ok)
        {
            // The only way TryWrite fails on an unbounded channel is if the
            // writer has been completed — i.e. the host is shutting down.
            // Surface it so the orphaned job in the tracker has a paper trail.
            _logger.LogWarning(
                "Enqueue refused for import job {JobId} — queue is closed (shutdown in progress?)",
                item.JobId);
        }
        return ok;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ImportQueueService started.");
        try
        {
            // Wait-then-pull pattern (instead of ReadAllAsync) so we can
            // hold the next pull while the worker is paused. The peek of
            // the channel happens *after* the pause check, so a paused
            // worker doesn't dequeue an item it's not going to process.
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_pauseStatus.ImportPaused && !stoppingToken.IsCancellationRequested)
                {
                    try { await Task.Delay(500, stoppingToken); }
                    catch (OperationCanceledException) { return; }
                }
                if (_channel.Reader.TryRead(out var item))
                {
                    await ProcessOneAsync(item, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — nothing to clean up; pending items will be lost
            // but the user can resubmit.
        }
        _logger.LogInformation("ImportQueueService stopping.");
    }

    private async Task ProcessOneAsync(QueuedImport item, CancellationToken stoppingToken)
    {
        try
        {
            _progressTracker.MarkRunning(item.JobId);
            _progressTracker.AddMessage(item.JobId, "Add-to-database phase started.");

            using var scope = _scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IDirectoryImportService>();

            var r = item.Request;
            await importService.ImportFromDirectoryAsync(
                r.DirectoryPath,
                r.InitialTagIds,
                r.Notes,
                r.IncludeSubdirectories,
                item.JobId,
                stoppingToken);

            _progressTracker.MarkCompleted(item.JobId);
            // Wake the downstream workers so the new Videos start getting
            // thumbnails + Md5 right away. They serialize among themselves
            // (single worker each) and order by IngestDate, so Import1's
            // videos drain before Import2's.
            _thumbSignal.Signal();
            _md5Signal.Signal();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import job {JobId} failed", item.JobId);
            _progressTracker.MarkFailed(item.JobId, ex.Message);
        }
    }
}
