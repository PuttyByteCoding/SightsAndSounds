using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared.Configuration;

namespace VideoOrganizer.API.Services;

// Background worker that pre-generates the scrub sprite+VTT for any video
// that doesn't have one on disk yet. Runs one video at a time at low priority
// so user-initiated requests still feel snappy.
//
// Wake-up model: the worker no longer auto-rescans on a timer. It processes
// whatever is pending at startup, then waits indefinitely on the wake signal.
// Signals come from finished imports, the user clicking "Scan now", or the
// "Retry failed" button — all funnel through ThumbnailWarmingSignal.Signal().
public sealed class ThumbnailWarmingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IThumbnailGenerator _generator;
    private readonly ILogger<ThumbnailWarmingService> _logger;
    private readonly ThumbnailWarmingSignal _signal;
    private readonly ThumbnailWarmingProgressTracker _progress;
    private readonly WorkerPauseStatus _pauseStatus;
    private readonly string _cacheDir;
    // Tunables sourced from BackgroundWorkers:ThumbnailWarming in
    // appsettings.json. Defaults preserved when the section is absent.
    private readonly TimeSpan _perVideoCooldown;
    private readonly TimeSpan _perVideoTimeout;
    private readonly TimeSpan _importGrace;

    public ThumbnailWarmingService(
        IServiceScopeFactory scopeFactory,
        IThumbnailGenerator generator,
        ILogger<ThumbnailWarmingService> logger,
        VideoStorageOptions storageOptions,
        BackgroundWorkerOptions workerOptions,
        ThumbnailWarmingSignal signal,
        ThumbnailWarmingProgressTracker progress,
        WorkerPauseStatus pauseStatus)
    {
        _scopeFactory = scopeFactory;
        _generator = generator;
        _logger = logger;
        _signal = signal;
        _progress = progress;
        _pauseStatus = pauseStatus;
        _cacheDir = !string.IsNullOrWhiteSpace(storageOptions.ThumbnailsDirectory)
            ? storageOptions.ThumbnailsDirectory
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "video-thumbnails");
        var t = workerOptions.ThumbnailWarming;
        _perVideoCooldown = TimeSpan.FromSeconds(Math.Max(0, t.PerVideoCooldownSeconds));
        _perVideoTimeout  = TimeSpan.FromSeconds(Math.Max(10, t.PerVideoTimeoutSeconds));
        _importGrace      = TimeSpan.FromSeconds(Math.Max(0, t.ImportGraceSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ThumbnailWarmingService started.");

        // Brief startup delay so the API is done migrating/settling before we
        // start competing for disk IO.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessPendingAsync(stoppingToken);
                if (processed == 0)
                {
                    // No more periodic rescan — wait indefinitely until
                    // someone fires the signal (finished import / Scan now /
                    // Retry failed). Cancellation throws OCE; we treat that
                    // as shutdown.
                    try
                    {
                        await _signal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }

                    // Brief grace window after wake. Lets a multi-folder
                    // import (or rapid Scan-now/Retry-failed clicks) collapse
                    // into a single ProcessPending pass. Surfaced in the UI
                    // as "Import detected — starting in Xs".
                    if (_importGrace > TimeSpan.Zero)
                    {
                        _signal.ImportDetectedAt = DateTime.UtcNow;
                        _signal.NextScanAt = DateTime.UtcNow + _importGrace;
                        try { await Task.Delay(_importGrace, stoppingToken); }
                        catch (TaskCanceledException) { break; }
                        finally
                        {
                            _signal.NextScanAt = null;
                            _signal.ImportDetectedAt = null;
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ThumbnailWarmingService loop error; backing off before retry");
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        _logger.LogInformation("ThumbnailWarmingService stopping.");
    }

    // Scans for videos without cached sprites and processes them one at a time.
    // Returns the number it warmed (0 if nothing pending).
    private async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        List<(Guid id, string path, bool alreadyFailed)> candidates;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            // Only warm videos belonging to enabled sets — no point spending cycles on
            // ones the user has hidden.
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);
            if (enabledRoots.Count == 0) return 0;

            // Order by IngestDate so once the import-phase queue serializes
            // the saves, Import1's videos drain through this worker before
            // Import2's. Mirrors Md5BackfillService.OrderBy(IngestDate).
            candidates = await db.Videos
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .Select(v => new { v.Id, v.FilePath, v.ThumbnailsFailed, v.IngestDate })
                .OrderBy(v => v.IngestDate)
                .ThenBy(v => v.Id)
                .ToListAsync(ct)
                .ContinueWith(t => t.Result
                    .Select(x => (x.Id, x.FilePath, x.ThumbnailsFailed)).ToList(), ct);
        }

        // Filter to ones that actually need warming (no sprite on disk) and
        // haven't been flagged as failed. Publish the queue so the UI can
        // show what's coming up.
        var pending = candidates
            .Where(c => !c.alreadyFailed && !IsAlreadyWarmed(c.id))
            .ToList();
        if (pending.Count == 0) return 0;
        _progress.SetQueue(pending.Select(p => p.path));

        var processed = 0;
        foreach (var (id, path, _) in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Pause check before each item. Paused → exit the batch and
            // wait on the signal until /resume fires it. Current item is
            // not started; the queue stays in place.
            if (_pauseStatus.ThumbnailsPaused)
            {
                _logger.LogInformation("ThumbnailWarmingService paused — stopping batch.");
                break;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _logger.LogDebug("Skipping warm for {VideoId}: file missing ({Path})", id, path);
                _progress.Dequeue(path ?? string.Empty);
                continue;
            }

            // Per-video CTS so a hang is bounded by _perVideoTimeout AND a
            // user-requested skip via the progress tracker can cancel mid-
            // ffmpeg. Polling task watches the skip flag.
            using var perVideo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perVideo.CancelAfter(_perVideoTimeout);
            _progress.Start(id, path);
            using var skipPoll = new CancellationTokenSource();
            var pollTask = Task.Run(async () =>
            {
                while (!skipPoll.IsCancellationRequested)
                {
                    if (_progress.IsSkipRequested) { perVideo.Cancel(); return; }
                    try { await Task.Delay(500, skipPoll.Token); }
                    catch (TaskCanceledException) { return; }
                }
            });

            var failed = false;
            var skipped = false;
            var succeeded = false;
            string? failedError = null;
            try
            {
                _logger.LogInformation("Warming thumbnails for {VideoId}", id);
                await _generator.GenerateThumbnailsAsync(path, id, ct: perVideo.Token);
                processed++;
                succeeded = true;
            }
            catch (OperationCanceledException) when (perVideo.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (_progress.IsSkipRequested)
                {
                    skipped = true;
                    failedError = "skipped manually via Skip button";
                    _logger.LogWarning(
                        "Thumbnail warming skipped manually via Skip button for {VideoId} at {Path}",
                        id, path);
                }
                else
                {
                    failed = true;
                    failedError = $"timed out after {_perVideoTimeout.TotalSeconds:0}s";
                    _logger.LogError("Thumbnail warming timed out (>{Timeout}) for {VideoId} at {Path}",
                        _perVideoTimeout, id, path);
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                failed = true;
                failedError = TruncateError(ex.Message);
                _logger.LogWarning(ex, "Thumbnail warming failed for {VideoId} at {Path}", id, path);
            }
            finally
            {
                skipPoll.Cancel();
                try { await pollTask; } catch { /* ignore */ }
                _progress.Clear();
                _progress.Dequeue(path);
            }

            if (failed || skipped)
            {
                // Persist the failure so the next scan doesn't pick this row
                // up again, and the UI can render a "Thumbnail failed" badge.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
                    var v = await db.Videos.FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (v != null)
                    {
                        v.ThumbnailsFailed = true;
                        v.ThumbnailsFailedError = failedError;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist ThumbnailsFailed for {VideoId}", id);
                }
            }
            else if (succeeded)
            {
                // Persist the "warmed" state so the per-import progress UI
                // can count generated rows without per-video disk hits.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
                    var v = await db.Videos.FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (v != null)
                    {
                        v.ThumbnailsGenerated = true;
                        v.ThumbnailsFailed = false;
                        v.ThumbnailsFailedError = null;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist ThumbnailsGenerated for {VideoId}", id);
                }
            }

            try { await Task.Delay(_perVideoCooldown, ct); } catch (TaskCanceledException) { break; }
        }

        if (processed > 0)
        {
            _logger.LogInformation("ThumbnailWarmingService: warmed {Count} videos", processed);
        }
        return processed;
    }

    private bool IsAlreadyWarmed(Guid videoId)
    {
        var dir = Path.Combine(_cacheDir, videoId.ToString());
        return File.Exists(Path.Combine(dir, "sprite.jpg"))
            && File.Exists(Path.Combine(dir, "thumbnails.vtt"));
    }

    // Caps the persisted error string so a runaway exception (e.g. a 5KB
    // ffmpeg stderr blob) doesn't blow up the row size. The full message
    // still lands in the logger.
    private static string TruncateError(string s) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= 500 ? s : s[..500] + "…");
}
