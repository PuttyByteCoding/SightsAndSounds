using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Configuration;
using VideoOrganizer.Shared.Dto;
using Xabe.FFmpeg;

namespace VideoOrganizer.API;

public static class ApiEndpoints
{
    // In-memory playlist storage (lost on restart, same as before).
    private static readonly Dictionary<Guid, PlaylistDto> _playlists = new();

    // --- Video DTO projection ----------------------------------------------

    private static VideoDto ToDto(Video v)
    {
        var tags = v.VideoTags
            .Where(vt => vt.Tag != null)
            .OrderBy(vt => vt.Tag!.TagGroup?.SortOrder ?? 0)
            .ThenBy(vt => vt.Tag!.SortOrder)
            .ThenBy(vt => vt.Tag!.Name)
            .Select(vt => new VideoTagSummaryDto(
                vt.Tag!.Id,
                vt.Tag.TagGroupId,
                vt.Tag.TagGroup?.Name ?? string.Empty,
                vt.Tag.Name))
            .ToList();

        var props = v.PropertyValues
            .Where(pv => pv.PropertyDefinition != null)
            .OrderBy(pv => pv.PropertyDefinition!.SortOrder)
            .ThenBy(pv => pv.PropertyDefinition!.Name)
            .Select(pv => new PropertyValueDto(
                pv.PropertyDefinitionId,
                pv.PropertyDefinition!.Name,
                (PropertyDataTypeDto)pv.PropertyDefinition.DataType,
                pv.Value))
            .ToList();

        return new VideoDto(
            v.Id, v.FileName, v.FilePath,
            v.Md5, v.Md5Failed, v.Md5FailedError,
            v.ThumbnailsFailed, v.ThumbnailsFailedError, v.ThumbnailsGenerated,
            v.ImportJobId, v.FileSize, v.Duration, v.Height, v.Width,
            (Shared.Dto.VideoDimensionFormat)(int)v.VideoDimensionFormat,
            (Shared.Dto.VideoCodec)(int)v.VideoCodec,
            v.Bitrate, v.FrameRate, v.PixelFormat, v.Ratio, v.CreationTime,
            v.VideoStreamCount, v.AudioStreamCount,
            v.IngestDate,
            (Shared.Dto.CameraTypes)(int)v.CameraType,
            (Shared.Dto.VideoQuality)(int)v.VideoQuality,
            v.WatchCount, v.Notes,
            v.NeedsReview, v.WontPlay, v.MarkedForDeletion,
            v.ParentVideoId, v.ClipStartSeconds, v.ClipEndSeconds,
            v.ParentVideoId.HasValue,
            v.ChapterMarkers.Select(c => new ChapterMarkerDto(c.Offset, c.Comment)).ToList(),
            v.VideoBlocks.Select(b => new VideoBlockDto(
                b.OffsetInSeconds, b.LengthInSeconds, (Shared.Dto.VideoBlockTypes)(int)b.VideoBlockType)).ToList(),
            tags, props);
    }

    private static IQueryable<Video> IncludeForVideoDto(IQueryable<Video> q) =>
        q.Include(v => v.VideoTags).ThenInclude(vt => vt.Tag).ThenInclude(t => t!.TagGroup)
         .Include(v => v.PropertyValues).ThenInclude(pv => pv.PropertyDefinition);

    // --- Helpers (file paths, mark/move, dirs) ------------------------------

    private static string FormatHhMmSs(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var total = (int)Math.Floor(seconds);
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    private static int CountVideoFilesRecursive(string path)
    {
        if (!TryDirectoryExists(path)) return 0;
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (PathFilters.IsInExcludedFolder(file, path)) continue;
                if (VideoFileExtensions.IsVideo(file)) count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    // Moves the file at video.FilePath into <setRoot>/<specialFolderName>/<rel>,
    // updates the row, and runs setFlag. Used by mark-for-deletion / mark-wont-play.
    // Skips file move on clip rows (they share the parent's file).
    private static async Task<IResult> MarkAndMoveAsync(
        Guid id,
        string specialFolderName,
        Action<Video> setFlag,
        VideoOrganizerDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (video is null) return Results.NotFound();

        if (video.ParentVideoId.HasValue)
        {
            setFlag(video);
            await db.SaveChangesAsync(ct);
            return Results.Ok(video);
        }

        var sets = await db.VideoSets.Where(s => s.Enabled).ToListAsync(ct);
        var set = sets.FirstOrDefault(s =>
            video.FilePath.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase));
        if (set is null)
        {
            return Results.BadRequest(new
            {
                error = "This video's path is not under any enabled VideoSet."
            });
        }

        var setRoot = set.Path.TrimEnd('/', '\\');
        var relative = Path.GetRelativePath(setRoot, video.FilePath);
        var sep = Path.DirectorySeparatorChar;
        var altSep = Path.AltDirectorySeparatorChar;
        var normalizedRel = relative.Replace(altSep, sep);
        if (normalizedRel.StartsWith(specialFolderName + sep, StringComparison.OrdinalIgnoreCase))
        {
            setFlag(video);
            await db.SaveChangesAsync(ct);
            return Results.Ok(video);
        }

        var targetPath = Path.Combine(setRoot, specialFolderName, relative);
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

        if (File.Exists(video.FilePath))
        {
            if (File.Exists(targetPath))
            {
                var ext = Path.GetExtension(targetPath);
                var stem = Path.ChangeExtension(targetPath, null);
                targetPath = $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
            }

            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(video.FilePath, targetPath);
                    break;
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    logger.LogDebug("Move attempt {Attempt} for {Src} failed: {Msg}; retrying",
                        attempt, video.FilePath, ex.Message);
                    try { await Task.Delay(250 * attempt, ct); }
                    catch (TaskCanceledException) { return Results.StatusCode(499); }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to move {Src} to {Dst}", video.FilePath, targetPath);
                    return Results.Problem(detail: $"Could not move file: {ex.Message}", statusCode: 500);
                }
            }
            video.FilePath = PathNormalizer.Normalize(targetPath);
        }
        else
        {
            logger.LogWarning("Video {VideoId} file missing on disk at {Path}; setting flag only",
                video.Id, video.FilePath);
        }

        setFlag(video);
        await db.SaveChangesAsync(ct);
        return Results.Ok(video);
    }

    private static async Task<IResult> UnmarkAndRestoreAsync(
        Guid id,
        string specialFolderName,
        Action<Video> clearFlag,
        VideoOrganizerDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (video is null) return Results.NotFound();

        if (video.ParentVideoId.HasValue)
        {
            clearFlag(video);
            await db.SaveChangesAsync(ct);
            return Results.Ok(video);
        }

        var sets = await db.VideoSets.Where(s => s.Enabled).ToListAsync(ct);
        var set = sets.FirstOrDefault(s =>
            video.FilePath.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase));
        if (set is null)
        {
            clearFlag(video);
            await db.SaveChangesAsync(ct);
            return Results.Ok(video);
        }

        var setRoot = set.Path.TrimEnd('/', '\\');
        var relative = Path.GetRelativePath(setRoot, video.FilePath);
        var sep = Path.DirectorySeparatorChar;
        var altSep = Path.AltDirectorySeparatorChar;
        var normalizedRel = relative.Replace(altSep, sep);

        var prefixAtStart = specialFolderName + sep;
        if (!normalizedRel.StartsWith(prefixAtStart, StringComparison.OrdinalIgnoreCase))
        {
            clearFlag(video);
            await db.SaveChangesAsync(ct);
            return Results.Ok(video);
        }

        var originalRelative = normalizedRel.Substring(prefixAtStart.Length);
        var originalPath = Path.Combine(setRoot, originalRelative);

        if (File.Exists(video.FilePath))
        {
            if (File.Exists(originalPath))
            {
                var ext = Path.GetExtension(originalPath);
                var stem = Path.ChangeExtension(originalPath, null);
                originalPath = $"{stem}-restored-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
            }
            var targetDir = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(video.FilePath, originalPath);
                    break;
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    logger.LogDebug("Restore attempt {Attempt} for {Src} failed: {Msg}; retrying",
                        attempt, video.FilePath, ex.Message);
                    try { await Task.Delay(250 * attempt, ct); }
                    catch (TaskCanceledException) { return Results.StatusCode(499); }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to restore {Src} to {Dst}", video.FilePath, originalPath);
                    return Results.Problem(detail: $"Could not move file: {ex.Message}", statusCode: 500);
                }
            }
            video.FilePath = PathNormalizer.Normalize(originalPath);
        }
        else
        {
            logger.LogWarning("Video {VideoId} file missing on disk at {Path}; clearing flag only",
                video.Id, video.FilePath);
        }

        clearFlag(video);
        await db.SaveChangesAsync(ct);
        return Results.Ok(video);
    }

    private static bool TryDirectoryExists(string path, ILogger? logger = null)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path); }
        catch (Exception ex)
        {
            // Permission denial, broken symlink, network mount offline — all
            // surface as a silent `false` here, which downstream becomes
            // "PathExists = false" in the UI with no diagnostic. Log so the
            // operator at least has a breadcrumb when a supposedly-valid
            // root suddenly stops listing.
            logger?.LogWarning(ex,
                "Directory.Exists check failed for {Path} — treating as missing. Likely permission, broken symlink, or unreachable mount.",
                path);
            return false;
        }
    }

    private static string? DescribeDirectoryIssue(string path)
    {
        if (Directory.Exists(path)) return null;
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return "This folder is a symlink or junction whose target isn't accessible to the API container. "
                    + "If it points to a separate drive, mount that drive directly in docker-compose.yml.";
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException)
        {
            return "Permission denied reading this folder.";
        }
        catch { }
        return "Directory not found";
    }

    private static string? ValidateVideoSet(VideoSet input, VideoOrganizerDbContext db, Guid? currentId)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(input.Path)) return "Path is required.";

        var nameTaken = db.VideoSets.Any(s => s.Name == input.Name && (currentId == null || s.Id != currentId));
        if (nameTaken) return $"A VideoSet named '{input.Name}' already exists.";

        return null;
    }

    // --- Tag-filter matching ------------------------------------------------

    // Caches tag-id → tag-group-id for the duration of a single filter evaluation
    // so we don't re-load it per-tag for the Missing-from-group check.
    private sealed class TagLookup
    {
        public Dictionary<Guid, Guid> TagIdToGroupId { get; init; } = new();
    }

    private static async Task<TagLookup> LoadTagLookupAsync(VideoOrganizerDbContext db, CancellationToken ct)
    {
        var pairs = await db.Tags.AsNoTracking()
            .Select(t => new { t.Id, t.TagGroupId })
            .ToListAsync(ct);
        return new TagLookup
        {
            TagIdToGroupId = pairs.ToDictionary(p => p.Id, p => p.TagGroupId)
        };
    }

    private static bool MatchesFilter(FilterRef f, Video v, TagLookup _)
    {
        switch (f.Type)
        {
            case FilterRefType.Tag:
                return Guid.TryParse(f.Value, out var tid)
                    && v.VideoTags.Any(vt => vt.TagId == tid);
            case FilterRefType.Folder:
                {
                    var folder = PathNormalizer.Normalize(f.Value).TrimEnd('/');
                    var dir = PathNormalizer.Normalize(
                        Path.GetDirectoryName(v.FilePath) ?? string.Empty
                    ).TrimEnd('/');
                    return string.Equals(folder, dir, StringComparison.OrdinalIgnoreCase);
                }
            case FilterRefType.Missing:
                // Value form: "tagGroup:<guid>" — true if the video has no
                // tags from that group.
                if (f.Value.StartsWith("tagGroup:", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(f.Value.Substring("tagGroup:".Length), out var gid))
                {
                    return !v.VideoTags.Any(vt => vt.Tag != null && vt.Tag.TagGroupId == gid);
                }
                return false;
            case FilterRefType.Status:
                return f.Value switch
                {
                    "needsReview" => v.NeedsReview,
                    "wontPlay" => v.WontPlay,
                    "markedForDeletion" => v.MarkedForDeletion,
                    "favorite" => v.IsFavorite,
                    _ => false
                };
            default:
                return false;
        }
    }

    // --- Endpoint registration ----------------------------------------------

    public static void MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/logs", (LogBuffer buf) => Results.Ok(buf.Snapshot())).WithName("GetLogs");

        // === Thumbnails worker ==============================================

        api.MapGet("/thumbnails/status", async (
            VideoOrganizerDbContext db,
            VideoStorageOptions storage,
            ThumbnailWarmingSignal signal,
            ThumbnailWarmingProgressTracker progress,
            CancellationToken ct) =>
        {
            var cacheDir = !string.IsNullOrWhiteSpace(storage.ThumbnailsDirectory)
                ? storage.ThumbnailsDirectory
                : Path.Combine(Path.GetTempPath(), "video-thumbnails");
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);

            var rows = await db.Videos
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .Select(v => new { v.Id, v.ThumbnailsFailed })
                .ToListAsync(ct);

            var warmed = 0;
            var failed = 0;
            foreach (var r in rows)
            {
                if (r.ThumbnailsFailed) { failed++; continue; }
                var dir = Path.Combine(cacheDir, r.Id.ToString());
                if (File.Exists(Path.Combine(dir, "sprite.jpg"))
                    && File.Exists(Path.Combine(dir, "thumbnails.vtt")))
                {
                    warmed++;
                }
            }

            var (curId, curPath, startedAt, _) = progress.Snapshot();
            return Results.Ok(new
            {
                total = rows.Count,
                warmed,
                failed,
                pending = rows.Count - warmed - failed,
                currentVideoId = curId,
                currentFilePath = curPath,
                startedAt,
                nextScanAt = signal.NextScanAt,
                importDetectedAt = signal.ImportDetectedAt
            });
        }).WithName("GetThumbnailStatus");

        api.MapPost("/thumbnails/scan-now", (ThumbnailWarmingSignal signal) =>
        {
            signal.Signal();
            return Results.NoContent();
        }).WithName("TriggerThumbnailScan");

        api.MapPost("/thumbnails/pause", (WorkerPauseStatus pause) =>
        {
            pause.ThumbnailsPaused = true;
            return Results.NoContent();
        }).WithName("PauseThumbnails");
        api.MapPost("/thumbnails/resume", (WorkerPauseStatus pause, ThumbnailWarmingSignal signal) =>
        {
            pause.ThumbnailsPaused = false;
            signal.Signal();
            return Results.NoContent();
        }).WithName("ResumeThumbnails");

        api.MapPost("/thumbnails/skip", (ThumbnailWarmingProgressTracker progress) =>
        {
            progress.RequestSkip();
            return Results.NoContent();
        }).WithName("SkipCurrentThumbnail");

        api.MapPost("/thumbnails/clear-failed", async (
            VideoOrganizerDbContext db,
            ThumbnailWarmingSignal signal,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // Snapshot the affected ids before clearing so retries that fail
            // again are correlatable to this batch.
            var clearedIds = await db.Videos
                .Where(v => v.ThumbnailsFailed)
                .Select(v => v.Id)
                .ToArrayAsync(ct);
            var cleared = await db.Videos
                .Where(v => v.ThumbnailsFailed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.ThumbnailsFailed, false)
                    .SetProperty(v => v.ThumbnailsFailedError, (string?)null), ct);
            if (cleared > 0)
            {
                logger.LogInformation(
                    "Cleared thumbnail-failed flag on {Count} videos and re-signalled the worker. Affected: {VideoIds}",
                    cleared, clearedIds);
                signal.Signal();
            }
            return Results.Ok(new { cleared });
        }).WithName("ClearFailedThumbnails");

        api.MapGet("/thumbnails/failed", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Videos
                .AsNoTracking()
                .Where(v => v.ThumbnailsFailed)
                .OrderBy(v => v.FileName)
                .Select(v => new WorkerFailedRowDto(
                    v.Id, v.FileName, v.FilePath, v.FileSize, v.ThumbnailsFailedError))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithName("GetFailedThumbnails");

        api.MapGet("/thumbnails/queue", async (
            ThumbnailWarmingProgressTracker progress,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            var (_, curPath, _, _) = progress.Snapshot();
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);
            if (enabledRoots.Count == 0) return Results.Ok(Array.Empty<WorkerQueueRowDto>());
            var rows = await db.Videos
                .AsNoTracking()
                .Where(v => !v.ThumbnailsGenerated
                         && !v.ThumbnailsFailed
                         && enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .OrderBy(v => v.IngestDate)
                .ThenBy(v => v.Id)
                .Select(v => new WorkerQueueRowDto(v.Id, v.FileName, v.FilePath, v.FileSize))
                .ToListAsync(ct);
            if (curPath is not null)
            {
                rows = rows.Where(r => !string.Equals(r.FilePath, curPath, StringComparison.Ordinal)).ToList();
            }
            return Results.Ok(rows);
        }).WithName("GetThumbnailQueue");

        // === Md5 worker =====================================================

        api.MapGet("/md5-backfill/status", async (
            VideoOrganizerDbContext db,
            Md5BackfillProgressTracker progress,
            Md5BackfillSignal signal,
            CancellationToken ct) =>
        {
            var total = await db.Videos.CountAsync(ct);
            var failed = await db.Videos.CountAsync(v => v.Md5Failed, ct);
            var pending = await db.Videos.CountAsync(v => v.Md5 == null && !v.Md5Failed, ct);
            var (fileName, filePath, bytesProcessed, totalBytes, _) = progress.Snapshot();
            return Results.Ok(new
            {
                total,
                hashed = total - pending - failed,
                pending,
                failed,
                currentFileName = fileName,
                currentFilePath = filePath,
                bytesProcessed,
                totalBytes,
                nextScanAt = signal.NextScanAt,
                importDetectedAt = signal.ImportDetectedAt
            });
        }).WithName("GetMd5BackfillStatus");

        api.MapPost("/md5-backfill/scan-now", (Md5BackfillSignal signal) =>
        {
            signal.Signal();
            return Results.NoContent();
        }).WithName("TriggerMd5BackfillScan");

        api.MapPost("/md5-backfill/pause", (WorkerPauseStatus pause) =>
        {
            pause.Md5Paused = true;
            return Results.NoContent();
        }).WithName("PauseMd5Backfill");
        api.MapPost("/md5-backfill/resume", (WorkerPauseStatus pause, Md5BackfillSignal signal) =>
        {
            pause.Md5Paused = false;
            signal.Signal();
            return Results.NoContent();
        }).WithName("ResumeMd5Backfill");

        api.MapPost("/md5-backfill/skip", (Md5BackfillProgressTracker progress) =>
        {
            progress.RequestSkip();
            return Results.NoContent();
        }).WithName("SkipCurrentMd5");

        api.MapPost("/md5-backfill/clear-failed", async (
            VideoOrganizerDbContext db,
            Md5BackfillSignal signal,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var clearedIds = await db.Videos
                .Where(v => v.Md5Failed)
                .Select(v => v.Id)
                .ToArrayAsync(ct);
            var cleared = await db.Videos
                .Where(v => v.Md5Failed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Md5Failed, false)
                    .SetProperty(v => v.Md5FailedError, (string?)null), ct);
            if (cleared > 0)
            {
                logger.LogInformation(
                    "Cleared Md5-failed flag on {Count} videos and re-signalled the worker. Affected: {VideoIds}",
                    cleared, clearedIds);
                signal.Signal();
            }
            return Results.Ok(new { cleared });
        }).WithName("ClearFailedMd5");

        api.MapGet("/md5-backfill/duplicates", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var dupMd5s = await db.Videos
                .AsNoTracking()
                .Where(v => v.Md5 != null)
                .GroupBy(v => v.Md5!)
                .Where(g => g.Count() > 1)
                .Select(g => new { Md5 = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            if (dupMd5s.Count == 0) return Results.Ok(Array.Empty<Md5DuplicateRowDto>());
            var sizeByMd5 = dupMd5s.ToDictionary(x => x.Md5, x => x.Count);
            var hashes = dupMd5s.Select(x => x.Md5).ToList();
            var rows = await db.Videos
                .AsNoTracking()
                .Where(v => v.Md5 != null && hashes.Contains(v.Md5))
                .OrderBy(v => v.Md5).ThenBy(v => v.FileName)
                .Select(v => new { v.Id, v.FileName, v.FilePath, v.FileSize, Md5 = v.Md5! })
                .ToListAsync(ct);
            var result = rows.Select(r => new Md5DuplicateRowDto(
                r.Id, r.FileName, r.FilePath, r.FileSize, r.Md5, sizeByMd5[r.Md5])).ToList();
            return Results.Ok(result);
        }).WithName("GetMd5Duplicates");

        api.MapGet("/md5-backfill/failed", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Videos
                .AsNoTracking()
                .Where(v => v.Md5Failed)
                .OrderBy(v => v.FileName)
                .Select(v => new WorkerFailedRowDto(
                    v.Id, v.FileName, v.FilePath, v.FileSize, v.Md5FailedError))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithName("GetFailedMd5");

        api.MapGet("/md5-backfill/queue", async (
            Md5BackfillProgressTracker progress,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            var (_, filePath, _, _, _) = progress.Snapshot();
            var rows = await db.Videos
                .AsNoTracking()
                .Where(v => v.Md5 == null && !v.Md5Failed)
                .OrderBy(v => v.IngestDate)
                .ThenBy(v => v.Id)
                .Select(v => new WorkerQueueRowDto(v.Id, v.FileName, v.FilePath, v.FileSize))
                .ToListAsync(ct);
            if (filePath is not null)
            {
                rows = rows.Where(r => !string.Equals(r.FilePath, filePath, StringComparison.Ordinal)).ToList();
            }
            return Results.Ok(rows);
        }).WithName("GetMd5BackfillQueue");

        // === Video sets =====================================================

        var videoSets = api.MapGroup("/video-sets").WithTags("VideoSets");

        videoSets.MapGet("/", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var sets = await db.VideoSets
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                .ToListAsync(ct);
            var result = sets.Select(s => new
            {
                s.Id, s.Name, s.Path, s.Enabled, s.SortOrder,
                PathExists = TryDirectoryExists(s.Path)
            });
            return Results.Ok(result);
        }).WithName("ListVideoSets");

        videoSets.MapPost("/", async (
            VideoSet input, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            input.Path = PathNormalizer.Normalize(input.Path ?? string.Empty);
            var error = ValidateVideoSet(input, db, currentId: null);
            if (error is not null) return Results.BadRequest(new { error });

            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            db.VideoSets.Add(input);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created VideoSet {VideoSetId} '{Name}' at {Path} (enabled={Enabled})",
                input.Id, input.Name, input.Path, input.Enabled);
            return Results.Created($"/api/video-sets/{input.Id}", input);
        }).WithName("CreateVideoSet");

        videoSets.MapPut("/{id:guid}", async (
            Guid id, VideoSet input, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var existing = await db.VideoSets.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (existing is null) return Results.NotFound();

            var error = ValidateVideoSet(input, db, currentId: id);
            if (error is not null) return Results.BadRequest(new { error });

            // Capture before-state. Path changes are especially worth flagging
            // because every Video below the old path becomes an orphan; the
            // app uses Path as a prefix lookup, not an FK.
            var oldPath = existing.Path;
            var oldEnabled = existing.Enabled;

            existing.Name = input.Name;
            existing.Path = PathNormalizer.Normalize(input.Path ?? string.Empty);
            existing.Enabled = input.Enabled;
            existing.SortOrder = input.SortOrder;
            await db.SaveChangesAsync(ct);

            if (!string.Equals(oldPath, existing.Path, StringComparison.OrdinalIgnoreCase))
            {
                var orphans = await db.Videos.CountAsync(v => v.FilePath.StartsWith(oldPath), ct);
                logger.LogWarning(
                    "VideoSet {VideoSetId} '{Name}' Path changed {OldPath}→{NewPath} — {OrphanCount} videos still point at the old prefix and won't be browsable until they're moved or re-rooted",
                    existing.Id, existing.Name, oldPath, existing.Path, orphans);
            }
            else if (oldEnabled != existing.Enabled)
            {
                logger.LogInformation(
                    "VideoSet {VideoSetId} '{Name}' enabled={Enabled} (was {OldEnabled})",
                    existing.Id, existing.Name, existing.Enabled, oldEnabled);
            }
            return Results.Ok(existing);
        }).WithName("UpdateVideoSet");

        videoSets.MapGet("/{id:guid}/orphan-count",
            async (Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var set = await db.VideoSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (set is null) return Results.NotFound();
            var count = await db.Videos.CountAsync(v => v.FilePath.StartsWith(set.Path), ct);
            return Results.Ok(new { count });
        }).WithName("GetVideoSetOrphanCount");

        videoSets.MapDelete("/{id:guid}", async (
            Guid id, bool? force, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var set = await db.VideoSets.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (set is null) return Results.NotFound();

            var orphanCount = await db.Videos.CountAsync(v => v.FilePath.StartsWith(set.Path), ct);
            if (orphanCount > 0 && force != true)
            {
                logger.LogWarning(
                    "VideoSet {VideoSetId} ({Name}, {Path}) delete blocked — would orphan {OrphanCount} videos. Caller must retry with ?force=true to override.",
                    set.Id, set.Name, set.Path, orphanCount);
                return Results.Conflict(new { orphanCount, error = "Deleting this set would orphan videos. Pass ?force=true to proceed." });
            }

            logger.LogInformation(
                "VideoSet {VideoSetId} ({Name}, {Path}) deleted{ForcedSuffix} — {OrphanCount} videos now point at a missing root",
                set.Id, set.Name, set.Path,
                orphanCount > 0 ? " (forced)" : string.Empty,
                orphanCount);
            db.VideoSets.Remove(set);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteVideoSet");

        // === Videos =========================================================

        api.MapGet("/videos/count", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var count = await db.Videos
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .CountAsync(ct);
            return Results.Ok(count);
        }).WithName("GetVideoCount");

        // GET /api/videos — simple AND-of-tags filter. Repeatable ?tagId=
        // narrows by every passed tag. For richer filtering, use POST
        // /api/videos/filter.
        api.MapGet("/videos", async (HttpContext http, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);

            IQueryable<Video> query = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));

            var tagIdParams = http.Request.Query["tagId"];
            foreach (var raw in tagIdParams)
            {
                if (Guid.TryParse(raw, out var tid))
                    query = query.Where(v => v.VideoTags.Any(vt => vt.TagId == tid));
            }

            var videos = await query.ToListAsync(ct);
            return Results.Ok(videos.Select(ToDto).ToList());
        }).WithName("GetVideos");

        api.MapPost("/videos/filter", async (
            PlaylistFilterRequest? filter,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);

            var candidates = await IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .ToListAsync(ct);

            var lookup = await LoadTagLookupAsync(db, ct);

            var required = filter?.Required ?? new();
            var optional = filter?.Optional ?? new();
            var excluded = filter?.Excluded ?? new();

            var matched = candidates.Where(v =>
            {
                if (required.Count > 0 && !required.All(t => MatchesFilter(t, v, lookup))) return false;
                if (optional.Count > 0 && !optional.Any(t => MatchesFilter(t, v, lookup))) return false;
                if (excluded.Count > 0 && excluded.Any(t => MatchesFilter(t, v, lookup))) return false;
                return true;
            }).ToList();

            return Results.Ok(matched.Select(ToDto).ToList());
        }).WithName("FilterVideos");

        api.MapGet("/videos/{id:guid}", async (VideoOrganizerDbContext db, Guid id, CancellationToken ct) =>
        {
            var video = await IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            return video is null ? Results.NotFound() : Results.Ok(ToDto(video));
        }).WithName("GetVideoById");

        // Related videos: rank other videos by overlap with the current
        // video's tags. Tags weighted equally regardless of group.
        api.MapGet("/videos/{id:guid}/related", async (
            Guid id,
            int? take,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            var limit = take is > 0 ? Math.Min(take.Value, 60) : 12;

            var current = await db.Videos
                .AsNoTracking()
                .Include(v => v.VideoTags)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (current == null) return Results.NotFound();

            var tagIds = current.VideoTags.Select(vt => vt.TagId).ToHashSet();
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);

            IQueryable<Video> q = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => v.Id != id)
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));

            if (tagIds.Count == 0)
            {
                // No tags to rank by — random sample.
                var allIds = await q.Select(v => v.Id).ToListAsync(ct);
                var rng = new Random();
                var pickedIds = allIds.OrderBy(_ => rng.Next()).Take(limit).ToHashSet();
                if (pickedIds.Count == 0) return Results.Ok(Array.Empty<VideoDto>());
                var fallback = await IncludeForVideoDto(db.Videos)
                    .AsNoTracking()
                    .Where(v => pickedIds.Contains(v.Id))
                    .ToListAsync(ct);
                return Results.Ok(fallback.Select(ToDto).ToList());
            }

            q = q.Where(v => v.VideoTags.Any(vt => tagIds.Contains(vt.TagId)));
            var candidates = await q.ToListAsync(ct);
            var ranked = candidates
                .Select(v => new
                {
                    Video = v,
                    Score = v.VideoTags.Count(vt => tagIds.Contains(vt.TagId))
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Video.IngestDate)
                .Take(limit)
                .Select(x => x.Video)
                .ToList();

            return Results.Ok(ranked.Select(ToDto).ToList());
        }).WithName("GetRelatedVideos");

        api.MapPost("/videos", async (VideoOrganizerDbContext db, Video video, CancellationToken ct) =>
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync(ct);
            return Results.CreatedAtRoute("GetVideoById", new { id = video.Id }, video);
        }).WithName("CreateVideo");

        // PUT /api/videos/{id} — full editable-field update. Tags managed via
        // /videos/{id}/tags, properties via /videos/{id}/properties.
        api.MapPut("/videos/{id:guid}", async (
            Guid id, UpdateVideoRequest input, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await IncludeForVideoDto(db.Videos)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video == null) return Results.NotFound();

            video.FileName = input.FileName;
            video.IngestDate = input.IngestDate;
            video.CameraType = (Domain.Models.CameraTypes)(int)input.CameraType;
            video.VideoQuality = (Domain.Models.VideoQuality)(int)input.VideoQuality;
            video.WatchCount = input.WatchCount;
            video.Notes = input.Notes;
            video.NeedsReview = input.NeedsReview;
            video.IsFavorite = input.IsFavorite;
            video.ClipStartSeconds = input.ClipStartSeconds;
            video.ClipEndSeconds = input.ClipEndSeconds;

            if (input.ChapterMarkers is not null)
            {
                video.ChapterMarkers = input.ChapterMarkers
                    .Select(c => new ChapterMarker { Offset = c.Offset, Comment = c.Comment ?? string.Empty }).ToList();
            }

            if (input.VideoBlocks is not null)
            {
                video.VideoBlocks = input.VideoBlocks
                    .Select(b => new VideoBlock
                    {
                        OffsetInSeconds = b.OffsetInSeconds,
                        LengthInSeconds = b.LengthInSeconds,
                        VideoBlockType = (Domain.Models.VideoBlockTypes)(int)b.VideoBlockType
                    }).ToList();
            }

            if (input.TagIds is not null)
            {
                var err = await ReplaceVideoTagsAsync(db, video, input.TagIds, logger, ct);
                if (err is not null) return Results.BadRequest(new { error = err });
            }

            if (input.Properties is not null)
            {
                await ReplaceVideoPropertiesAsync(db, video, input.Properties, logger, ct);
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("UpdateVideo");

        api.MapDelete("/videos/{id:guid}", async (VideoOrganizerDbContext db, Guid id, CancellationToken ct) =>
        {
            var video = await db.Videos.FindAsync(new object[] { id }, ct);
            if (video is null) return Results.NotFound();
            db.Videos.Remove(video);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteVideo");

        // PUT /api/videos/{id}/tags — replace the tag set for a video.
        // Enforces TagGroup.AllowMultiple = false for single-value groups.
        api.MapPut("/videos/{id:guid}/tags", async (
            Guid id, SetVideoTagsRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.Include(v => v.VideoTags)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();

            var err = await ReplaceVideoTagsAsync(db, video, req.TagIds, logger, ct);
            if (err is not null) return Results.BadRequest(new { error = err });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("SetVideoTags");

        api.MapPut("/videos/{id:guid}/properties", async (
            Guid id, SetPropertyValuesRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.Include(v => v.PropertyValues)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            await ReplaceVideoPropertiesAsync(db, video, req.Values, logger, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("SetVideoProperties");

        api.MapPost("/videos/{id:guid}/mark-for-deletion", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await MarkAndMoveAsync(id, "_ToDelete",
                v => v.MarkedForDeletion = true, db, logger, ct);
        }).WithName("MarkVideoForDeletion");

        api.MapPost("/videos/{id:guid}/unmark-for-deletion", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await UnmarkAndRestoreAsync(id, "_ToDelete",
                v => v.MarkedForDeletion = false, db, logger, ct);
        }).WithName("UnmarkVideoForDeletion");

        api.MapPost("/videos/{id:guid}/mark-wont-play", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await MarkAndMoveAsync(id, "_WontPlay",
                v => v.WontPlay = true, db, logger, ct);
        }).WithName("MarkVideoWontPlay");

        // NeedsReview is structural but has no file-system side effect — just a
        // bool flip. Setting NeedsReview = false ("mark reviewed") is the
        // primary user action; the inverse exists for symmetry.
        api.MapPost("/videos/{id:guid}/mark-reviewed", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v is null) return Results.NotFound();
            v.NeedsReview = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("MarkVideoReviewed");

        api.MapPost("/videos/{id:guid}/unmark-reviewed", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v is null) return Results.NotFound();
            v.NeedsReview = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("UnmarkVideoReviewed");

        api.MapPost("/videos/{id:guid}/unmark-wont-play", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await UnmarkAndRestoreAsync(id, "_WontPlay",
                v => v.WontPlay = false, db, logger, ct);
        }).WithName("UnmarkVideoWontPlay");

        // Favorite is a plain boolean — no file-system side effect, just a
        // user-set flag rendered as ★ throughout the UI.
        api.MapPost("/videos/{id:guid}/mark-favorite", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v is null) return Results.NotFound();
            v.IsFavorite = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("MarkVideoFavorite");

        api.MapPost("/videos/{id:guid}/unmark-favorite", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v is null) return Results.NotFound();
            v.IsFavorite = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("UnmarkVideoFavorite");

        api.MapPost("/videos/{id:guid}/watched", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v == null) return Results.NotFound();
            v.WatchCount += 1;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("MarkVideoWatched");

        // Create a clip Video that points at a time range inside the parent's
        // file. Inherits the parent's tag set; user can edit independently.
        api.MapPost("/videos/{parentId:guid}/clips", async (
            Guid parentId,
            CreateClipRequest req,
            VideoOrganizerDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var parent = await db.Videos
                .Include(v => v.VideoTags)
                .FirstOrDefaultAsync(v => v.Id == parentId, ct);
            if (parent is null) return Results.NotFound();
            if (parent.ParentVideoId.HasValue)
                return Results.BadRequest(new { error = "Cannot create a clip of a clip." });

            var start = Math.Max(0, req.StartSeconds);
            var end = Math.Max(start, req.EndSeconds);
            if (end - start < 0.25)
                return Results.BadRequest(new { error = "Clip length must be at least 0.25 seconds." });

            var parentDurSec = parent.Duration.TotalSeconds;
            if (parentDurSec > 0)
            {
                if (start >= parentDurSec)
                    return Results.BadRequest(new { error = "Clip starts after the source ends." });
                if (end > parentDurSec) end = parentDurSec;
            }

            var name = !string.IsNullOrWhiteSpace(req.Name)
                ? req.Name!.Trim()
                : $"{parent.FileName} [{FormatHhMmSs(start)}-{FormatHhMmSs(end)}]";

            var clip = new Video
            {
                Id = Guid.NewGuid(),
                FileName = name,
                FilePath = parent.FilePath,
                Md5 = parent.Md5,
                FileSize = parent.FileSize,
                Duration = TimeSpan.FromSeconds(end - start),
                Height = parent.Height,
                Width = parent.Width,
                VideoDimensionFormat = parent.VideoDimensionFormat,
                VideoCodec = parent.VideoCodec,
                Bitrate = parent.Bitrate,
                FrameRate = parent.FrameRate,
                PixelFormat = parent.PixelFormat,
                Ratio = parent.Ratio,
                CreationTime = parent.CreationTime,
                VideoStreamCount = parent.VideoStreamCount,
                AudioStreamCount = parent.AudioStreamCount,
                IngestDate = DateTime.UtcNow,
                CameraType = parent.CameraType,
                VideoQuality = parent.VideoQuality,
                Notes = parent.Notes,
                ParentVideoId = parent.Id,
                ClipStartSeconds = start,
                ClipEndSeconds = end
            };

            // Inherit parent tags.
            foreach (var pt in parent.VideoTags)
            {
                clip.VideoTags.Add(new VideoTag { TagId = pt.TagId });
            }

            db.Videos.Add(clip);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Created clip {ClipId} of {ParentId}: {Start}-{End}",
                clip.Id, parent.Id, start, end);

            return Results.Ok(clip.Id);
        }).WithName("CreateClip");

        api.MapGet("/videos/marked-for-deletion", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var videos = await IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => v.MarkedForDeletion)
                .OrderBy(v => v.FilePath)
                .ToListAsync(ct);
            return Results.Ok(videos.Select(ToDto).ToList());
        }).WithName("GetMarkedForDeletionVideos");

        api.MapPost("/videos/{id:guid}/purge", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (!video.MarkedForDeletion)
            {
                logger.LogWarning(
                    "Purge rejected for video {VideoId} ({FileName}) — video is not marked for deletion (state: deleted={IsDeleted}, wontPlay={WontPlay})",
                    video.Id, video.FileName, video.MarkedForDeletion, video.WontPlay);
                return Results.BadRequest(new { error = "Video is not marked for deletion." });
            }

            // Clip rows share the file with the parent — drop the row only.
            if (!video.ParentVideoId.HasValue)
            {
                var filePath = video.FilePath;
                try
                {
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        File.Delete(filePath);
                    else
                        logger.LogWarning("Purge: file not found on disk: {Path}", filePath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Purge: failed to delete file {Path}", filePath);
                    return Results.Problem($"Failed to delete file: {ex.Message}");
                }
            }

            logger.LogInformation(
                "Purged video {VideoId} ({FileName}) — clip={IsClip}",
                video.Id, video.FileName, video.ParentVideoId.HasValue);
            db.Videos.Remove(video);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("PurgeVideo");

        api.MapPost("/videos/purge-all", async (
            VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var videos = await db.Videos.Where(v => v.MarkedForDeletion).ToListAsync(ct);
            logger.LogInformation(
                "Purge-all starting — {TotalCandidates} videos marked for deletion",
                videos.Count);

            var purged = 0;
            var failed = new List<object>();
            // Purge parents first so their cascade removes sibling clips.
            var ordered = videos.OrderBy(v => v.ParentVideoId.HasValue ? 1 : 0).ToList();
            foreach (var video in ordered)
            {
                try
                {
                    if (!video.ParentVideoId.HasValue
                        && !string.IsNullOrEmpty(video.FilePath)
                        && File.Exists(video.FilePath))
                    {
                        File.Delete(video.FilePath);
                    }
                    db.Videos.Remove(video);
                    purged++;
                }
                catch (Exception ex)
                {
                    // Per-failure error log gives an actionable identifier
                    // (id + filename + path) — the aggregated counts at the
                    // end aren't enough to retry individually.
                    logger.LogError(ex,
                        "Purge-all: failed on {VideoId} ({FileName}) at {Path}",
                        video.Id, video.FileName, video.FilePath);
                    failed.Add(new { id = video.Id, fileName = video.FileName, error = ex.Message });
                }
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Purge-all complete — {Purged} purged, {Failed} failed of {Total} candidates",
                purged, failed.Count, videos.Count);
            return Results.Ok(new { purged, failed });
        }).WithName("PurgeAllMarkedForDeletion");

        api.MapGet("/videos/{id:guid}/stream", async (
            VideoOrganizerDbContext dbContext, Guid id, ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await dbContext.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            var path = video.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Results.NotFound();

            var fullPath = Path.GetFullPath(path);
            var enabledRoots = await dbContext.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".ogg" => "video/ogg",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".mkv" => "video/x-matroska",
                _ => "application/octet-stream"
            };
            logger.LogInformation("Serving video: {Path}", fullPath);
            return Results.File(fullPath, contentType, enableRangeProcessing: true);
        }).WithName("StreamVideo");

        api.MapGet("/videos/by-folder", async (
            VideoOrganizerDbContext db, string path, bool? recursive, CancellationToken ct) =>
        {
            var fullPath = Path.GetFullPath(path);
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            var prefix = fullPath.Replace('\\', '/').TrimEnd('/');
            var query = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => v.FilePath.StartsWith(fullPath) || v.FilePath.StartsWith(prefix));

            var list = await query.ToListAsync(ct);

            if (recursive != true)
            {
                list = list.Where(v =>
                {
                    var normalized = v.FilePath.Replace('\\', '/');
                    if (!normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
                    var rest = normalized.Substring(prefix.Length + 1);
                    return !rest.Contains('/');
                }).ToList();
            }

            return Results.Ok(list.Select(ToDto).ToList());
        }).WithName("GetVideosByFolder");

        api.MapGet("/videos/{id:guid}/poster.jpg", async (
            VideoOrganizerDbContext db, Guid id, ILogger<Program> logger, HttpContext http, CancellationToken ct) =>
        {
            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            var path = video.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(path);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            var mtime = File.GetLastWriteTimeUtc(fullPath);
            var keySource = $"poster|{fullPath}|{mtime.Ticks}";
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(keySource)));
            var cacheDir = Path.Combine(Path.GetTempPath(), "vo-posters");
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{hash}.jpg");

            if (!File.Exists(cachePath))
            {
                var totalSeconds = video.Duration.TotalSeconds;
                var midpoint = totalSeconds > 2 ? TimeSpan.FromSeconds(totalSeconds / 2) : TimeSpan.Zero;
                try
                {
                    var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(fullPath, cachePath, midpoint);
                    await conversion.AddParameter("-s 320x180").Start(ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Poster generation failed at midpoint for {Path}, retrying at 0s", fullPath);
                    try
                    {
                        var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(fullPath, cachePath, TimeSpan.Zero);
                        await conversion.AddParameter("-s 320x180").Start(ct);
                    }
                    catch (Exception ex2)
                    {
                        logger.LogError(ex2, "Poster generation failed for {Path}", fullPath);
                        return Results.NotFound();
                    }
                }
            }

            http.Response.Headers.CacheControl = "public, max-age=604800, immutable";
            return Results.File(cachePath, "image/jpeg");
        }).WithName("GetVideoPoster");

        api.MapGet("/videos/{id:guid}/thumbnails.vtt", async (
            VideoOrganizerDbContext dbContext,
            IThumbnailGenerator thumbnailGenerator,
            Guid id, ILogger<Program> logger, HttpContext context, CancellationToken ct) =>
        {
            var video = await dbContext.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            var path = video.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Results.NotFound();

            var fullPath = Path.GetFullPath(path);
            var enabledRoots = await dbContext.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            try
            {
                var (_, vttContent) = await thumbnailGenerator.GenerateThumbnailsAsync(
                    fullPath, id, intervalSeconds: 0, thumbnailWidth: 320, thumbnailHeight: 180);
                context.Response.Headers.CacheControl = "public, max-age=86400";
                context.Response.Headers.ETag = $"\"{id}\"";
                return Results.Content(vttContent, "text/vtt");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error generating thumbnails for video: {VideoId}", id);
                return Results.Problem("Failed to generate thumbnails");
            }
        }).WithName("GetVideoThumbnails");

        api.MapGet("/videos/{id:guid}/sprite.jpg", (
            IThumbnailGenerator thumbnailGenerator,
            Guid id, ILogger<Program> logger, HttpContext context) =>
        {
            var spriteImagePath = thumbnailGenerator.GetSpriteImagePath(id);
            if (string.IsNullOrEmpty(spriteImagePath) || !File.Exists(spriteImagePath))
            {
                logger.LogWarning("Sprite image not found for video: {VideoId}", id);
                return Results.NotFound();
            }
            context.Response.Headers.CacheControl = "public, max-age=86400";
            context.Response.Headers.ETag = $"\"{id}\"";
            return Results.File(spriteImagePath, "image/jpeg", enableRangeProcessing: false);
        }).WithName("GetVideoSpriteImage");

        // === Tag Groups =====================================================

        var tagGroups = api.MapGroup("/tag-groups").WithTags("TagGroups");

        tagGroups.MapGet("/", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var rows = await db.TagGroups.AsNoTracking()
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
                .Select(g => new TagGroupDto(
                    g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes,
                    db.Tags.Count(t => t.TagGroupId == g.Id)))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithName("ListTagGroups");

        tagGroups.MapGet("/{id:guid}", async (Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var g = await db.TagGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound();
            var count = await db.Tags.CountAsync(t => t.TagGroupId == id, ct);
            return Results.Ok(new TagGroupDto(g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes, count));
        }).WithName("GetTagGroup");

        tagGroups.MapPost("/", async (
            CreateTagGroupRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (await db.TagGroups.AnyAsync(g => g.Name == req.Name, ct))
                return Results.Conflict(new { error = "A tag group with that name already exists." });
            var g = new TagGroup
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                AllowMultiple = req.AllowMultiple,
                DisplayAsCheckboxes = req.DisplayAsCheckboxes,
                SortOrder = req.SortOrder,
                Notes = req.Notes
            };
            db.TagGroups.Add(g);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created TagGroup {TagGroupId} '{Name}' (allowMultiple={AllowMultiple}, checkboxes={Checkboxes})",
                g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes);
            return Results.Created($"/api/tag-groups/{g.Id}",
                new TagGroupDto(g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes, 0));
        }).WithName("CreateTagGroup");

        tagGroups.MapPut("/{id:guid}", async (
            Guid id, UpdateTagGroupRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var g = await db.TagGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (await db.TagGroups.AnyAsync(x => x.Name == req.Name && x.Id != id, ct))
                return Results.Conflict(new { error = "A tag group with that name already exists." });

            // Capture meaningful before-state so we can flag policy changes —
            // particularly AllowMultiple flipping true→false, which can leave
            // existing videos with multi-tag assignments that violate the new
            // rule. The DB doesn't repair those automatically.
            var oldName = g.Name;
            var oldAllowMultiple = g.AllowMultiple;

            g.Name = req.Name;
            g.AllowMultiple = req.AllowMultiple;
            g.DisplayAsCheckboxes = req.DisplayAsCheckboxes;
            g.SortOrder = req.SortOrder;
            g.Notes = req.Notes;
            await db.SaveChangesAsync(ct);

            if (oldAllowMultiple && !req.AllowMultiple)
            {
                // Count videos currently violating the new policy so the
                // operator can decide whether to clean them up.
                var orphans = await db.VideoTags
                    .Where(vt => vt.Tag!.TagGroupId == id)
                    .GroupBy(vt => vt.VideoId)
                    .Where(grp => grp.Count() > 1)
                    .CountAsync(ct);
                logger.LogWarning(
                    "TagGroup {TagGroupId} '{Name}' AllowMultiple flipped true→false — {ViolatingVideos} videos now have multi-tag assignments that violate the new single-value rule (existing rows are NOT cleaned up automatically)",
                    g.Id, g.Name, orphans);
            }

            logger.LogInformation(
                "Updated TagGroup {TagGroupId}: '{OldName}'→'{NewName}', allowMultiple={AllowMultiple}, checkboxes={Checkboxes}",
                g.Id, oldName, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes);
            return Results.NoContent();
        }).WithName("UpdateTagGroup");

        tagGroups.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var g = await db.TagGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound();

            // Count what's about to cascade away so the audit log answers
            // "I deleted a TagGroup — what data went with it?".
            var tagCount = await db.Tags.CountAsync(t => t.TagGroupId == id, ct);
            var videoTagCount = await db.VideoTags.CountAsync(vt => vt.Tag!.TagGroupId == id, ct);
            var propDefCount = await db.PropertyDefinitions.CountAsync(p => p.TagGroupId == id, ct);

            db.TagGroups.Remove(g);  // cascades to tags + property defs
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Deleted TagGroup {TagGroupId} '{Name}' — cascaded {TagCount} tags ({VideoTagCount} VideoTag rows) and {PropertyDefinitionCount} property definitions",
                g.Id, g.Name, tagCount, videoTagCount, propDefCount);
            return Results.NoContent();
        }).WithName("DeleteTagGroup");

        // === Tags ===========================================================

        var tagsGroup = api.MapGroup("/tags").WithTags("Tags");

        // GET /api/tags?groupId=&withCounts=&q=
        tagsGroup.MapGet("/", async (
            Guid? groupId,
            bool? withCounts,
            string? q,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            IQueryable<Tag> query = db.Tags.AsNoTracking().Include(t => t.TagGroup);
            if (groupId.HasValue) query = query.Where(t => t.TagGroupId == groupId.Value);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var lower = q.Trim().ToLower();
                query = query.Where(t => t.Name.ToLower().Contains(lower));
            }
            var rows = await query
                .OrderBy(t => t.TagGroup!.SortOrder)
                .ThenBy(t => t.SortOrder)
                .ThenBy(t => t.Name)
                .ToListAsync(ct);

            Dictionary<Guid, int> counts = new();
            if (withCounts == true)
            {
                var tagIds = rows.Select(t => t.Id).ToList();
                counts = await db.VideoTags.AsNoTracking()
                    .Where(vt => tagIds.Contains(vt.TagId))
                    .GroupBy(vt => vt.TagId)
                    .Select(g => new { TagId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.TagId, x => x.Count, ct);
            }

            var dtos = rows.Select(t => new TagDto(
                t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty,
                t.Name, t.Aliases, t.IsFavorite, t.SortOrder, t.Notes,
                counts.TryGetValue(t.Id, out var c) ? c : 0)).ToList();
            return Results.Ok(dtos);
        }).WithName("ListTags");

        tagsGroup.MapGet("/{id:guid}", async (Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var t = await db.Tags.AsNoTracking()
                .Include(x => x.TagGroup)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            var count = await db.VideoTags.CountAsync(vt => vt.TagId == id, ct);
            return Results.Ok(new TagDto(
                t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty,
                t.Name, t.Aliases, t.IsFavorite, t.SortOrder, t.Notes, count));
        }).WithName("GetTag");

        tagsGroup.MapPost("/", async (
            CreateTagRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (!await db.TagGroups.AnyAsync(g => g.Id == req.TagGroupId, ct))
                return Results.BadRequest(new { error = "TagGroup not found." });
            if (await db.Tags.AnyAsync(t => t.TagGroupId == req.TagGroupId && t.Name == req.Name, ct))
                return Results.Conflict(new { error = "A tag with that name already exists in this group." });

            var t = new Tag
            {
                Id = Guid.NewGuid(),
                TagGroupId = req.TagGroupId,
                Name = req.Name,
                Aliases = req.Aliases?.ToList() ?? new(),
                IsFavorite = req.IsFavorite,
                SortOrder = req.SortOrder,
                Notes = req.Notes
            };
            db.Tags.Add(t);
            await db.SaveChangesAsync(ct);

            var grp = await db.TagGroups.AsNoTracking().FirstAsync(g => g.Id == t.TagGroupId, ct);
            logger.LogInformation(
                "Created Tag {TagId} '{Name}' in TagGroup {TagGroupId} '{GroupName}' ({AliasCount} aliases)",
                t.Id, t.Name, t.TagGroupId, grp.Name, t.Aliases.Count);
            return Results.Created($"/api/tags/{t.Id}",
                new TagDto(t.Id, t.TagGroupId, grp.Name, t.Name, t.Aliases, t.IsFavorite, t.SortOrder, t.Notes, 0));
        }).WithName("CreateTag");

        tagsGroup.MapPut("/{id:guid}", async (
            Guid id, UpdateTagRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (await db.Tags.AnyAsync(x => x.TagGroupId == t.TagGroupId && x.Name == req.Name && x.Id != id, ct))
                return Results.Conflict(new { error = "A tag with that name already exists in this group." });

            // Capture before-state so the log can show what actually changed —
            // a tag rename + alias edit affects search/typeahead behavior, so
            // it's worth being able to grep for "when did this tag get
            // renamed".
            var oldName = t.Name;
            var oldAliasCount = t.Aliases.Count;

            t.Name = req.Name;
            t.Aliases = req.Aliases.ToList();
            t.IsFavorite = req.IsFavorite;
            t.SortOrder = req.SortOrder;
            t.Notes = req.Notes;
            await db.SaveChangesAsync(ct);

            if (!string.Equals(oldName, t.Name, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Renamed Tag {TagId} '{OldName}'→'{NewName}' (aliases {OldAliasCount}→{NewAliasCount})",
                    t.Id, oldName, t.Name, oldAliasCount, t.Aliases.Count);
            }
            else if (oldAliasCount != t.Aliases.Count)
            {
                logger.LogInformation(
                    "Updated Tag {TagId} '{Name}' aliases ({OldAliasCount}→{NewAliasCount})",
                    t.Id, t.Name, oldAliasCount, t.Aliases.Count);
            }
            return Results.NoContent();
        }).WithName("UpdateTag");

        tagsGroup.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            // Pre-count cascaded rows so the audit log includes the blast
            // radius, not just an opaque "deleted".
            var videoTagCount = await db.VideoTags.CountAsync(vt => vt.TagId == id, ct);
            var propValueCount = await db.TagPropertyValues.CountAsync(pv => pv.TagId == id, ct);

            db.Tags.Remove(t);  // cascades VideoTag + TagPropertyValue
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Deleted Tag {TagId} '{Name}' (group {TagGroupId}) — cascaded {VideoTagCount} VideoTag rows and {PropertyValueCount} TagPropertyValue rows",
                t.Id, t.Name, t.TagGroupId, videoTagCount, propValueCount);
            return Results.NoContent();
        }).WithName("DeleteTag");

        tagsGroup.MapPost("/merge", async (
            MergeTagsRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (req.SourceIds.Contains(req.TargetId))
            {
                logger.LogWarning(
                    "Tag merge rejected — target {TargetId} is also listed as a source",
                    req.TargetId);
                return Results.BadRequest(new { error = "Target must not be in sources." });
            }
            var target = await db.Tags.FirstOrDefaultAsync(t => t.Id == req.TargetId, ct);
            if (target is null) return Results.NotFound(new { error = "Target tag not found." });
            var sources = await db.Tags.Where(t => req.SourceIds.Contains(t.Id)).ToListAsync(ct);
            if (sources.Any(s => s.TagGroupId != target.TagGroupId))
            {
                logger.LogWarning(
                    "Tag merge rejected — sources {SourceIds} span TagGroups, but target {TargetId} is in TagGroup {TargetGroupId}",
                    sources.Select(s => s.Id).ToArray(), target.Id, target.TagGroupId);
                return Results.BadRequest(new { error = "All merged tags must belong to the same group." });
            }

            // Re-point video_tags from sources to target. VideoTag's PK is
            // (VideoId, TagId), so EF Core won't let us mutate TagId in place
            // — we delete the source rows and insert fresh target rows for
            // any video that didn't already have the target.
            var srcIds = sources.Select(s => s.Id).ToList();
            var affected = await db.VideoTags
                .Where(vt => srcIds.Contains(vt.TagId))
                .ToListAsync(ct);
            var alreadyHasTarget = await db.VideoTags
                .Where(vt => vt.TagId == target.Id)
                .Select(vt => vt.VideoId)
                .ToListAsync(ct);
            var alreadySet = new HashSet<Guid>(alreadyHasTarget);
            var newlyAttached = new HashSet<Guid>();

            foreach (var vt in affected)
            {
                db.VideoTags.Remove(vt);
                if (!alreadySet.Contains(vt.VideoId) && newlyAttached.Add(vt.VideoId))
                {
                    db.VideoTags.Add(new VideoTag { VideoId = vt.VideoId, TagId = target.Id });
                }
            }

            // Fold each source's name and aliases into the target's alias list
            // so the search/typeahead can still find the target by what users
            // typed for the merged-away tags. Case-insensitive dedup, and we
            // never add anything that collides with the target's own name.
            var existing = new HashSet<string>(target.Aliases, StringComparer.OrdinalIgnoreCase);
            foreach (var s in sources)
            {
                if (!string.Equals(s.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                    && existing.Add(s.Name))
                {
                    target.Aliases.Add(s.Name);
                }
                foreach (var a in s.Aliases)
                {
                    if (!string.Equals(a, target.Name, StringComparison.OrdinalIgnoreCase)
                        && existing.Add(a))
                    {
                        target.Aliases.Add(a);
                    }
                }
            }

            db.Tags.RemoveRange(sources);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Merged tags {SourceIds} into {TargetId} ('{TargetName}', group {TargetGroupId}) — re-pointed {RepointedRows} VideoTag rows ({NewlyAttached} new attachments, {SkippedDuplicates} skipped because target was already attached), folded source names/aliases into target",
                sources.Select(s => s.Id).ToArray(),
                target.Id, target.Name, target.TagGroupId,
                affected.Count, newlyAttached.Count, affected.Count - newlyAttached.Count);
            return Results.Ok(new { mergedVideos = affected.Count, removedSources = sources.Count });
        }).WithName("MergeTags");

        // Unified search across all tag groups for the filter chip picker.
        tagsGroup.MapGet("/search", async (
            string q, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<TagSearchHit>());
            var lower = q.Trim().ToLower();
            const int limit = 40;

            var matches = await db.Tags.AsNoTracking()
                .Include(t => t.TagGroup)
                .Where(t => t.Name.ToLower().Contains(lower))
                .OrderBy(t => t.TagGroup!.SortOrder)
                .ThenBy(t => t.Name)
                .Take(limit)
                .ToListAsync(ct);

            var hits = matches.Select(t => new TagSearchHit(
                t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty, t.Name, t.Aliases)).ToList();

            // Fall back to alias matching if name-only didn't fill the limit.
            if (hits.Count < limit)
            {
                var seen = hits.Select(h => h.TagId).ToHashSet();
                var aliasCandidates = await db.Tags.AsNoTracking()
                    .Include(t => t.TagGroup)
                    .Where(t => !seen.Contains(t.Id))
                    .ToListAsync(ct);
                foreach (var t in aliasCandidates)
                {
                    if (hits.Count >= limit) break;
                    if (t.Aliases.Any(a => a.ToLower().Contains(lower)))
                    {
                        hits.Add(new TagSearchHit(
                            t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty, t.Name, t.Aliases));
                    }
                }
            }

            return Results.Ok(hits);
        }).WithName("SearchTags");

        // Replace property values on a tag.
        tagsGroup.MapPut("/{id:guid}/properties", async (
            Guid id, SetPropertyValuesRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var tag = await db.Tags.Include(t => t.PropertyValues)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();
            await ReplaceTagPropertiesAsync(db, tag, req.Values, logger, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("SetTagProperties");

        // === Property definitions ===========================================

        var props = api.MapGroup("/properties").WithTags("Properties");

        props.MapGet("/", async (Guid? tagGroupId, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            IQueryable<PropertyDefinition> q = db.PropertyDefinitions.AsNoTracking();
            if (tagGroupId.HasValue) q = q.Where(p => p.TagGroupId == tagGroupId.Value);
            var rows = await q
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
                .Select(p => new PropertyDefinitionDto(
                    p.Id, p.Name, (PropertyDataTypeDto)p.DataType, (PropertyScopeDto)p.Scope,
                    p.TagGroupId, p.Required, p.SortOrder, p.Notes))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithName("ListProperties");

        props.MapPost("/", async (
            CreatePropertyDefinitionRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (req.Scope == PropertyScopeDto.Tag && !req.TagGroupId.HasValue)
                return Results.BadRequest(new { error = "Tag-scoped properties must specify a TagGroupId." });
            if (req.Scope == PropertyScopeDto.Video && req.TagGroupId.HasValue)
                return Results.BadRequest(new { error = "Video-scoped properties must not specify a TagGroupId." });
            if (req.TagGroupId.HasValue && !await db.TagGroups.AnyAsync(g => g.Id == req.TagGroupId, ct))
                return Results.BadRequest(new { error = "TagGroup not found." });

            var def = new PropertyDefinition
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                DataType = (PropertyDataType)req.DataType,
                Scope = (PropertyScope)req.Scope,
                TagGroupId = req.TagGroupId,
                Required = req.Required,
                SortOrder = req.SortOrder,
                Notes = req.Notes
            };
            db.PropertyDefinitions.Add(def);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created PropertyDefinition {PropertyId} '{Name}' (scope={Scope}, dataType={DataType}, tagGroup={TagGroupId}, required={Required})",
                def.Id, def.Name, def.Scope, def.DataType, def.TagGroupId, def.Required);
            return Results.Created($"/api/properties/{def.Id}",
                new PropertyDefinitionDto(def.Id, def.Name,
                    (PropertyDataTypeDto)def.DataType, (PropertyScopeDto)def.Scope,
                    def.TagGroupId, def.Required, def.SortOrder, def.Notes));
        }).WithName("CreateProperty");

        props.MapPut("/{id:guid}", async (
            Guid id, UpdatePropertyDefinitionRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var def = await db.PropertyDefinitions.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (def is null) return Results.NotFound();
            var oldName = def.Name;
            var oldDataType = def.DataType;
            def.Name = req.Name;
            def.DataType = (PropertyDataType)req.DataType;
            def.Required = req.Required;
            def.SortOrder = req.SortOrder;
            def.Notes = req.Notes;
            await db.SaveChangesAsync(ct);

            // DataType changes are especially worth flagging — existing
            // string values stay in the DB and may no longer parse under
            // the new type. Operator should know.
            if (oldDataType != def.DataType)
            {
                logger.LogWarning(
                    "PropertyDefinition {PropertyId} '{Name}' DataType changed {OldDataType}→{NewDataType} — existing values are NOT re-validated",
                    def.Id, def.Name, oldDataType, def.DataType);
            }
            else if (!string.Equals(oldName, def.Name, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Renamed PropertyDefinition {PropertyId} '{OldName}'→'{NewName}'",
                    def.Id, oldName, def.Name);
            }
            return Results.NoContent();
        }).WithName("UpdateProperty");

        props.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var def = await db.PropertyDefinitions.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (def is null) return Results.NotFound();

            // Count cascaded value rows before removing.
            var videoValueCount = await db.VideoPropertyValues.CountAsync(v => v.PropertyDefinitionId == id, ct);
            var tagValueCount = await db.TagPropertyValues.CountAsync(t => t.PropertyDefinitionId == id, ct);

            db.PropertyDefinitions.Remove(def);  // cascades to value rows
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Deleted PropertyDefinition {PropertyId} '{Name}' (scope={Scope}) — cascaded {VideoValueCount} VideoPropertyValue rows and {TagValueCount} TagPropertyValue rows",
                def.Id, def.Name, def.Scope, videoValueCount, tagValueCount);
            return Results.NoContent();
        }).WithName("DeleteProperty");

        // === Playlists ======================================================

        var playlists = api.MapGroup("/playlists").WithTags("Playlists");

        playlists.MapPost("/random", async (
            PlaylistFilterRequest? filter,
            VideoOrganizerDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var candidates = await db.Videos
                .AsNoTracking()
                .Include(v => v.VideoTags)
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .ToListAsync(ct);
            var lookup = await LoadTagLookupAsync(db, ct);

            var required = filter?.Required ?? new();
            var optional = filter?.Optional ?? new();
            var excluded = filter?.Excluded ?? new();

            var matched = candidates.Where(v =>
            {
                if (required.Count > 0 && !required.All(t => MatchesFilter(t, v, lookup))) return false;
                if (optional.Count > 0 && !optional.Any(t => MatchesFilter(t, v, lookup))) return false;
                if (excluded.Count > 0 && excluded.Any(t => MatchesFilter(t, v, lookup))) return false;
                return true;
            }).Select(v => v.Id).ToList();

            if (matched.Count == 0)
                return Results.BadRequest("No videos found matching the filter criteria");

            var rng = new Random();
            var shuffled = matched.OrderBy(_ => rng.Next()).ToList();
            var playlistId = Guid.NewGuid();
            var playlist = new PlaylistDto(playlistId, shuffled, DateTime.UtcNow);
            _playlists[playlistId] = playlist;
            logger.LogInformation("Created random playlist {PlaylistId} with {Count} videos", playlistId, shuffled.Count);
            return Results.Ok(playlist);
        }).WithName("CreateRandomPlaylist");

        playlists.MapPost("/even", async (
            PlaylistFilterRequest? filter,
            VideoOrganizerDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var candidates = await db.Videos
                .AsNoTracking()
                .Include(v => v.VideoTags)
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .ToListAsync(ct);
            var lookup = await LoadTagLookupAsync(db, ct);

            var required = filter?.Required ?? new();
            var optional = filter?.Optional ?? new();
            var excluded = filter?.Excluded ?? new();

            var matched = candidates.Where(v =>
            {
                if (required.Count > 0 && !required.All(t => MatchesFilter(t, v, lookup))) return false;
                if (optional.Count > 0 && !optional.Any(t => MatchesFilter(t, v, lookup))) return false;
                if (excluded.Count > 0 && excluded.Any(t => MatchesFilter(t, v, lookup))) return false;
                return true;
            }).Select(v => new { v.Id, v.WatchCount }).ToList();

            if (matched.Count == 0)
                return Results.BadRequest("No videos found matching the filter criteria");

            var rng = new Random();
            var ordered = matched
                .OrderBy(x => x.WatchCount)
                .ThenBy(_ => rng.Next())
                .Select(x => x.Id)
                .ToList();
            var playlistId = Guid.NewGuid();
            var playlist = new PlaylistDto(playlistId, ordered, DateTime.UtcNow);
            _playlists[playlistId] = playlist;
            logger.LogInformation("Created even-distribution playlist {PlaylistId} with {Count} videos",
                playlistId, ordered.Count);
            return Results.Ok(playlist);
        }).WithName("CreateEvenDistributionPlaylist");

        playlists.MapGet("/{id:guid}", (Guid id, ILogger<Program> logger) =>
        {
            if (!_playlists.TryGetValue(id, out var playlist))
            {
                logger.LogWarning("Playlist {PlaylistId} not found", id);
                return Results.NotFound();
            }
            return Results.Ok(playlist);
        }).WithName("GetPlaylist");

        playlists.MapGet("/{playlistId:guid}/navigation/{videoId:guid}",
            (Guid playlistId, Guid videoId, ILogger<Program> logger) =>
        {
            if (!_playlists.TryGetValue(playlistId, out var playlist))
                return Results.NotFound("Playlist not found");

            var currentIndex = playlist.VideoIds.IndexOf(videoId);
            if (currentIndex == -1) return Results.NotFound("Video not found in playlist");

            var previousVideoId = currentIndex > 0 ? playlist.VideoIds[currentIndex - 1] : (Guid?)null;
            var nextVideoId = currentIndex < playlist.VideoIds.Count - 1
                ? playlist.VideoIds[currentIndex + 1] : (Guid?)null;
            return Results.Ok(new PlaylistNavigationDto(
                videoId, nextVideoId, previousVideoId, currentIndex, playlist.VideoIds.Count));
        }).WithName("GetPlaylistNavigation");

        // === Import =========================================================

        var import = api.MapGroup("/import").WithTags("Import");

        import.MapPost("/directory", (
            DirectoryImportRequest request,
            ImportProgressTracker progressTracker,
            ImportQueueService importQueue) =>
        {
            var jobId = progressTracker.StartJob(request.DirectoryPath, request.Name);
            progressTracker.AddMessage(jobId, "Import queued.");
            if (!importQueue.Enqueue(new QueuedImport(jobId, request)))
            {
                progressTracker.MarkFailed(jobId, "Failed to enqueue import (service shutting down).");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Accepted($"/api/import/progress/{jobId}", new { JobId = jobId });
        }).WithName("ImportFromDirectory");

        import.MapGet("/progress/{jobId:guid}", (
            Guid jobId, ImportProgressTracker progressTracker) =>
        {
            var (messages, isCompleted, error, fileStatuses) = progressTracker.GetStatus(jobId);
            return Results.Ok(new ImportProgressResponse(messages, isCompleted, error, fileStatuses));
        }).WithName("GetImportProgress");

        import.MapPost("/pause", (WorkerPauseStatus pause) =>
        {
            pause.ImportPaused = true;
            return Results.NoContent();
        }).WithName("PauseImports");
        import.MapPost("/resume", (WorkerPauseStatus pause) =>
        {
            pause.ImportPaused = false;
            return Results.NoContent();
        }).WithName("ResumeImports");

        api.MapGet("/worker-pause-status", (WorkerPauseStatus pause) =>
        {
            return Results.Ok(new
            {
                importPaused = pause.ImportPaused,
                thumbnailsPaused = pause.ThumbnailsPaused,
                md5Paused = pause.Md5Paused,
            });
        }).WithName("GetWorkerPauseStatus");

        import.MapGet("/jobs", async (
            ImportProgressTracker progressTracker,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            var snapshots = progressTracker.GetAllJobSnapshots();
            if (snapshots.Count == 0) return Results.Ok(Array.Empty<ImportJobSummaryDto>());

            var jobIds = snapshots.Select(s => s.JobId).ToHashSet();
            var rows = await db.Videos
                .AsNoTracking()
                .Where(v => v.ImportJobId != null && jobIds.Contains(v.ImportJobId.Value))
                .Select(v => new
                {
                    JobId = v.ImportJobId!.Value,
                    v.ThumbnailsGenerated,
                    v.ThumbnailsFailed,
                    HasMd5 = v.Md5 != null,
                    v.Md5Failed,
                })
                .ToListAsync(ct);
            var byJob = rows.GroupBy(r => r.JobId).ToDictionary(g => g.Key, g => g.ToList());

            var result = snapshots.Select(s =>
            {
                byJob.TryGetValue(s.JobId, out var videos);
                videos ??= new();
                int total = videos.Count;
                int thumbDone = videos.Count(v => v.ThumbnailsGenerated);
                int thumbFailed = videos.Count(v => v.ThumbnailsFailed);
                int thumbPending = total - thumbDone - thumbFailed;
                int md5Done = videos.Count(v => v.HasMd5);
                int md5Failed = videos.Count(v => v.Md5Failed);
                int md5Pending = total - md5Done - md5Failed;

                bool importPhaseDone = s.IsCompleted;
                bool thumbsTaskDone = total == 0 || (thumbDone + thumbFailed >= total);
                bool md5TaskDone = total == 0 || (md5Done + md5Failed >= total);
                bool isFullyDone = importPhaseDone && (s.Error != null || (thumbsTaskDone && md5TaskDone));
                DateTime? completedAt = isFullyDone ? s.CompletedAt : null;

                return new ImportJobSummaryDto(
                    s.JobId, s.Name, s.DirectoryPath, s.EnqueuedAt, s.StartedAt, completedAt,
                    isFullyDone, s.Error, s.TotalFiles, s.CompletedCount, s.FailedCount,
                    s.SkippedCount, s.ImportingCount, s.CurrentFilePath,
                    new ImportTaskProgressDto(total, thumbDone, Math.Max(0, thumbPending), thumbFailed),
                    new ImportTaskProgressDto(total, md5Done, Math.Max(0, md5Pending), md5Failed));
            }).ToList();
            return Results.Ok(result);
        }).WithName("ListImportJobs");

        import.MapDelete("/jobs/completed", (ImportProgressTracker progressTracker) =>
        {
            var removed = progressTracker.ClearCompleted();
            return Results.Ok(new { removed });
        }).WithName("ClearCompletedImportJobs");

        import.MapGet("/failed-files", (ImportProgressTracker progressTracker) =>
            Results.Ok(progressTracker.GetFailedFiles())
        ).WithName("ListFailedImportFiles");

        import.MapGet("/queue", (ImportProgressTracker progressTracker) =>
            Results.Ok(progressTracker.GetQueueFiles())
        ).WithName("ListImportQueue");

        import.MapGet("/browse", async (
            VideoOrganizerDbContext db, string? path, ILogger<Program> logger, CancellationToken ct) =>
        {
            try
            {
                var sets = await db.VideoSets.Where(s => s.Enabled)
                    .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                    .ToListAsync(ct);

                async Task<List<ImportBrowseDirectory>> AnnotateAsync(
                    IEnumerable<(string name, string fullPath, bool hasSubs)> dirs, string dbPrefix)
                {
                    var dirList = dirs.ToList();
                    var normalizedPrefix = PathNormalizer.Normalize(dbPrefix);
                    var importedPaths = await db.Videos
                        .Where(v => v.FilePath.StartsWith(normalizedPrefix))
                        .Select(v => v.FilePath)
                        .ToListAsync(ct);

                    return dirList.Select(d =>
                    {
                        var normalizedDir = PathNormalizer.Normalize(d.fullPath);
                        var importedCount = importedPaths.Count(p =>
                            p.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase));
                        return new ImportBrowseDirectory(
                            Name: d.name, FullPath: d.fullPath, HasSubdirectories: d.hasSubs,
                            VideoCount: CountVideoFilesRecursive(d.fullPath),
                            ImportedCount: importedCount);
                    }).ToList();
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    var annotated = new List<ImportBrowseDirectory>();
                    foreach (var s in sets)
                    {
                        var full = Path.GetFullPath(s.Path);
                        var hasSubs = TryDirectoryExists(s.Path) && Directory.GetDirectories(s.Path).Length > 0;
                        var normalizedSet = PathNormalizer.Normalize(s.Path);
                        var importedCount = await db.Videos
                            .CountAsync(v => v.FilePath.StartsWith(normalizedSet), ct);
                        annotated.Add(new ImportBrowseDirectory(
                            Name: s.Name, FullPath: full, HasSubdirectories: hasSubs,
                            VideoCount: CountVideoFilesRecursive(full),
                            ImportedCount: importedCount));
                    }
                    return Results.Ok(new ImportBrowseResponse(string.Empty, null, annotated));
                }

                var fullPath = Path.GetFullPath(path);
                var containingSet = sets.FirstOrDefault(s =>
                    fullPath.StartsWith(Path.GetFullPath(s.Path), StringComparison.Ordinal));
                if (containingSet is null) return Results.Forbid();

                var issue = DescribeDirectoryIssue(fullPath);
                if (issue is not null) return Results.NotFound(issue);

                var rawDirs = Directory.GetDirectories(fullPath)
                    .Where(d => !PathFilters.IsExcludedFolderName(Path.GetFileName(d)))
                    .OrderBy(d => Path.GetFileName(d))
                    .Select(d => (
                        name: Path.GetFileName(d),
                        fullPath: d,
                        hasSubs: Directory.GetDirectories(d).Length > 0
                    ));

                var directories = await AnnotateAsync(rawDirs, fullPath);

                var setRoot = Path.GetFullPath(containingSet.Path);
                string? parent;
                if (string.Equals(fullPath, setRoot, StringComparison.Ordinal))
                {
                    parent = string.Empty;
                }
                else
                {
                    var fsParent = Directory.GetParent(fullPath)?.FullName;
                    parent = fsParent != null && fsParent.StartsWith(setRoot, StringComparison.Ordinal)
                        ? fsParent : setRoot;
                }
                return Results.Ok(new ImportBrowseResponse(fullPath, parent, directories));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error browsing directory: {Path}", path);
                return Results.Problem("Failed to browse directory");
            }
        }).WithName("BrowseDirectory");

        import.MapGet("/files", async (
            VideoOrganizerDbContext db, string directoryPath, ILogger<Program> logger,
            CancellationToken ct, bool includeSubdirectories = true) =>
        {
            try
            {
                var targetPath = Path.GetFullPath(directoryPath);
                var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
                if (!enabledRoots.Any(r => targetPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                    return Results.Forbid();

                var fileIssue = DescribeDirectoryIssue(targetPath);
                if (fileIssue is not null) return Results.NotFound(fileIssue);

                var searchOption = includeSubdirectories
                    ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var allFiles = Directory.EnumerateFiles(targetPath, "*.*", searchOption)
                    .Where(f => !PathFilters.IsInExcludedFolder(f, targetPath))
                    .OrderBy(f => f)
                    .ToList();

                var importable = new List<string>();
                var nonImportable = new List<string>();
                foreach (var file in allFiles)
                {
                    if (VideoFileExtensions.IsVideo(file)) importable.Add(file);
                    else nonImportable.Add(file);
                }

                var importableByNormalized = importable
                    .GroupBy(p => PathNormalizer.Normalize(p), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var normalizedTarget = PathNormalizer.Normalize(targetPath);
                var dbMatches = await db.Videos
                    .Where(v => v.FilePath.StartsWith(normalizedTarget))
                    .Select(v => v.FilePath)
                    .ToListAsync(ct);
                var importedFiles = dbMatches
                    .Where(p => importableByNormalized.ContainsKey(p))
                    .Select(p => importableByNormalized[p])
                    .ToList();

                return Results.Ok(new ImportFileListResponse(targetPath, importable, nonImportable, importedFiles));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error listing files for directory {Path}", directoryPath);
                return Results.Problem("Failed to list files");
            }
        }).WithName("ListImportFiles");

        import.MapGet("/thumbnail", async (
            VideoOrganizerDbContext db, string path, ILogger<Program> logger, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) return Results.NotFound();

                var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
                if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                    return Results.Forbid();
                if (!VideoFileExtensions.IsVideo(fullPath)) return Results.BadRequest();

                var mtime = File.GetLastWriteTimeUtc(fullPath);
                var keySource = $"{fullPath}|{mtime.Ticks}";
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(keySource)));
                var cacheDir = Path.Combine(Path.GetTempPath(), "vo-import-thumbs");
                Directory.CreateDirectory(cacheDir);
                var cachePath = Path.Combine(cacheDir, $"{hash}.jpg");

                if (!File.Exists(cachePath))
                {
                    var snapshotTime = TimeSpan.FromSeconds(5);
                    try
                    {
                        var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(
                            fullPath, cachePath, snapshotTime);
                        await conversion.AddParameter("-s 240x135").Start(ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Thumbnail generation failed at 5s for {Path}, retrying at 0s", fullPath);
                        try
                        {
                            var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(
                                fullPath, cachePath, TimeSpan.Zero);
                            await conversion.AddParameter("-s 240x135").Start(ct);
                        }
                        catch (Exception ex2)
                        {
                            logger.LogError(ex2, "Thumbnail generation failed for {Path}", fullPath);
                            return Results.NotFound();
                        }
                    }
                }
                http.Response.Headers.CacheControl = "public, max-age=604800, immutable";
                return Results.File(cachePath, "image/jpeg");
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error serving thumbnail for {Path}", path);
                return Results.Problem("Failed to generate thumbnail");
            }
        }).WithName("GetImportThumbnail");
    }

    // --- Tag-set / property-set replace helpers -----------------------------

    private static async Task<string?> ReplaceVideoTagsAsync(
        VideoOrganizerDbContext db, Video video, IReadOnlyList<Guid> tagIds,
        ILogger logger, CancellationToken ct)
    {
        var distinctIds = tagIds.Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            video.VideoTags.Clear();
            return null;
        }

        var tags = await db.Tags
            .Where(t => distinctIds.Contains(t.Id))
            .Select(t => new { t.Id, t.TagGroupId })
            .ToListAsync(ct);
        if (tags.Count != distinctIds.Count)
        {
            // Log the specific missing IDs so a 400 to the client has a paper
            // trail. Helps distinguish "client sent wrong ID" from "tag was
            // deleted between the page load and this request".
            var missing = distinctIds.Except(tags.Select(t => t.Id)).ToArray();
            logger.LogWarning(
                "Video {VideoId} tag-set update rejected — tag IDs not found: {MissingTagIds}",
                video.Id, missing);
            return "One or more tag IDs not found.";
        }

        // Enforce AllowMultiple = false where applicable.
        var groupsTouched = tags.Select(t => t.TagGroupId).Distinct().ToList();
        var singleValueGroups = await db.TagGroups
            .Where(g => groupsTouched.Contains(g.Id) && !g.AllowMultiple)
            .Select(g => g.Id).ToListAsync(ct);
        var perGroup = tags.GroupBy(t => t.TagGroupId);
        foreach (var grp in perGroup)
        {
            if (singleValueGroups.Contains(grp.Key) && grp.Count() > 1)
            {
                logger.LogWarning(
                    "Video {VideoId} tag-set update rejected — TagGroup {TagGroupId} is single-value but request supplied {TagCount} tags",
                    video.Id, grp.Key, grp.Count());
                return $"TagGroup {grp.Key} does not allow multiple tags per video.";
            }
        }

        video.VideoTags.Clear();
        foreach (var tid in distinctIds)
            video.VideoTags.Add(new VideoTag { TagId = tid });
        return null;
    }

    private static async Task ReplaceVideoPropertiesAsync(
        VideoOrganizerDbContext db, Video video,
        IReadOnlyList<PropertyValueWrite> values, ILogger logger, CancellationToken ct)
    {
        var ids = values.Select(v => v.PropertyDefinitionId).Distinct().ToList();
        var defs = await db.PropertyDefinitions
            .Where(p => ids.Contains(p.Id) && p.Scope == PropertyScope.Video)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var validIds = new HashSet<Guid>(defs);

        // Identify dropped IDs once — values may contain duplicates that we
        // don't want to log multiple times.
        var droppedIds = ids.Where(id => !validIds.Contains(id)).ToArray();
        if (droppedIds.Length > 0)
        {
            logger.LogWarning(
                "Video {VideoId} property update silently dropped {DroppedCount} unknown/non-Video-scoped PropertyDefinitionIds: {DroppedIds}",
                video.Id, droppedIds.Length, droppedIds);
        }

        video.PropertyValues.Clear();
        foreach (var w in values)
        {
            if (!validIds.Contains(w.PropertyDefinitionId)) continue;
            video.PropertyValues.Add(new VideoPropertyValue
            {
                PropertyDefinitionId = w.PropertyDefinitionId,
                Value = w.Value ?? string.Empty
            });
        }
    }

    private static async Task ReplaceTagPropertiesAsync(
        VideoOrganizerDbContext db, Tag tag,
        IReadOnlyList<PropertyValueWrite> values, ILogger logger, CancellationToken ct)
    {
        var ids = values.Select(v => v.PropertyDefinitionId).Distinct().ToList();
        var defs = await db.PropertyDefinitions
            .Where(p => ids.Contains(p.Id)
                     && p.Scope == PropertyScope.Tag
                     && p.TagGroupId == tag.TagGroupId)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var validIds = new HashSet<Guid>(defs);

        var droppedIds = ids.Where(id => !validIds.Contains(id)).ToArray();
        if (droppedIds.Length > 0)
        {
            logger.LogWarning(
                "Tag {TagId} property update silently dropped {DroppedCount} unknown/wrong-scope/wrong-group PropertyDefinitionIds: {DroppedIds}",
                tag.Id, droppedIds.Length, droppedIds);
        }

        tag.PropertyValues.Clear();
        foreach (var w in values)
        {
            if (!validIds.Contains(w.PropertyDefinitionId)) continue;
            tag.PropertyValues.Add(new TagPropertyValue
            {
                PropertyDefinitionId = w.PropertyDefinitionId,
                Value = w.Value ?? string.Empty
            });
        }
    }
}
