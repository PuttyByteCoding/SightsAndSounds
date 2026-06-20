using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API.Services;

// Attempts to repair videos that won't play (issue #165) by re-encoding them to
// a browser-friendly H.264/AAC MP4 with error-tolerant flags: ignore decode
// errors, regenerate timestamps, and faststart. This fixes the common cases —
// odd/corrupt containers, broken timestamps, and HEVC that browsers can't play
// — producing a "<stem>_repaired.mp4" ingested as a fresh video. The original
// is left untouched (still flagged) so the user can compare and then delete it.
// One run at a time; reuses the shared MediaExport engine.
public sealed class RepairService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVideoMetadataService _metadata;
    private readonly RepairProgress _progress;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly ThumbnailWarmingSignal _thumbSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RepairService> _logger;

    public RepairService(
        IServiceScopeFactory scopeFactory, IVideoMetadataService metadata,
        RepairProgress progress, Md5BackfillSignal md5Signal,
        ThumbnailWarmingSignal thumbSignal, IHostApplicationLifetime lifetime,
        ILogger<RepairService> logger)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
        _progress = progress;
        _md5Signal = md5Signal;
        _thumbSignal = thumbSignal;
        _lifetime = lifetime;
        _logger = logger;
    }

    public enum StartResult { Started, AlreadyRunning, NothingToDo }

    public async Task<StartResult> TryStartAsync(IReadOnlyList<Guid> videoIds, CancellationToken ct)
    {
        if (_progress.IsActive) return StartResult.AlreadyRunning;

        List<Guid> valid;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            valid = await db.Videos.AsNoTracking()
                .Where(v => videoIds.Contains(v.Id) && v.ParentVideoId == null)
                .Select(v => v.Id)
                .ToListAsync(ct);
        }
        if (valid.Count == 0) return StartResult.NothingToDo;

        _progress.Begin(valid.Count);
        _ = Task.Run(() => RunAsync(valid));
        return StartResult.Started;
    }

    private async Task RunAsync(List<Guid> videoIds)
    {
        var ct = _lifetime.ApplicationStopping;
        var jobId = Guid.NewGuid();
        var producedAny = false;
        try
        {
            foreach (var id in videoIds)
            {
                if (_progress.StopRequested || ct.IsCancellationRequested) break;
                try
                {
                    await RepairOneAsync(id, jobId, ct);
                    producedAny = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to repair {VideoId}", id);
                    _progress.AddError($"{id}: {ex.Message}");
                }
                finally { _progress.CompletedOne(); }
            }

            if (producedAny) { _md5Signal.Signal(); _thumbSignal.Signal(); }
            _progress.End("done");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repair run failed");
            _progress.AddError(ex.Message);
            _progress.End("error");
        }
    }

    private async Task RepairOneAsync(Guid videoId, Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();

        var video = await db.Videos.Include(v => v.VideoTags)
            .FirstOrDefaultAsync(v => v.Id == videoId, ct)
            ?? throw new InvalidOperationException("Video not found.");
        if (!File.Exists(video.FilePath))
            throw new InvalidOperationException("Source file is missing on disk.");

        _progress.SetCurrent(video.FileName);

        var outPath = RepairedOutputPath(video.FilePath);

        // Error-tolerant re-encode to browser-friendly H.264/AAC MP4. -err_detect
        // ignore_err + genpts recover from bad packets / timestamps; faststart
        // moves the moov atom up front for streaming.
        await MediaExport.RunFfmpegAsync(ct,
            "-err_detect", "ignore_err",
            "-fflags", "+genpts",
            "-i", video.FilePath,
            "-map", "0:v:0?", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            outPath);
        if (!File.Exists(outPath))
            throw new InvalidOperationException("ffmpeg produced no output file.");

        var repaired = await MediaExport.BuildVideoFromFileAsync(_metadata, outPath, jobId, _logger, ct);
        if (repaired.Duration <= TimeSpan.Zero)
        {
            // A zero-duration output means the repair didn't really work; don't
            // leave a junk file + row behind.
            try { File.Delete(outPath); } catch { /* best-effort */ }
            throw new InvalidOperationException("Repair produced an unreadable file.");
        }
        foreach (var t in video.VideoTags)
            repaired.VideoTags.Add(new VideoTag { TagId = t.TagId });
        db.Videos.Add(repaired);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Repaired {VideoId} -> {Path} ({NewId})", videoId, outPath, repaired.Id);
    }

    // "<dir>/<stem>_repaired.mp4" (always MP4, since we re-encode to H.264/AAC),
    // collision-safe.
    private static string RepairedOutputPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? "_repaired" : $"_repaired-{i.ToString(CultureInfo.InvariantCulture)}";
            var candidate = Path.Combine(dir, $"{stem}{suffix}.mp4");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
