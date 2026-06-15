using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.IO;
using System.Text;
using Xabe.FFmpeg;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Helpers;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

public static partial class ApiEndpoints
{
    private static void MapImportEndpoints(RouteGroupBuilder api)
    {
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
            VideoOrganizerDbContext db, string? path, ImportScanProgress progress,
            DirectoryScanCache scanCache, bool? refresh,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            // The recursive video-file count below feeds a shared progress
            // counter so the Import page can poll GET /import/scan-progress
            // for a live "discovered N" total while a source loads. (issue #27)
            progress.Begin();
            // ?refresh=true (the Sources refresh button) drops the scan cache
            // so every folder is re-walked fresh — picks up changes made
            // outside the app. Normal loads reuse cached counts. (issue #4)
            if (refresh == true) scanCache.Clear();

            // Recursive on-disk video count, memoized per folder. The walk
            // (CountVideoFilesRecursive) is the slow part of annotating the
            // tree; a hit skips it but still credits the discovered total so
            // the live count stays meaningful.
            int CachedVideoCount(string p)
            {
                var key = PathNormalizer.Normalize(p);
                if (scanCache.TryGet(key, out var hit))
                {
                    progress.Add(hit);
                    return hit;
                }
                var n = CountVideoFilesRecursive(p, progress);
                scanCache.Set(key, n);
                return n;
            }

            try
            {
                // Include disabled sources too so the browse-page
                // Sources tree can still surface them (rendered with
                // a strikethrough + "(Disabled)" suffix on the
                // client). Disabling a source hides its videos from
                // the playback grid but shouldn't cut off filesystem
                // visibility — users may want to see what's there
                // before re-enabling.
                var sets = await db.VideoSets
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
                            VideoCount: CachedVideoCount(d.fullPath),
                            ImportedCount: importedCount);
                    }).ToList();
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    // Per-source try/catch — a single source with an
                    // invalid path, permission-denied folder, or
                    // unreachable network mount used to take down the
                    // whole endpoint with a 500. Now we log and skip
                    // so the user can still see / add other sources.
                    var annotated = new List<ImportBrowseDirectory>();
                    foreach (var s in sets)
                    {
                        try
                        {
                            var full = Path.GetFullPath(s.Path);
                            var hasSubs = TryDirectoryExists(s.Path, logger)
                                && SafeGetDirectoryCount(s.Path, logger) > 0;
                            var normalizedSet = PathNormalizer.Normalize(s.Path);
                            var importedCount = await db.Videos
                                .CountAsync(v => v.FilePath.StartsWith(normalizedSet), ct);
                            annotated.Add(new ImportBrowseDirectory(
                                Name: s.Name, FullPath: full, HasSubdirectories: hasSubs,
                                VideoCount: CachedVideoCount(full),
                                ImportedCount: importedCount));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "Skipping VideoSet {VideoSetId} '{Name}' ({Path}) in /import/browse — listing failed",
                                s.Id, s.Name, s.Path);
                        }
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
            finally
            {
                progress.End();
            }
        }).WithName("BrowseDirectory");

        // GET /api/import/imported-folders — flat, filterable destination list
        // for the move dialog: every distinct folder that already holds an
        // imported video, under an enabled source. A pure DB read (no
        // filesystem walk), so it's fast regardless of library size. (issue #4)
        import.MapGet("/imported-folders", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var sets = await db.VideoSets.Where(s => s.Enabled).ToListAsync(ct);
            if (sets.Count == 0) return Results.Ok(new List<ImportedFolder>());

            // Pull just the path column; the parent-folder split + dedupe is
            // cheap in memory and avoids provider-specific SQL string funcs.
            // Clips share their parent's file, so skip them.
            var paths = await db.Videos
                .Where(v => v.ParentVideoId == null)
                .Select(v => v.FilePath)
                .ToListAsync(ct);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                var norm = PathNormalizer.Normalize(p);
                var idx = norm.LastIndexOf('/');
                if (idx <= 0) continue;
                var folder = norm[..idx];
                counts[folder] = counts.TryGetValue(folder, out var c) ? c + 1 : 1;
            }

            var roots = sets
                .Select(s => (Set: s, Root: PathNormalizer.Normalize(s.Path).TrimEnd('/')))
                .ToList();

            var result = new List<ImportedFolder>();
            foreach (var (folder, count) in counts)
            {
                var match = roots.FirstOrDefault(r =>
                    folder.Equals(r.Root, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(r.Root + "/", StringComparison.OrdinalIgnoreCase));
                if (match.Set is null) continue; // under a disabled/removed source

                var rel = folder.Length > match.Root.Length
                    ? folder[(match.Root.Length + 1)..]
                    : string.Empty;
                var label = rel.Length == 0 ? match.Set.Name : $"{match.Set.Name}/{rel}";
                result.Add(new ImportedFolder(folder, label, count));
            }

            result.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(result);
        }).WithName("ListImportedFolders");

        // Live progress for an in-flight /import/browse scan. The Import
        // page polls this (~500ms) to show a climbing "Discovered N video
        // files…" count while a source loads, instead of a blind spinner.
        // Scanning=false once the walk(s) finish; Discovered holds the
        // final total until the next scan starts. (issue #27)
        import.MapGet("/scan-progress", (ImportScanProgress progress) =>
        {
            var (scanning, discovered) = progress.Snapshot();
            return Results.Ok(new ImportScanProgressDto(scanning, discovered));
        }).WithName("GetImportScanProgress");

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
                var hidden = new List<string>();
                foreach (var file in allFiles)
                {
                    // Hidden files (dot-prefixed) are surfaced on their own tab
                    // and never counted as importable or "other". (issue #62)
                    if (PathFilters.IsHiddenFile(file)) hidden.Add(file);
                    else if (VideoFileExtensions.IsVideo(file)) importable.Add(file);
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

                return Results.Ok(new ImportFileListResponse(targetPath, importable, nonImportable, importedFiles, hidden));
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
}
