using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared.Configuration;

namespace VideoOrganizer.API.Services;

// Background worker that computes MD5 hashes for videos that were imported
// without one. Directory import deliberately skips MD5 so it stays fast on
// large files; this service does the heavy lifting afterwards and also runs
// the cross-video dedup check once the hash exists.
//
// Duplicate handling: when the computed hash matches an *already-hashed*
// video, we re-set NeedsReview = true and append a "[MD5 duplicate of ...]"
// line to Video.Notes so the user can investigate on the Player page
// instead of having the backfill silently delete or merge.
public sealed class Md5BackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Md5BackfillService> _logger;
    private readonly Md5BackfillProgressTracker _progress;
    private readonly Md5BackfillSignal _signal;
    private readonly WorkerPauseStatus _pauseStatus;

    // Tunables sourced from BackgroundWorkers:Md5Backfill in appsettings.json.
    private readonly TimeSpan _perFileCooldown;
    private readonly TimeSpan _perFileTimeout;
    private readonly TimeSpan _importGrace;

    public Md5BackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<Md5BackfillService> logger,
        Md5BackfillProgressTracker progress,
        Md5BackfillSignal signal,
        BackgroundWorkerOptions workerOptions,
        WorkerPauseStatus pauseStatus)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _progress = progress;
        _signal = signal;
        _pauseStatus = pauseStatus;
        var m = workerOptions.Md5Backfill;
        _perFileCooldown = TimeSpan.FromMilliseconds(Math.Max(0, m.PerFileCooldownMilliseconds));
        _perFileTimeout  = TimeSpan.FromSeconds(Math.Max(10, m.PerFileTimeoutSeconds));
        _importGrace     = TimeSpan.FromSeconds(Math.Max(0, m.ImportGraceSeconds));
    }

    // Wake-up model mirrors ThumbnailWarmingService: process whatever is
    // pending at startup, then wait indefinitely on the wake signal. No
    // periodic rescan — signals come from finished imports, "Scan now", or
    // "Retry failed".
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Md5BackfillService started.");

        // Let migrations / seeding / initial startup settle before we start
        // pulling large files off disk.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessPendingAsync(stoppingToken);
                if (processed == 0)
                {
                    // Wait indefinitely for someone to wake us. Cancellation
                    // throws OCE; we treat it as shutdown.
                    try
                    {
                        await _signal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }

                    // Grace window so bursty wake-ups (multi-folder import,
                    // rapid Scan-now clicks) collapse into one scan pass.
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
                _logger.LogError(ex, "Md5BackfillService loop error; backing off before retry");
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        _logger.LogInformation("Md5BackfillService stopping.");
    }

    private async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        List<(Guid id, string path)> pending;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            pending = await db.Videos
                .AsNoTracking()
                .Where(v => v.Md5 == null && !v.Md5Failed)
                .OrderBy(v => v.IngestDate)
                .Take(100) // chunk so each scoped DbContext stays short-lived
                .Select(v => new { v.Id, v.FilePath })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(x => (x.Id, x.FilePath)).ToList(), ct);
        }

        if (pending.Count == 0) return 0;
        _logger.LogInformation("Md5BackfillService: {Count} videos pending MD5.", pending.Count);

        // Publish the batch so the Background Tasks page can surface what's
        // coming up next. Paths drop off as each file is consumed.
        _progress.SetQueue(pending.Select(p => p.path));

        var processed = 0;
        foreach (var (id, path) in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (_pauseStatus.Md5Paused)
            {
                _logger.LogInformation("Md5BackfillService paused — stopping batch.");
                break;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _logger.LogDebug("Skipping MD5 for {VideoId}: file missing ({Path})", id, path);
                _progress.Dequeue(path);
                continue;
            }

            string md5;
            var failedReason = (string?)null;
            using var perFile = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perFile.CancelAfter(_perFileTimeout);
            try
            {
                var info = new FileInfo(path);
                _progress.Start(info.Name, path, info.Exists ? info.Length : 0);
                md5 = await ComputeMd5Async(path, _progress, perFile.Token);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Distinguish user-skip from auto-timeout. RequestSkip throws
                // OCE without firing the linked CTS's CancelAfter — so if the
                // skip flag is set, attribute the cancel to the user.
                if (_progress.IsSkipRequested)
                {
                    failedReason = "skipped manually via Skip button";
                    _logger.LogWarning(
                        "MD5 compute skipped manually via Skip button for {VideoId} at {Path}",
                        id, path);
                }
                else
                {
                    failedReason = $"timed out after {_perFileTimeout.TotalSeconds:0}s";
                    _logger.LogError("MD5 compute timed out (>{Timeout}) for {VideoId} at {Path}",
                        _perFileTimeout, id, path);
                }
                md5 = string.Empty; // not used; Md5Failed flag is what matters
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MD5 compute failed for {VideoId} at {Path}", id, path);
                await MarkMd5FailedAsync(id, TruncateError(ex.Message), ct);
                _progress.Clear();
                _progress.Dequeue(path);
                continue;
            }

            if (failedReason != null)
            {
                await MarkMd5FailedAsync(id, failedReason, ct);
                _progress.Clear();
                _progress.Dequeue(path);
                continue;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();

                var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
                if (video == null)
                {
                    // Video was deleted between the queue load and the hash
                    // compute. The MD5 we just calculated is dropped on the
                    // floor — log so the queue's "processed" counter doesn't
                    // look mysteriously inflated against the DB.
                    _logger.LogDebug(
                        "MD5 backfill skipped {VideoId} ({Path}) — row was deleted mid-flight, hash discarded",
                        id, path);
                    continue;
                }

                var existingDuplicate = await db.Videos
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Md5 == md5 && v.Id != id, ct);

                if (existingDuplicate != null)
                {
                    _logger.LogWarning(
                        "MD5 duplicate: {NewId} ({NewPath}) matches {ExistingId} ({ExistingPath})",
                        id, path, existingDuplicate.Id, existingDuplicate.FilePath);
                    video.NeedsReview = true;
                    var note = $"[MD5 duplicate of {existingDuplicate.FilePath}]";
                    if (!video.Notes.Contains(note)) video.Notes = string.IsNullOrWhiteSpace(video.Notes) ? note : $"{video.Notes}\n{note}";
                    // Still set the MD5 so a later run doesn't re-pick this row.
                }

                video.Md5 = md5;
                await db.SaveChangesAsync(ct);
                processed++;
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving MD5 failed for {VideoId}", id);
            }
            finally
            {
                _progress.Clear();
                _progress.Dequeue(path);
            }

            try { await Task.Delay(_perFileCooldown, ct); }
            catch (TaskCanceledException) { break; }
        }

        if (processed > 0)
        {
            _logger.LogInformation("Md5BackfillService: hashed {Count} videos", processed);
        }
        return processed;
    }

    // Persist Md5Failed=true (plus a captured error reason) so the worker
    // stops re-trying this row each scan and the UI can render a "MD5
    // failed" badge with a real "why" in the Show Failed table.
    private async Task MarkMd5FailedAsync(Guid id, string? error, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            var v = await db.Videos.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v != null)
            {
                v.Md5Failed = true;
                v.Md5FailedError = error;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (TaskCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist Md5Failed for {VideoId}", id);
        }
    }

    // Mirror of ThumbnailWarmingService.TruncateError — keep the persisted
    // string bounded.
    private static string TruncateError(string s) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= 500 ? s : s[..500] + "…");

    private static async Task<string> ComputeMd5Async(string path, Md5BackfillProgressTracker progress, CancellationToken ct)
    {
        using var md5Alg = MD5.Create();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1024 * 256,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        var buffer = new byte[1024 * 256];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            // User-requested skip from Background Tasks: throw OCE so the
            // outer catch records "skipped" and moves on to the next file.
            if (progress.IsSkipRequested) throw new OperationCanceledException();
            md5Alg.TransformBlock(buffer, 0, read, null, 0);
            totalRead += read;
            progress.Update(totalRead);
        }
        md5Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(md5Alg.Hash ?? Array.Empty<byte>()).Replace("-", string.Empty).ToLowerInvariant();
    }
}
