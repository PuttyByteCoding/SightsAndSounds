using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared.Dto;
using Xabe.FFmpeg;

namespace VideoOrganizer.API.Services;

// Exports clips (child Video rows with a [start,end] range over a parent file)
// into their own standalone files (issue #69). Per clip: ffmpeg stream-copies
// the range to "<parentstem>_clip<ext>" next to the parent, then ingests that
// file as a brand-new top-level Video (ffprobe metadata + Md5/thumbnail
// backfill, exactly like a normal import), copies the clip's tags plus a "Clip"
// tag, and marks the source clip exported (so it leaves the library but the
// parent keeps a breadcrumb band). No re-encode — fast, lossless, keyframe-snapped.
public sealed class ClipExportService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVideoMetadataService _metadata;
    private readonly ClipExportProgress _progress;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly ThumbnailWarmingSignal _thumbSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ClipExportService> _logger;

    public ClipExportService(
        IServiceScopeFactory scopeFactory, IVideoMetadataService metadata,
        ClipExportProgress progress, Md5BackfillSignal md5Signal,
        ThumbnailWarmingSignal thumbSignal, IHostApplicationLifetime lifetime,
        ILogger<ClipExportService> logger)
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

    public async Task<StartResult> TryStartAsync(IReadOnlyList<ExportClipItem> items, CancellationToken ct)
    {
        if (_progress.IsActive) return StartResult.AlreadyRunning;

        var ids = items.Select(i => i.ClipId).ToList();
        List<Guid> valid;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            valid = await db.Videos.AsNoTracking()
                .Where(v => ids.Contains(v.Id) && v.ParentVideoId != null && !v.ClipExported
                    && !v.MarkedForDeletion
                    && v.ClipStartSeconds != null && v.ClipEndSeconds != null)
                .Select(v => v.Id)
                .ToListAsync(ct);
        }
        if (valid.Count == 0) return StartResult.NothingToDo;

        // Per-clip chosen names (#173); last write wins on dupes.
        var validSet = valid.ToHashSet();
        var nameById = items
            .Where(i => validSet.Contains(i.ClipId))
            .GroupBy(i => i.ClipId)
            .ToDictionary(g => g.Key, g => g.Last().Name);

        _progress.Begin(valid.Count);
        _ = Task.Run(() => RunExportAsync(valid, nameById));
        return StartResult.Started;
    }

    private async Task RunExportAsync(List<Guid> clipIds, Dictionary<Guid, string?> nameById)
    {
        var ct = _lifetime.ApplicationStopping;
        // One import job id groups this run's new files in the Background Tasks UI.
        var jobId = Guid.NewGuid();
        var producedAny = false;
        try
        {
            foreach (var clipId in clipIds)
            {
                if (_progress.StopRequested || ct.IsCancellationRequested) break;
                try
                {
                    var name = await ExportOneAsync(clipId, nameById.GetValueOrDefault(clipId), jobId, ct);
                    _progress.SetCurrent(name);
                    producedAny = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to export clip {ClipId}", clipId);
                    _progress.AddError($"{clipId}: {ex.Message}");
                }
                finally
                {
                    _progress.CompletedOne();
                }
            }

            // Wake the background workers once so the new files get hashed +
            // thumbnailed (they were created with Md5 = null / no sprite).
            if (producedAny) { _md5Signal.Signal(); _thumbSignal.Signal(); }
            _progress.End("done");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip export run failed");
            _progress.AddError(ex.Message);
            _progress.End("error");
        }
    }

    private async Task<string> ExportOneAsync(Guid clipId, string? desiredName, Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();

        var clip = await db.Videos.Include(v => v.VideoTags)
            .FirstOrDefaultAsync(v => v.Id == clipId, ct)
            ?? throw new InvalidOperationException("Clip not found.");
        if (clip.ParentVideoId is null) throw new InvalidOperationException("Not a clip.");
        if (clip.ClipStartSeconds is null || clip.ClipEndSeconds is null)
            throw new InvalidOperationException("Clip has no range.");

        var parent = await db.Videos.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == clip.ParentVideoId, ct)
            ?? throw new InvalidOperationException("Parent not found.");
        if (!File.Exists(parent.FilePath))
            throw new InvalidOperationException("Parent file is missing on disk.");

        _progress.SetCurrent(clip.FileName);

        var start = clip.ClipStartSeconds.Value;
        var duration = Math.Max(0.05, clip.ClipEndSeconds.Value - start);
        // User-chosen name (#173), else the default "<parent>_clip".
        var outPath = string.IsNullOrWhiteSpace(desiredName)
            ? MediaExport.ComputeOutputPath(parent.FilePath, "_clip")
            : MediaExport.ComputeNamedOutputPath(parent.FilePath, desiredName!);

        // ffmpeg stream-copy the [start, start+duration] range. -ss before -i is
        // a fast input seek that snaps to the keyframe at/before start; -c copy
        // avoids any re-encode. -map 0:v?/0:a? carries the video + audio streams.
        await MediaExport.RunFfmpegAsync(ct,
            "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", parent.FilePath,
            "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:v?", "-map", "0:a?",
            "-c", "copy", "-avoid_negative_ts", "make_zero",
            outPath);
        if (!File.Exists(outPath))
            throw new InvalidOperationException("ffmpeg produced no output file.");

        // Ingest the new file as a fresh top-level video.
        var newVideo = await MediaExport.BuildVideoFromFileAsync(_metadata, outPath, jobId, _logger, ct);

        // Carry the clip's tags, and flag the new file as a Clip / Exported
        // clip (#167) — flags, not tags.
        foreach (var t in clip.VideoTags)
            newVideo.VideoTags.Add(new VideoTag { TagId = t.TagId });
        newVideo.IsClip = true;
        newVideo.IsExportedClip = true;

        db.Videos.Add(newVideo);

        // Mark the source clip exported (breadcrumb on the parent; hidden from
        // the library + export queue).
        clip.ClipExported = true;
        clip.ExportedToVideoId = newVideo.Id;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Exported clip {ClipId} -> {Path} ({NewId})", clipId, outPath, newVideo.Id);
        return Path.GetFileName(outPath);
    }

    // Keyframe-snapped cut points for the preview (issue #69): with input-seek
    // stream copy the output actually begins at the keyframe at/before the
    // requested start, so the user sees a little lead-in. Returns that snapped
    // start. Best-effort — falls back to the requested start on any probe issue.
    public async Task<double> GetSnappedStartAsync(string parentPath, double requestedStart, CancellationToken ct)
    {
        if (requestedStart <= 0) return 0;
        var from = Math.Max(0, requestedStart - 15);
        var psi = new ProcessStartInfo
        {
            FileName = MediaExport.ResolveTool("ffprobe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     "-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey",
                     "-show_entries", "frame=pts_time", "-of", "csv=p=0",
                     "-read_intervals", $"{from.ToString("0.###", CultureInfo.InvariantCulture)}%{(requestedStart + 0.5).ToString("0.###", CultureInfo.InvariantCulture)}",
                     parentPath,
                 })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            double best = 0;
            var found = false;
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                    && t <= requestedStart + 0.001 && t >= best)
                {
                    best = t; found = true;
                }
            }
            return found ? best : requestedStart;
        }
        catch
        {
            return requestedStart;
        }
    }
}
