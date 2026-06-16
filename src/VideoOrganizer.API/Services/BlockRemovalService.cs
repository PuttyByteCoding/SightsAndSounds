using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API.Services;

// Creates a new file with the "Hide" blocks removed (issue #70). For each video
// it computes the keep-segments (the gaps between Hide blocks), stream-copies
// each, concatenates them losslessly into "<stem>_trimmed<ext>" next to the
// source, and ingests that as a fresh top-level video carrying the source's
// tags. The original is deleted by the caller (the page marks it for deletion
// via the normal purge flow once the run succeeds). No re-encode — cuts snap to
// keyframes. Reuses the shared MediaExport engine (with #69).
public sealed class BlockRemovalService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVideoMetadataService _metadata;
    private readonly BlockRemovalProgress _progress;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly ThumbnailWarmingSignal _thumbSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BlockRemovalService> _logger;

    public BlockRemovalService(
        IServiceScopeFactory scopeFactory, IVideoMetadataService metadata,
        BlockRemovalProgress progress, Md5BackfillSignal md5Signal,
        ThumbnailWarmingSignal thumbSignal, IHostApplicationLifetime lifetime,
        ILogger<BlockRemovalService> logger)
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
            var rows = await db.Videos.AsNoTracking()
                .Where(v => videoIds.Contains(v.Id) && v.ParentVideoId == null)
                .Select(v => new { v.Id, v.VideoBlocks })
                .ToListAsync(ct);
            valid = rows
                .Where(r => r.VideoBlocks.Any(b => b.VideoBlockType == VideoBlockTypes.Hide))
                .Select(r => r.Id)
                .ToList();
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
                    await TrimOneAsync(id, jobId, ct);
                    producedAny = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove blocks from {VideoId}", id);
                    _progress.AddError($"{id}: {ex.Message}");
                }
                finally { _progress.CompletedOne(); }
            }

            if (producedAny) { _md5Signal.Signal(); _thumbSignal.Signal(); }
            _progress.End("done");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Block removal run failed");
            _progress.AddError(ex.Message);
            _progress.End("error");
        }
    }

    private async Task TrimOneAsync(Guid videoId, Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();

        var video = await db.Videos.Include(v => v.VideoTags)
            .FirstOrDefaultAsync(v => v.Id == videoId, ct)
            ?? throw new InvalidOperationException("Video not found.");
        if (!File.Exists(video.FilePath))
            throw new InvalidOperationException("Source file is missing on disk.");

        _progress.SetCurrent(video.FileName);

        var keep = KeepSegments(video.VideoBlocks, video.Duration.TotalSeconds);
        if (keep.Count == 0)
            throw new InvalidOperationException("The whole video is hidden — nothing left to keep.");

        var outPath = MediaExport.ComputeOutputPath(video.FilePath, "_trimmed");
        var tempDir = Path.Combine(Path.GetTempPath(), "sas-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            if (keep.Count == 1)
            {
                await ExtractSegmentAsync(video.FilePath, keep[0].Start, keep[0].Duration, outPath, ct);
            }
            else
            {
                var ext = Path.GetExtension(video.FilePath);
                var segPaths = new List<string>();
                for (var i = 0; i < keep.Count; i++)
                {
                    var seg = Path.Combine(tempDir, $"seg_{i:D4}{ext}");
                    await ExtractSegmentAsync(video.FilePath, keep[i].Start, keep[i].Duration, seg, ct);
                    segPaths.Add(seg);
                }
                await ConcatAsync(segPaths, outPath, tempDir, ct);
            }
            if (!File.Exists(outPath))
                throw new InvalidOperationException("ffmpeg produced no output file.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        // Ingest the trimmed file as a fresh video, carrying the source's tags
        // plus a "Trimmed" tag so block-removed files are identifiable (#70).
        var trimmed = await MediaExport.BuildVideoFromFileAsync(_metadata, outPath, jobId, _logger, ct);
        var trimmedTagId = await MediaExport.GetOrCreateTagAsync(db, "Trimmed", "Trimmed", ct);
        var tagIds = video.VideoTags.Select(t => t.TagId).ToHashSet();
        tagIds.Add(trimmedTagId);
        foreach (var tid in tagIds)
            trimmed.VideoTags.Add(new VideoTag { TagId = tid });
        db.Videos.Add(trimmed);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Trimmed {VideoId} -> {Path} ({NewId})", videoId, outPath, trimmed.Id);
    }

    // The kept ranges: [0, duration] minus the merged Hide intervals. Block
    // bounds are whole seconds; the final keep runs to the true (fractional) end.
    internal static List<(double Start, double Duration)> KeepSegments(
        IReadOnlyList<VideoBlock> blocks, double durationSeconds)
    {
        if (durationSeconds <= 0) return new();

        var hides = blocks
            .Where(b => b.VideoBlockType == VideoBlockTypes.Hide)
            .Select(b => (Start: (double)Math.Max(0, b.OffsetInSeconds),
                          End: Math.Min(durationSeconds, (double)(b.OffsetInSeconds + b.LengthInSeconds))))
            .Where(h => h.End > h.Start)
            .OrderBy(h => h.Start)
            .ToList();

        // Merge overlapping/adjacent hides.
        var merged = new List<(double Start, double End)>();
        foreach (var h in hides)
        {
            if (merged.Count > 0 && h.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, h.End));
            else
                merged.Add(h);
        }

        // Complement within [0, duration].
        var keep = new List<(double Start, double Duration)>();
        var cursor = 0.0;
        foreach (var (s, e) in merged)
        {
            if (s > cursor) keep.Add((cursor, s - cursor));
            cursor = Math.Max(cursor, e);
        }
        if (cursor < durationSeconds - 0.05) keep.Add((cursor, durationSeconds - cursor));
        return keep;
    }

    private static Task ExtractSegmentAsync(string input, double start, double duration, string outPath, CancellationToken ct)
        => MediaExport.RunFfmpegAsync(ct,
            "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:v?", "-map", "0:a?",
            "-c", "copy", "-avoid_negative_ts", "make_zero",
            outPath);

    private static async Task ConcatAsync(List<string> segPaths, string outPath, string tempDir, CancellationToken ct)
    {
        // ffmpeg concat demuxer over a list file, stream-copied. Single-quoted
        // paths with internal quotes escaped per the demuxer's rules.
        var sb = new StringBuilder();
        foreach (var p in segPaths)
            sb.Append("file '").Append(p.Replace("'", @"'\''")).Append("'\n");
        var listPath = Path.Combine(tempDir, "concat.txt");
        await File.WriteAllTextAsync(listPath, sb.ToString(), ct);

        await MediaExport.RunFfmpegAsync(ct,
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c", "copy", outPath);
    }
}
