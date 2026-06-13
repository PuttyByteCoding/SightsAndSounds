using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Helpers;
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
            v.NeedsReview, v.PlaybackIssue, v.MarkedForDeletion,
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

    // True when the inbound HTTP request came from the loopback adapter
    // (127.0.0.1, ::1) — i.e. the browser is running on the same box as
    // the API. Endpoints that fork off external processes (reveal in
    // file manager, ffprobe diagnostics, …) gate on this so a remotely
    // exposed port can't trigger arbitrary local launches; the frontend
    // also reads this via /api/runtime-info to decide whether to show
    // a "must be on the host machine" banner and whether to render the
    // local-only buttons at all.
    private static bool IsLocalRequest(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        return ip != null && IPAddress.IsLoopback(ip);
    }

    // EscapeLikePattern lives in VideoOrganizer.API.Helpers.SqlHelpers
    // now so it can be unit-tested. The endpoints below call it via
    // its fully-qualified name (or `using static`) instead.

    // (command, args[]) pair describing one terminal-launch attempt.
    // Consumed by /api/videos/{id}/open-terminal.
    private readonly record struct TerminalAttempt(string Command, string[] Arguments);

    // Ordered list of terminal-launch attempts for the current OS.
    // The endpoint walks this list, runs Process.Start on each one, and
    // takes the first that doesn't throw "binary not found". Each entry
    // is responsible for opening a new interactive shell whose CWD is
    // `dir` — using the terminal's native "start here" flag where one
    // exists, or shell tricks (xterm) where one doesn't.
    //
    // Linux ordering rationale:
    //   1. `x-terminal-emulator` — Debian alternatives system. Whatever
    //      the user picked as their default; if it's set, respect it.
    //   2. DE-bundled emulators (gnome-terminal, konsole, …) — present
    //      on the vast majority of desktop installs.
    //   3. Keyboard-driven favorites (kitty, alacritty, wezterm) — power
    //      users tend to remove the DE one in favor of these.
    //   4. xterm — universal-ish fallback. Ugly but reliable.
    private static IEnumerable<TerminalAttempt> TerminalLaunchAttempts(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return new("wt.exe", new[] { "-d", dir });
            // PowerShell -NoExit keeps the window open; -WorkingDirectory
            // is honored by both Windows PowerShell and PowerShell 7+.
            yield return new("pwsh.exe", new[] { "-NoExit", "-WorkingDirectory", dir });
            yield return new("powershell.exe", new[] { "-NoExit", "-WorkingDirectory", dir });
            yield return new("cmd.exe", new[] { "/K", "cd", "/D", dir });
            yield break;
        }
        if (OperatingSystem.IsMacOS())
        {
            yield return new("open", new[] { "-a", "Terminal", dir });
            yield break;
        }
        // Linux + other Unixes
        yield return new("x-terminal-emulator", new[] { "--working-directory", dir });
        yield return new("gnome-terminal", new[] { $"--working-directory={dir}" });
        yield return new("konsole", new[] { "--workdir", dir });
        yield return new("xfce4-terminal", new[] { $"--working-directory={dir}" });
        yield return new("mate-terminal", new[] { $"--working-directory={dir}" });
        yield return new("tilix", new[] { $"--working-directory={dir}" });
        yield return new("terminator", new[] { "--working-directory", dir });
        yield return new("kitty", new[] { "--directory", dir });
        yield return new("alacritty", new[] { "--working-directory", dir });
        yield return new("wezterm", new[] { "start", "--cwd", dir });
        // xterm has no --working-directory flag. It DOES inherit CWD
        // from its parent process, and the endpoint already sets
        // ProcessStartInfo.WorkingDirectory, so a bare xterm invocation
        // opens in `dir` automatically.
        yield return new("xterm", Array.Empty<string>());
    }

    // Reload a Video from the DB with all DTO-shaped navigation
    // properties, then project to VideoDto. Used by the mark/unmark
    // endpoints (mark-for-deletion, mark-playback-issue, …) so they can
    // return the same wire shape as /videos/{id} — the frontend's
    // `Video` type expects tags / properties / chapterMarkers /
    // videoBlocks to all be present, and crashes (e.g.
    // `video.tags.map(...)` on undefined) when they're missing.
    // AsNoTracking sidesteps any leftover state on the in-context
    // tracked entity that just had its FilePath / flag mutated.
    private static async Task<IResult> ReturnFreshDtoAsync(
        VideoOrganizerDbContext db, Guid id, CancellationToken ct)
    {
        var loaded = await IncludeForVideoDto(db.Videos.AsNoTracking())
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        return loaded is null ? Results.NotFound() : Results.Ok(ToDto(loaded));
    }

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

    // Compute an MD5 hex digest by streaming the file in 256KB chunks
    // — keeps a multi-GB video out of the heap. Mirrors the worker's
    // ComputeMd5Async (Md5BackfillService) but without the progress-
    // tracker / skip-request plumbing the worker needs; this one is
    // called from the validation endpoint where progress is tracked
    // client-side and cancellation comes from the request CT.
    private static async Task<string> ComputeFileMd5Async(string path, CancellationToken ct)
    {
        using var md5Alg = System.Security.Cryptography.MD5.Create();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1024 * 256,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[1024 * 256];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            md5Alg.TransformBlock(buffer, 0, read, null, 0);
        }
        md5Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(md5Alg.Hash ?? Array.Empty<byte>())
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    // Recursive count of video files under `path`. When `progress` is
    // supplied, each video found also bumps the shared scan counter so the
    // Import page can poll a live "discovered" total while this walk runs
    // (issue #27). EnumerateFiles is lazy, so the counter climbs as the
    // filesystem yields entries.
    private static int CountVideoFilesRecursive(string path, ImportScanProgress? progress = null)
    {
        if (!TryDirectoryExists(path)) return 0;
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (PathFilters.IsInExcludedFolder(file, path)) continue;
                if (VideoFileExtensions.IsVideo(file))
                {
                    count++;
                    progress?.Increment();
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    // Evicts only the directories whose recursive video count changed when a
    // file moved between folders (issue #4). A directory's count changes iff
    // the file entered or left its subtree — i.e. it's an ancestor of exactly
    // one of {fromDir, toDir}. Directories that are ancestors of both (the
    // source root for a within-source move) net zero and stay cached, so a
    // move no longer forces a full re-walk on the next browse.
    private static void EvictMovedDirs(DirectoryScanCache cache, string? fromDir, string? toDir)
    {
        var changed = AncestorKeys(fromDir);
        changed.SymmetricExceptWith(AncestorKeys(toDir));
        foreach (var key in changed) cache.Remove(key);
    }

    // Normalized cache keys for `dir` and every ancestor up to the filesystem
    // root. Case-insensitive to match the cache. Path.GetDirectoryName flips
    // to the platform separator, so each level is re-normalized to the
    // forward-slash form the cache stores.
    private static HashSet<string> AncestorKeys(string? dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var d = dir;
        while (!string.IsNullOrEmpty(d))
        {
            set.Add(PathNormalizer.Normalize(d));
            var parent = Path.GetDirectoryName(d);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, d, StringComparison.Ordinal)) break;
            d = parent;
        }
        return set;
    }

    // Normalizes free text into a space-delimited lowercase token string for
    // tag-suggestion matching (issue #10). Every non-alphanumeric character
    // becomes a separator so "Bob.Marley_live-2009" and "bob marley" line up;
    // runs of separators collapse. Matching against the result with a leading
    // and trailing space gives whole-word/phrase hits without substring noise.
    private static string NormalizeForMatch(string s)
    {
        var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return string.Join(' ', new string(chars.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Moves a file from src to dst, reporting byte progress (issue #4).
    // Same-volume moves are an instant File.Move (a rename); cross-volume
    // moves stream the copy in 1 MiB chunks so the UI can show a real
    // percentage, then delete the source — mimicking File.Move's
    // copy+delete without losing progress visibility. A partial
    // cross-volume copy is cleaned up on failure/cancel so we never strand
    // a half file at the destination.
    private static async Task MoveFileWithProgressAsync(
        string src, string dst, FileMoveProgress progress, CancellationToken ct)
    {
        if (MovePathHelpers.IsSameVolume(src, dst))
        {
            File.Move(src, dst);
            return;
        }

        const int bufferSize = 1 << 20; // 1 MiB
        var buffer = new byte[bufferSize];
        long copied = 0;
        try
        {
            await using (var input = new FileStream(
                src, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            await using (var output = new FileStream(
                dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                    progress.Report(copied);
                }
                await output.FlushAsync(ct);
            }
        }
        catch
        {
            // Drop the partial destination copy before bubbling up.
            try { if (File.Exists(dst)) File.Delete(dst); } catch { /* best effort */ }
            throw;
        }

        progress.SetPhase("finalizing");
        File.Delete(src);
    }

    // Moves the file at video.FilePath into <setRoot>/<specialFolderName>/<rel>,
    // updates the row, and runs setFlag. Used by mark-for-deletion / mark-playback-issue.
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
            return await ReturnFreshDtoAsync(db, video.Id, ct);
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
            return await ReturnFreshDtoAsync(db, video.Id, ct);
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

            // `moved` distinguishes "did File.Move actually succeed" from
            // "we found the source missing and want to set the flag
            // anyway". The latter is the same outcome as falling into the
            // outer `else` branch below — the file's gone, just flag the
            // row — but reached via the move loop's catch instead of the
            // up-front File.Exists probe (which can race). Without this
            // distinction a missing source produced a 500 after six
            // pointless retries (FileNotFoundException inherits IOException
            // so it slid into the retry catch).
            bool moved = false;
            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(video.FilePath, targetPath);
                    moved = true;
                    break;
                }
                catch (FileNotFoundException ex)
                {
                    // Source vanished between the probe and the move:
                    // a prior mark action moved it, the user deleted
                    // it on disk, a flaky network share dropped it.
                    // Treat as "file is gone" — set the flag, no 500.
                    logger.LogWarning(ex,
                        "Source file gone for video {VideoId} at {Path}; setting flag without move",
                        video.Id, video.FilePath);
                    break;
                }
                catch (DirectoryNotFoundException ex)
                {
                    // Same reasoning as FileNotFoundException — the
                    // containing directory no longer exists, so there's
                    // nothing to move. Don't 500 the user; flag and move on.
                    logger.LogWarning(ex,
                        "Source directory gone for video {VideoId} at {Path}; setting flag without move",
                        video.Id, video.FilePath);
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
            if (moved) video.FilePath = PathNormalizer.Normalize(targetPath);
        }
        else
        {
            logger.LogWarning("Video {VideoId} file missing on disk at {Path}; setting flag only",
                video.Id, video.FilePath);
        }

        setFlag(video);
        await db.SaveChangesAsync(ct);
        return await ReturnFreshDtoAsync(db, video.Id, ct);
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
            return await ReturnFreshDtoAsync(db, video.Id, ct);
        }

        var sets = await db.VideoSets.Where(s => s.Enabled).ToListAsync(ct);
        var set = sets.FirstOrDefault(s =>
            video.FilePath.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase));
        if (set is null)
        {
            clearFlag(video);
            await db.SaveChangesAsync(ct);
            return await ReturnFreshDtoAsync(db, video.Id, ct);
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
            return await ReturnFreshDtoAsync(db, video.Id, ct);
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

            // See MarkAndMoveAsync for the rationale on `moved` and the
            // FileNotFoundException / DirectoryNotFoundException catches:
            // a missing source means "file's gone, clear the flag and
            // stop pretending we have an on-disk artifact" — not a 500.
            bool moved = false;
            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(video.FilePath, originalPath);
                    moved = true;
                    break;
                }
                catch (FileNotFoundException ex)
                {
                    logger.LogWarning(ex,
                        "Source file gone for video {VideoId} at {Path}; clearing flag without restore",
                        video.Id, video.FilePath);
                    break;
                }
                catch (DirectoryNotFoundException ex)
                {
                    logger.LogWarning(ex,
                        "Source directory gone for video {VideoId} at {Path}; clearing flag without restore",
                        video.Id, video.FilePath);
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
            if (moved) video.FilePath = PathNormalizer.Normalize(originalPath);
        }
        else
        {
            logger.LogWarning("Video {VideoId} file missing on disk at {Path}; clearing flag only",
                video.Id, video.FilePath);
        }

        clearFlag(video);
        await db.SaveChangesAsync(ct);
        return await ReturnFreshDtoAsync(db, video.Id, ct);
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

    // Permission-safe directory enumeration. Used by the import browse
    // endpoint to decide whether a folder has subdirectories without
    // letting an UnauthorizedAccessException / IOException take down
    // the whole response. Returns -1 on failure so callers can
    // distinguish "0 subdirs (readable)" from "couldn't list".
    private static int SafeGetDirectoryCount(string path, ILogger? logger = null)
    {
        try { return Directory.GetDirectories(path).Length; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Directory.GetDirectories failed for {Path} — treating as no subdirectories.",
                path);
            return -1;
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
                    "playbackIssue" => v.PlaybackIssue,
                    "markedForDeletion" => v.MarkedForDeletion,
                    "favorite" => v.IsFavorite,
                    // "isClip" reads off the structural ParentVideoId
                    // — a Video row is a clip iff it has a parent. No
                    // separate boolean column; clips are flagged
                    // automatically when CreateClip inserts the row.
                    "isClip" => v.ParentVideoId.HasValue,
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

        // GET /logs — Windowed snapshot of the in-memory log buffer.
        //
        //   sinceMinutes  default 5, clamped [1, 2880] (=48h, the
        //                 buffer's retention). The Logs page defaults
        //                 to a 5-minute window because the previous
        //                 "return everything" behavior was sending the
        //                 full 48h of events on every poll, which got
        //                 painfully slow on chatty boots.
        //   take          default 1000, clamped [1, 5000]. Caps the
        //                 response so an unusually loud minute can't
        //                 dump megabytes per poll. The client polls
        //                 every 2.5s anyway, so a 1000-entry cap is
        //                 plenty of recent context. Older entries
        //                 belong in Seq.
        api.MapGet("/logs", (LogBuffer buf, int? sinceMinutes, int? take) =>
        {
            var minutes = Math.Clamp(sinceMinutes ?? 5, 1, 2880);
            var limit = Math.Clamp(take ?? 1000, 1, 5000);
            var since = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(minutes);
            return Results.Ok(buf.SnapshotRecent(since, limit));
        }).WithName("GetLogs");

        // === Global search ==================================================
        // Powers the Ctrl+K command palette on the frontend. v1 returns
        // matching videos; v2 will add tag / source / etc. results behind
        // additional [JsonDerivedType] entries on SearchResult.
        //
        // Query string is treated as a single substring (case-insensitive).
        // Matches against four trigram-indexed fields (FileName, FilePath,
        // Notes, Md5) plus tag names via the VideoTags JOIN. Pg_trgm GIN
        // indexes (see migration 20260520010000_AddSearchTrigramIndexes)
        // keep this subsecond at 100k+ rows; without them this would be a
        // sequential scan.
        //
        // v2 hooks already present in the wire contract:
        //   · `kinds` query param accepts a CSV allow-list. v1 only honors
        //     "video"; the parameter is parsed and validated now so v2
        //     can add "tag", "source", etc. without a breaking change.
        //   · The SearchResult union is open — v2 just adds a
        //     [JsonDerivedType] on SearchResult and a new result-shape
        //     record, then wires the corresponding query into this method.
        //   · Tag.Aliases is JSONB — v1 skips alias matching to keep the
        //     query in pure LINQ; v2 should add a raw-SQL fragment using
        //     `jsonb_array_elements_text(...) ILIKE ?`.
        api.MapGet("/search", async (
            VideoOrganizerDbContext db,
            string? q,
            int? limit,
            int? offset,
            string? kinds,
            CancellationToken ct) =>
        {
            var query = (q ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                return Results.Ok(new SearchResponse(
                    Query: string.Empty,
                    TotalCount: 0,
                    Truncated: false,
                    Results: Array.Empty<SearchResult>()));
            }

            var lim = Math.Clamp(limit ?? 50, 1, 200);
            var off = Math.Max(offset ?? 0, 0);

            // Allow-list parsing. v1 only recognizes "video". Unknown
            // kinds are silently dropped so a v1 server still works
            // when a v2 client sends `?kinds=video,tag`.
            var requestedKinds = (kinds ?? "video")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.ToLowerInvariant())
                .ToHashSet();
            var includeVideos = requestedKinds.Contains("video");

            if (!includeVideos)
            {
                return Results.Ok(new SearchResponse(
                    Query: query,
                    TotalCount: 0,
                    Truncated: false,
                    Results: Array.Empty<SearchResult>()));
            }

            // Build the ILIKE pattern via the shared escape helper so a
            // stray % or _ in the user input doesn't act as a wildcard.
            var pat = $"%{SqlHelpers.EscapeLikePattern(query)}%";

            // Base predicate: OR across the four indexed string fields +
            // matching tag names via the VideoTags join. Aliases skipped
            // in v1 (see note above).
            var baseQuery = db.Videos
                .AsNoTracking()
                .Where(v =>
                    EF.Functions.ILike(v.FileName, pat) ||
                    EF.Functions.ILike(v.FilePath, pat) ||
                    EF.Functions.ILike(v.Notes, pat) ||
                    (v.Md5 != null && EF.Functions.ILike(v.Md5, pat)) ||
                    v.VideoTags.Any(vt => vt.Tag != null && EF.Functions.ILike(vt.Tag.Name, pat)));

            var totalCount = await baseQuery.CountAsync(ct);

            // Cheap relevance ranking: exact-filename match first, then
            // filename substring, then filepath, then everything else.
            // Stable secondary sort by filename so equal-tier matches
            // come back deterministically.
            var lower = query.ToLowerInvariant();
            var rows = await baseQuery
                .Include(v => v.VideoTags).ThenInclude(vt => vt.Tag).ThenInclude(t => t!.TagGroup)
                .OrderBy(v =>
                    v.FileName.ToLower() == lower ? 0 :
                    EF.Functions.ILike(v.FileName, pat) ? 1 :
                    EF.Functions.ILike(v.FilePath, pat) ? 2 : 3)
                .ThenBy(v => v.FileName)
                .Skip(off)
                .Take(lim)
                .ToListAsync(ct);

            var results = rows.Select(v =>
            {
                // Tag matches in this iteration so the UI can surface
                // them as "tag:Performer/Bob Marley"-shaped breadcrumbs
                // rather than just showing the file's full tag list.
                var matched = new List<string>(4);
                if (v.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) matched.Add("fileName");
                if (v.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase)) matched.Add("filePath");
                if (v.Notes.Contains(query, StringComparison.OrdinalIgnoreCase)) matched.Add("notes");
                if (v.Md5 != null && v.Md5.Contains(query, StringComparison.OrdinalIgnoreCase)) matched.Add("md5");
                foreach (var vt in v.VideoTags)
                {
                    if (vt.Tag != null && vt.Tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var groupName = vt.Tag.TagGroup?.Name ?? "?";
                        matched.Add($"tag:{groupName}/{vt.Tag.Name}");
                    }
                }

                var tagNames = v.VideoTags
                    .Where(vt => vt.Tag != null)
                    .OrderBy(vt => vt.Tag!.TagGroup?.SortOrder ?? 0)
                    .ThenBy(vt => vt.Tag!.SortOrder)
                    .ThenBy(vt => vt.Tag!.Name)
                    .Select(vt => vt.Tag!.Name)
                    .ToList();

                return (SearchResult)new VideoSearchResult(
                    Id: v.Id,
                    Title: v.FileName,
                    Subtitle: v.FilePath,
                    FileSize: v.FileSize,
                    Duration: v.Duration,
                    IsClip: v.ParentVideoId.HasValue,
                    Tags: tagNames,
                    MatchedFields: matched);
            }).ToList();

            return Results.Ok(new SearchResponse(
                Query: query,
                TotalCount: totalCount,
                Truncated: totalCount > off + lim,
                Results: results));
        }).WithName("Search");

        // === Runtime info ===================================================
        // Tells the frontend whether the request came in over the loopback
        // adapter (i.e. browser is on the host machine) and what OS the
        // server is on. Drives:
        //   · the "must be on the SightsAndSounds host" banner in the
        //     layout when the user is on a different machine, and
        //   · whether to render the local-only buttons (reveal in file
        //     manager, ffprobe diagnostics) at all — those endpoints
        //     themselves also gate on IsLocalRequest server-side.
        api.MapGet("/runtime-info", (HttpContext http) =>
        {
            var os = OperatingSystem.IsWindows() ? "windows"
                   : OperatingSystem.IsMacOS() ? "macos"
                   : OperatingSystem.IsLinux() ? "linux"
                   : "other";
            return Results.Ok(new RuntimeInfoDto(IsLocalRequest(http), os));
        }).WithName("GetRuntimeInfo");

        // === Data validation ================================================
        // Diagnostic endpoints powering the /data-validation page:
        //
        //   GET /validation/missing-files
        //     Videos whose FilePath no longer exists on disk. By default
        //     scoped to enabled sources — pass includeDisabled=true to
        //     surface orphans under disabled sources too.
        //
        //   POST /validation/missing-files/purge
        //     Remove the DB rows for videos surfaced by the scan above.
        //     DB-only — no file I/O beyond a per-row File.Exists re-probe
        //     that refuses to delete rows whose file has reappeared.
        //
        //   GET /validation/extra-files
        //     Video files on disk under a configured source that have no
        //     matching Video row in the DB. Useful for spotting un-imported
        //     leftovers. Defaults to enabled sources only; sourceId scopes
        //     to a single set; includeDisabled=true scans every source.
        //
        // The "are sources reachable" check is already covered by
        // GET /video-sets returning PathExists per row, so no separate
        // endpoint here.

        api.MapGet("/validation/missing-files", async (
            VideoOrganizerDbContext db, bool? includeDisabled, CancellationToken ct) =>
        {
            var sets = await db.VideoSets.AsNoTracking().ToListAsync(ct);
            // Build a longest-prefix-first matcher so a video at
            // "/mnt/A/B/file.mp4" picks up source B's metadata when
            // both A and A/B are configured.
            var setsByLength = sets
                .OrderByDescending(s => s.Path?.Length ?? 0)
                .ToList();

            var includeAll = includeDisabled == true;
            var query = db.Videos.AsNoTracking()
                .Where(v => !v.ParentVideoId.HasValue); // clips share parent's file
            var videos = await query
                .Select(v => new { v.Id, v.FileName, v.FilePath, v.FileSize, v.IngestDate })
                .ToListAsync(ct);

            var missing = new List<MissingVideoFileDto>();
            foreach (var v in videos)
            {
                Domain.Models.VideoSet? set = null;
                foreach (var s in setsByLength)
                {
                    if (!string.IsNullOrEmpty(s.Path)
                        && v.FilePath.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        set = s;
                        break;
                    }
                }
                // Skip videos under a disabled source unless the caller
                // explicitly opts in. Videos with no matching source
                // (orphans by definition) are always included so a
                // path-typo can't permanently hide a missing file.
                if (!includeAll && set is not null && !set.Enabled) continue;

                if (File.Exists(v.FilePath)) continue;

                missing.Add(new MissingVideoFileDto(
                    VideoId: v.Id,
                    FileName: v.FileName,
                    FilePath: v.FilePath,
                    FileSize: v.FileSize,
                    IngestDate: v.IngestDate.ToString("o"),
                    SourceId: set?.Id,
                    SourceName: set?.Name,
                    SourceEnabled: set?.Enabled ?? false));
            }
            return Results.Ok(missing
                .OrderBy(m => m.SourceName)
                .ThenBy(m => m.FilePath)
                .ToList());
        }).WithName("GetValidationMissingFiles");

        // POST /validation/missing-files/purge — remove DB rows for videos
        // whose file is gone from disk. Deliberately DB-only: the file is
        // already gone (that's the premise), so unlike /videos/{id}/purge
        // nothing here touches disk. Each row is re-probed with File.Exists
        // immediately before deletion so a file that reappeared since the
        // scan (remounted drive, restored backup) is never silently dropped
        // from the library — those rows are skipped and reported back.
        // Clip rows, tags, properties, markers and blocks all cascade with
        // the parent row via FK delete behavior.
        api.MapPost("/validation/missing-files/purge", async (
            PurgeMissingFilesRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (req.VideoIds is null || req.VideoIds.Count == 0)
                return Results.BadRequest(new { error = "No video IDs provided." });

            var ids = req.VideoIds.Distinct().ToList();
            var videos = await db.Videos
                .Where(v => ids.Contains(v.Id))
                .ToListAsync(ct);
            var notFound = ids.Count - videos.Count;

            var skippedPresent = new List<Guid>();
            var deleted = 0;
            foreach (var video in videos)
            {
                if (!string.IsNullOrEmpty(video.FilePath) && File.Exists(video.FilePath))
                {
                    skippedPresent.Add(video.Id);
                    logger.LogWarning(
                        "Missing-files purge: file for video {VideoId} ({FileName}) reappeared at {Path} — row kept",
                        video.Id, video.FileName, video.FilePath);
                    continue;
                }
                db.Videos.Remove(video);
                deleted++;
            }
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Missing-files purge: {Deleted} rows deleted, {SkippedPresent} skipped (file present), {NotFound} not found",
                deleted, skippedPresent.Count, notFound);
            return Results.Ok(new PurgeMissingFilesResultDto(
                Deleted: deleted,
                SkippedPresent: skippedPresent.Count,
                NotFound: notFound,
                SkippedPresentIds: skippedPresent));
        }).WithName("PurgeValidationMissingFiles");

        api.MapGet("/validation/extra-files", async (
            VideoOrganizerDbContext db, Guid? sourceId, bool? includeDisabled,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var includeAll = includeDisabled == true;
            var setsQuery = db.VideoSets.AsNoTracking().AsQueryable();
            if (sourceId.HasValue)
                setsQuery = setsQuery.Where(s => s.Id == sourceId.Value);
            else if (!includeAll)
                setsQuery = setsQuery.Where(s => s.Enabled);
            var sets = await setsQuery.ToListAsync(ct);
            if (sets.Count == 0) return Results.Ok(new List<ExtraDiskFileDto>());

            // Build a fast lookup of every FilePath the DB knows about,
            // case-insensitive. Clips share their parent's file so
            // they're effectively dedup'd here too — multiple Video
            // rows with the same FilePath collapse to one entry.
            var dbPaths = await db.Videos.AsNoTracking()
                .Select(v => v.FilePath)
                .ToListAsync(ct);
            var known = new HashSet<string>(dbPaths, StringComparer.OrdinalIgnoreCase);

            var extras = new List<ExtraDiskFileDto>();
            foreach (var s in sets)
            {
                if (!TryDirectoryExists(s.Path, logger)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(s.Path, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Skipping VideoSet {VideoSetId} '{Name}' ({Path}) in /validation/extra-files — listing failed",
                        s.Id, s.Name, s.Path);
                    continue;
                }
                foreach (var f in files)
                {
                    // Skip the special-folder staging areas used by
                    // mark-for-deletion / playback-issue. Those are
                    // intentionally outside the player grid; flagging
                    // them as "extra" would just be noise.
                    if (PathFilters.IsInExcludedFolder(f, s.Path)) continue;
                    if (!VideoFileExtensions.IsVideo(f)) continue;
                    var normalized = PathNormalizer.Normalize(f);
                    if (known.Contains(normalized)) continue;
                    long size = 0;
                    try { size = new FileInfo(f).Length; }
                    catch { /* unreachable file — leave size at 0 */ }
                    extras.Add(new ExtraDiskFileDto(
                        FilePath: normalized,
                        FileName: Path.GetFileName(f),
                        FileSize: size,
                        SourceId: s.Id,
                        SourceName: s.Name));
                }
            }
            return Results.Ok(extras
                .OrderBy(x => x.SourceName)
                .ThenBy(x => x.FilePath)
                .ToList());
        }).WithName("GetValidationExtraFiles");

        // GET /validation/md5-candidates — list of videos eligible
        // for MD5 re-verification. Skips clips (share parent's file),
        // rows without a stored Md5 (nothing to compare against), and
        // missing files (separate tool covers those). The client
        // walks this list and POSTs each id to /md5-check below.
        api.MapGet("/validation/md5-candidates", async (
            VideoOrganizerDbContext db, bool? includeDisabled, CancellationToken ct) =>
        {
            var sets = await db.VideoSets.AsNoTracking().ToListAsync(ct);
            var setsByLength = sets
                .OrderByDescending(s => s.Path?.Length ?? 0)
                .ToList();
            var includeAll = includeDisabled == true;

            var rows = await db.Videos.AsNoTracking()
                .Where(v => !v.ParentVideoId.HasValue
                    && v.Md5 != null && v.Md5 != "")
                .Select(v => new { v.Id, v.FileName, v.FilePath, v.FileSize, v.Md5 })
                .ToListAsync(ct);

            var result = new List<Md5CandidateDto>();
            foreach (var v in rows)
            {
                Domain.Models.VideoSet? set = null;
                foreach (var s in setsByLength)
                {
                    if (!string.IsNullOrEmpty(s.Path)
                        && v.FilePath.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        set = s;
                        break;
                    }
                }
                if (!includeAll && set is not null && !set.Enabled) continue;
                if (!File.Exists(v.FilePath)) continue;
                result.Add(new Md5CandidateDto(
                    VideoId: v.Id,
                    FileName: v.FileName,
                    FilePath: v.FilePath,
                    FileSize: v.FileSize,
                    SourceId: set?.Id,
                    SourceName: set?.Name,
                    SourceEnabled: set?.Enabled ?? false,
                    StoredMd5: v.Md5!));
            }
            return Results.Ok(result
                .OrderBy(r => r.SourceName)
                .ThenBy(r => r.FilePath)
                .ToList());
        }).WithName("GetValidationMd5Candidates");

        // POST /validation/md5-check/{id} — recompute the MD5 of one
        // video's file and compare against the stored hash. Streams
        // the file off disk so a multi-GB recording doesn't pin the
        // entire byte array in memory. Cancellable via the request
        // CT — if the client navigates away mid-hash, the read
        // unwinds cleanly.
        api.MapPost("/validation/md5-check/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.Md5))
            {
                return Results.Ok(new Md5CheckResultDto(
                    VideoId: id, ComputedMd5: string.Empty,
                    StoredMd5: string.Empty, Match: false,
                    FileSize: video.FileSize, FileExists: true,
                    Error: "Video has no stored MD5 to compare against."));
            }
            if (!File.Exists(video.FilePath))
            {
                return Results.Ok(new Md5CheckResultDto(
                    VideoId: id, ComputedMd5: string.Empty,
                    StoredMd5: video.Md5, Match: false,
                    FileSize: video.FileSize, FileExists: false,
                    Error: "File missing on disk."));
            }
            try
            {
                var computed = await ComputeFileMd5Async(video.FilePath, ct);
                var match = string.Equals(computed, video.Md5, StringComparison.OrdinalIgnoreCase);
                if (!match)
                {
                    logger.LogWarning(
                        "MD5 validation mismatch for {VideoId} ({FileName}) at {Path} — stored={Stored} computed={Computed}",
                        id, video.FileName, video.FilePath, video.Md5, computed);
                }
                return Results.Ok(new Md5CheckResultDto(
                    VideoId: id, ComputedMd5: computed,
                    StoredMd5: video.Md5, Match: match,
                    FileSize: video.FileSize, FileExists: true, Error: null));
            }
            catch (OperationCanceledException)
            {
                throw; // let ASP.NET map to 499
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "MD5 validation failed for {VideoId} ({FileName}) at {Path}",
                    id, video.FileName, video.FilePath);
                return Results.Ok(new Md5CheckResultDto(
                    VideoId: id, ComputedMd5: string.Empty,
                    StoredMd5: video.Md5, Match: false,
                    FileSize: video.FileSize, FileExists: true,
                    Error: ex.Message));
            }
        }).WithName("PostValidationMd5Check");

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

        videoSets.MapGet("/", async (
            VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            // Same per-row resilience as /import/browse: a single bad
            // path (permission-denied, broken symlink, unreachable
            // mount) should not 500 the whole listing. Project to an
            // anonymous shape outside the EF query so the PathExists
            // probe runs once per row in C# memory, with TryDirectoryExists
            // already swallowing filesystem failures.
            var sets = await db.VideoSets
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                .ToListAsync(ct);
            var result = sets.Select(s => new
            {
                s.Id,
                s.Name,
                s.Path,
                s.Enabled,
                s.SortOrder,
                PathExists = TryDirectoryExists(s.Path, logger)
            }).ToList();
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
            // Same empty-roots short-circuit as the filter / list
            // endpoints — see /videos/filter for the rationale.
            if (enabledRoots.Count == 0)
                return Results.Ok(0);
            var count = await db.Videos
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)))
                .CountAsync(ct);
            return Results.Ok(count);
        }).WithName("GetVideoCount");

        // Aggregate counts of videos with each boolean flag set.
        // Powers the per-flag count badges on the browse sidebar's
        // Flags tree so the user can see how many rows would match
        // before applying the filter. Same enabled-roots scoping as
        // /videos/count so a video that's no longer under any
        // enabled set doesn't get counted.
        api.MapGet("/videos/flag-counts", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            if (enabledRoots.Count == 0)
                return Results.Ok(new FlagCountsDto(0, 0, 0, 0, 0));
            var scoped = db.Videos.AsNoTracking()
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));
            // Five small COUNT queries against indexed boolean / FK
            // columns — Postgres handles this in milliseconds. Doing
            // them sequentially (rather than as a single grouped
            // aggregate) keeps the EF translation stable.
            var favorite = await scoped.CountAsync(v => v.IsFavorite, ct);
            var needsReview = await scoped.CountAsync(v => v.NeedsReview, ct);
            var playbackIssue = await scoped.CountAsync(v => v.PlaybackIssue, ct);
            var markedForDeletion = await scoped.CountAsync(v => v.MarkedForDeletion, ct);
            var isClip = await scoped.CountAsync(v => v.ParentVideoId.HasValue, ct);
            return Results.Ok(new FlagCountsDto(
                favorite, needsReview, playbackIssue, markedForDeletion, isClip));
        }).WithName("GetFlagCounts");

        // GET /api/videos — simple AND-of-tags filter. Repeatable ?tagId=
        // narrows by every passed tag. For richer filtering, use POST
        // /api/videos/filter.
        api.MapGet("/videos", async (HttpContext http, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);

            // Same empty-DB short-circuit as POST /videos/filter — see
            // the comment there for why the EF Any() expression below
            // can be fragile with an empty parameter list.
            if (enabledRoots.Count == 0)
                return Results.Ok(new List<VideoDto>());

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

            // Empty-DB short-circuit. With no enabled VideoSets the
            // EF translation of the StartsWith-Any expression below
            // can throw on certain Npgsql/EF combos. There's nothing
            // to filter against anyway — return an empty list so the
            // browse page can render its empty state instead of a 500.
            if (enabledRoots.Count == 0)
                return Results.Ok(new List<VideoDto>());

            // Build the SQL-side candidates query. The base filter is the
            // enabled-VideoSet path-prefix predicate; SearchQuery (when
            // present) pushes a free-text ILIKE down through the same
            // query so the trigram GIN indexes do the heavy lifting
            // instead of yanking every video into memory and filtering.
            var candidatesQuery = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));

            var searchQuery = filter?.SearchQuery?.Trim();
            if (!string.IsNullOrEmpty(searchQuery))
            {
                var pat = $"%{SqlHelpers.EscapeLikePattern(searchQuery)}%";
                candidatesQuery = candidatesQuery.Where(v =>
                    EF.Functions.ILike(v.FileName, pat) ||
                    EF.Functions.ILike(v.FilePath, pat) ||
                    EF.Functions.ILike(v.Notes, pat) ||
                    (v.Md5 != null && EF.Functions.ILike(v.Md5, pat)) ||
                    v.VideoTags.Any(vt => vt.Tag != null && EF.Functions.ILike(vt.Tag.Name, pat)));
            }

            var candidates = await candidatesQuery.ToListAsync(ct);

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

        // POST /api/videos/{id}/move — move the video's file into another
        // folder under some enabled source (within or across sources).
        // Logged + reversible via file_move_logs; byte progress is reported
        // to FileMoveProgress and polled at /videos/{id}/move-progress.
        // (issue #4)
        api.MapPost("/videos/{id:guid}/move", async (
            Guid id, MoveVideoRequest req, VideoOrganizerDbContext db,
            FileMoveProgress progress, DirectoryScanCache scanCache,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.TargetDirectory))
                return Results.BadRequest(new { error = "No target folder provided." });

            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (video.ParentVideoId.HasValue)
                return Results.BadRequest(new { error = "Clips share their parent's file and can't be moved on their own." });

            var targetDir = Path.GetFullPath(req.TargetDirectory);
            var sets = await db.VideoSets.Where(s => s.Enabled).ToListAsync(ct);
            var underSource = sets.Any(s =>
                targetDir.StartsWith(Path.GetFullPath(s.Path), StringComparison.OrdinalIgnoreCase));
            if (!underSource)
                return Results.BadRequest(new { error = "Target folder is not under any enabled source." });
            if (!Directory.Exists(targetDir))
                return Results.BadRequest(new { error = "Target folder does not exist." });
            if (!File.Exists(video.FilePath))
                return Results.BadRequest(new { error = "The video's file is missing on disk." });

            var fileName = Path.GetFileName(video.FilePath);
            var desired = Path.Combine(targetDir, fileName);
            if (string.Equals(Path.GetFullPath(desired), Path.GetFullPath(video.FilePath), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "The file is already in that folder." });

            var dest = MovePathHelpers.UniqueDestination(desired, File.Exists);
            var fromPath = video.FilePath;
            long total = 0;
            try { total = new FileInfo(fromPath).Length; } catch { /* size best-effort */ }

            progress.Begin(id, total);
            try
            {
                await MoveFileWithProgressAsync(fromPath, dest, progress, ct);
            }
            catch (Exception ex)
            {
                progress.End();
                logger.LogError(ex, "Failed to move {Src} to {Dst}", fromPath, dest);
                return Results.Problem($"Could not move file: {ex.Message}");
            }
            progress.End();

            var normalizedDest = PathNormalizer.Normalize(dest);
            video.FilePath = normalizedDest;
            db.FileMoveLogs.Add(new FileMoveLog
            {
                VideoId = id,
                FileName = fileName,
                FromPath = PathNormalizer.Normalize(fromPath),
                ToPath = normalizedDest,
                MovedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            // Only the folders that gained/lost the file have stale counts —
            // evict those, not the whole cache, so the next browse stays fast
            // right after a move. (issue #4)
            EvictMovedDirs(scanCache, Path.GetDirectoryName(fromPath), targetDir);
            logger.LogInformation(
                "Moved video {VideoId} ({FileName}) from {From} to {To}",
                id, fileName, fromPath, dest);
            return await ReturnFreshDtoAsync(db, id, ct);
        }).WithName("MoveVideo");

        // GET /api/videos/{id}/move-progress — live byte progress for an
        // in-flight move/undo of this video; idle when nothing is moving it.
        api.MapGet("/videos/{id:guid}/move-progress", (Guid id, FileMoveProgress progress) =>
        {
            var (active, copied, total, phase, vid) = progress.Snapshot();
            return active && vid == id
                ? Results.Ok(new MoveProgressDto(true, copied, total, phase))
                : Results.Ok(new MoveProgressDto(false, 0, 0, "idle"));
        }).WithName("GetMoveProgress");

        // GET /api/file-moves — recent moves, newest first, for the Moves
        // list + Undo. (issue #4)
        api.MapGet("/file-moves", async (
            VideoOrganizerDbContext db, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var moves = await db.FileMoveLogs.AsNoTracking()
                .OrderByDescending(m => m.MovedAt)
                .Take(take)
                .Select(m => new FileMoveLogDto(
                    m.Id, m.VideoId, m.FileName, m.FromPath, m.ToPath,
                    m.MovedAt.ToString("o"),
                    m.RevertedAt.HasValue ? m.RevertedAt.Value.ToString("o") : null))
                .ToListAsync(ct);
            return Results.Ok(moves);
        }).WithName("ListFileMoves");

        // POST /api/file-moves/{moveId}/revert — undo a move by putting the
        // file back at its original path and restoring the row. (issue #4)
        api.MapPost("/file-moves/{moveId:guid}/revert", async (
            Guid moveId, VideoOrganizerDbContext db, FileMoveProgress progress,
            DirectoryScanCache scanCache, ILogger<Program> logger, CancellationToken ct) =>
        {
            var move = await db.FileMoveLogs.FirstOrDefaultAsync(m => m.Id == moveId, ct);
            if (move is null) return Results.NotFound();
            if (move.RevertedAt.HasValue)
                return Results.BadRequest(new { error = "This move was already undone." });

            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == move.VideoId, ct);
            if (video is null)
                return Results.BadRequest(new { error = "The video no longer exists." });
            if (!File.Exists(move.ToPath))
                return Results.BadRequest(new { error = "The moved file is no longer at its destination — can't undo." });

            var fromDir = Path.GetDirectoryName(move.FromPath);
            if (!string.IsNullOrEmpty(fromDir)) Directory.CreateDirectory(fromDir);
            var dest = MovePathHelpers.UniqueDestination(move.FromPath, File.Exists);

            long total = 0;
            try { total = new FileInfo(move.ToPath).Length; } catch { /* best-effort */ }
            progress.Begin(move.VideoId, total);
            try
            {
                await MoveFileWithProgressAsync(move.ToPath, dest, progress, ct);
            }
            catch (Exception ex)
            {
                progress.End();
                logger.LogError(ex, "Failed to undo move {MoveId}", moveId);
                return Results.Problem($"Could not undo move: {ex.Message}");
            }
            progress.End();

            video.FilePath = PathNormalizer.Normalize(dest);
            move.RevertedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // File moved back → evict only the affected folders. (issue #4)
            EvictMovedDirs(scanCache, Path.GetDirectoryName(move.ToPath), Path.GetDirectoryName(dest));
            logger.LogInformation("Reverted move {MoveId}: {To} -> {From}", moveId, move.ToPath, dest);
            return await ReturnFreshDtoAsync(db, move.VideoId, ct);
        }).WithName("RevertFileMove");

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

        // GET /api/videos/{id}/tag-suggestions — tags whose name or an alias
        // appears in the video's file name or folder path, minus the ones
        // already applied (issue #10). Lets the player offer "found these in
        // the name — add them?" on demand. Whole-word/phrase matching on
        // normalized text; file-name hits rank above folder hits.
        api.MapGet("/videos/{id:guid}/tag-suggestions", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var video = await db.Videos.AsNoTracking()
                .Include(v => v.VideoTags)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();

            var applied = video.VideoTags.Select(vt => vt.TagId).ToHashSet();

            // Split the path into the file name and the folder segments that
            // sit below the containing source root — matching the whole
            // absolute path would drag in drive letters and system folders.
            var norm = PathNormalizer.Normalize(video.FilePath);
            var fileText = Path.GetFileNameWithoutExtension(norm);
            var dir = Path.GetDirectoryName(norm)?.Replace('\\', '/') ?? string.Empty;
            var roots = await db.VideoSets.Select(s => s.Path).ToListAsync(ct);
            var setRoot = roots
                .Select(p => PathNormalizer.Normalize(p).TrimEnd('/'))
                .Where(r => norm.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Length)
                .FirstOrDefault();
            var folderText = setRoot != null && dir.Length > setRoot.Length
                ? dir[(setRoot.Length + 1)..]
                : Path.GetFileName(dir);

            var fileHay = $" {NormalizeForMatch(fileText)} ";
            var folderHay = $" {NormalizeForMatch(folderText)} ";
            // Collapsed (separator-free) forms so a multi-word tag written
            // without spaces still hits — "BobMarley" / "bobmarley" for the tag
            // "Bob Marley" (issue #10 follow-up). The whole-word haystacks above
            // can't catch that because the run has no separator to split on.
            // collapsedMin keeps short tags from turning into substring noise.
            var fileCollapsed = fileHay.Replace(" ", string.Empty);
            var folderCollapsed = folderHay.Replace(" ", string.Empty);
            const int collapsedMin = 4;

            var tags = await db.Tags.AsNoTracking().Include(t => t.TagGroup).ToListAsync(ct);
            var suggestions = new List<TagSuggestion>();
            foreach (var t in tags)
            {
                if (applied.Contains(t.Id)) continue;

                string? source = null;
                string matched = string.Empty;
                foreach (var candidate in new[] { t.Name }.Concat(t.Aliases))
                {
                    var cn = NormalizeForMatch(candidate);
                    if (cn.Length < 2) continue; // 1-char tags match everything
                    var cc = cn.Replace(" ", string.Empty); // collapsed tag

                    var inFile = fileHay.Contains($" {cn} ", StringComparison.Ordinal)
                        || (cc.Length >= collapsedMin && fileCollapsed.Contains(cc, StringComparison.Ordinal));
                    if (inFile)
                    {
                        source = "File name";
                        matched = candidate;
                        break; // file-name hit is the best source; stop looking
                    }
                    if (source is null)
                    {
                        var inFolder = folderHay.Contains($" {cn} ", StringComparison.Ordinal)
                            || (cc.Length >= collapsedMin && folderCollapsed.Contains(cc, StringComparison.Ordinal));
                        if (inFolder)
                        {
                            source = "Folder";
                            matched = candidate;
                        }
                    }
                }
                if (source is not null)
                    suggestions.Add(new TagSuggestion(
                        t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty, t.Name, source, matched));
            }

            var ordered = suggestions
                .OrderByDescending(s => s.Source == "File name")
                .ThenBy(s => s.TagGroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Ok(ordered);
        }).WithName("GetTagSuggestions");

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

        api.MapPost("/videos/{id:guid}/mark-playback-issue", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await MarkAndMoveAsync(id, "_PlaybackIssue",
                v => v.PlaybackIssue = true, db, logger, ct);
        }).WithName("MarkVideoPlaybackIssue");

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

        api.MapPost("/videos/{id:guid}/unmark-playback-issue", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await UnmarkAndRestoreAsync(id, "_PlaybackIssue",
                v => v.PlaybackIssue = false, db, logger, ct);
        }).WithName("UnmarkVideoPlaybackIssue");

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

        // List clips of a parent video — used by the scrubber to paint
        // green-tinted bands at each clip's [start, end] range so the
        // viewer can see which slices have been clipped out without
        // leaving the player. Ordered by start time. Empty list when
        // the parent has no clips, or when {parentId} is itself a clip.
        api.MapGet("/videos/{parentId:guid}/clips", async (
            Guid parentId, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var clips = await db.Videos
                .AsNoTracking()
                .Where(v => v.ParentVideoId == parentId
                    && v.ClipStartSeconds != null
                    && v.ClipEndSeconds != null)
                .OrderBy(v => v.ClipStartSeconds)
                .Select(v => new ClipSummaryDto(
                    v.Id, v.FileName, v.ClipStartSeconds!.Value, v.ClipEndSeconds!.Value))
                .ToListAsync(ct);
            return Results.Ok(clips);
        }).WithName("GetClipsOfParent");

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

        // List of videos flagged with PlaybackIssue. Mirrors the
        // marked-for-deletion list and powers the /playback-issues
        // triage page (which mirrors /purge).
        api.MapGet("/videos/playback-issues", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var videos = await IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => v.PlaybackIssue)
                .OrderBy(v => v.FilePath)
                .ToListAsync(ct);
            return Results.Ok(videos.Select(ToDto).ToList());
        }).WithName("GetPlaybackIssueVideos");

        // Reveal the file in the host OS's file manager (Explorer /
        // Finder / xdg-open). Loopback-gated: launching processes for a
        // remote caller would either be useless (UI on the wrong
        // machine) or actively dangerous (RCE on the host). Path is
        // also re-validated against enabled VideoSets so that even a
        // local caller can't ask us to launch into an arbitrary path.
        api.MapPost("/videos/{id:guid}/reveal", async (
            Guid id, VideoOrganizerDbContext db, HttpContext http,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsLocalRequest(http)) return Results.Forbid();

            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
                return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(video.FilePath);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // /select reveals the file inside its parent folder
                    // with the file pre-selected, instead of opening the
                    // file with its default app.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // -R reveals in Finder.
                    Process.Start("open", new[] { "-R", fullPath });
                }
                else
                {
                    // xdg-open accepts a directory path; most desktops
                    // open it in the user's preferred file manager.
                    var dir = Path.GetDirectoryName(fullPath) ?? fullPath;
                    Process.Start("xdg-open", new[] { dir });
                }
                logger.LogInformation(
                    "Revealed video {VideoId} ({FileName}) in file manager",
                    video.Id, video.FileName);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to reveal video {VideoId} at {Path}", video.Id, fullPath);
                return Results.Problem($"Failed to open file manager: {ex.Message}");
            }
        }).WithName("RevealVideoInFileManager");

        // === Open terminal at the video's directory =======================
        //
        // Same security model as /reveal — loopback-only + VideoSet path-
        // prefix check. The launch itself is platform-specific:
        //
        //   · Windows: try Windows Terminal (wt.exe) first since it's the
        //              modern default on Win11 and gives the best UX.
        //              Fall back to PowerShell, then cmd.
        //   · macOS:   `open -a Terminal <dir>` opens Terminal.app at the
        //              directory. iTerm users can swap manually.
        //   · Linux:   no universal terminal — probe a long list of common
        //              emulators in priority order. x-terminal-emulator is
        //              Debian's alternatives-managed symlink (whatever
        //              the user picked); after that, walk through the
        //              big DE-bundled emulators (gnome-terminal, konsole,
        //              etc.), then keyboard-driven favorites (kitty,
        //              alacritty, wezterm), ending at xterm as the
        //              "always installed somewhere" fallback.
        //
        // Each attempt runs via Process.Start with UseShellExecute=false.
        // A missing binary throws Win32Exception (ERROR_FILE_NOT_FOUND);
        // we catch and try the next one. First success wins. If none work,
        // 500 with a list of what was tried so the user knows what to
        // install.
        api.MapPost("/videos/{id:guid}/open-terminal", async (
            Guid id, VideoOrganizerDbContext db, HttpContext http,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsLocalRequest(http)) return Results.Forbid();

            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
                return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(video.FilePath);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            var dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return Results.NotFound();

            var attempts = TerminalLaunchAttempts(dir).ToList();
            var triedCommands = new List<string>(attempts.Count);
            Exception? lastEx = null;

            foreach (var attempt in attempts)
            {
                triedCommands.Add(attempt.Command);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = attempt.Command,
                        UseShellExecute = false,
                        // Set the launcher's CWD too — terminals like xterm
                        // inherit it, so this also covers any emulator we
                        // didn't bother adding a --working-directory arg for.
                        WorkingDirectory = dir
                    };
                    foreach (var a in attempt.Arguments) psi.ArgumentList.Add(a);
                    using var _ = Process.Start(psi);
                    logger.LogInformation(
                        "Opened terminal for video {VideoId} at {Dir} via {Command}",
                        video.Id, dir, attempt.Command);
                    return Results.NoContent();
                }
                catch (Exception ex)
                {
                    // Most common: Win32Exception "No such file or directory"
                    // when the binary isn't installed. Keep walking the list.
                    lastEx = ex;
                }
            }

            logger.LogError(lastEx,
                "No terminal emulator could be launched for video {VideoId} at {Dir}. Tried: {Tried}",
                video.Id, dir, string.Join(", ", triedCommands));
            return Results.Problem(
                $"No terminal emulator could be launched. Tried: {string.Join(", ", triedCommands)}. " +
                $"Install one (e.g. xterm) or run the API on a machine that has a terminal in PATH.");
        }).WithName("OpenTerminalAtVideo");

        // Run ffprobe on the file and return stdout/stderr/exitCode so
        // the frontend can show a diagnostic modal. Same loopback +
        // VideoSet path guards as /reveal — running a process is the
        // privileged action; the JSON output is just plumbing. ffprobe
        // is taken from FFmpeg.ExecutablesPath which the API pins on
        // startup (Program.cs); falls back to PATH if for some reason
        // that's not set.
        api.MapGet("/videos/{id:guid}/ffprobe", async (
            Guid id, VideoOrganizerDbContext db, HttpContext http,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsLocalRequest(http)) return Results.Forbid();

            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
                return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(video.FilePath);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.Forbid();

            try
            {
                var ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
                var ffprobePath = !string.IsNullOrEmpty(FFmpeg.ExecutablesPath)
                    ? Path.Combine(FFmpeg.ExecutablesPath, ffprobeName)
                    : ffprobeName;

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-show_format");
                psi.ArgumentList.Add("-show_streams");
                psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
                psi.ArgumentList.Add(fullPath);

                using var proc = Process.Start(psi);
                if (proc is null)
                    return Results.Problem("Failed to start ffprobe process");

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                logger.LogInformation(
                    "ffprobe {FileName}: exit={ExitCode}, stdout={StdoutLen}b, stderr={StderrLen}b",
                    video.FileName, proc.ExitCode, stdout.Length, stderr.Length);
                return Results.Ok(new FfprobeResultDto(
                    Stdout: stdout,
                    Stderr: stderr,
                    ExitCode: proc.ExitCode,
                    FilePath: fullPath));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "ffprobe failed for video {VideoId} at {Path}", video.Id, fullPath);
                return Results.Problem($"ffprobe failed: {ex.Message}");
            }
        }).WithName("RunFfprobeOnVideo");

        api.MapPost("/videos/{id:guid}/purge", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            // Accept rows that are either marked-for-deletion or
            // flagged with a playback issue. The /purge page calls
            // this with the former; the /playback-issues page calls
            // it with the latter (Purge All button bypasses the
            // staging step). Anything else is a misuse — the user
            // hasn't explicitly opted into destruction yet.
            if (!video.MarkedForDeletion && !video.PlaybackIssue)
            {
                logger.LogWarning(
                    "Purge rejected for video {VideoId} ({FileName}) — neither MarkedForDeletion nor PlaybackIssue is set (state: deleted={IsDeleted}, playbackIssue={PlaybackIssue})",
                    video.Id, video.FileName, video.MarkedForDeletion, video.PlaybackIssue);
                return Results.BadRequest(new { error = "Video is not flagged for purge." });
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

        // Bulk-purge every row currently flagged with PlaybackIssue.
        // Same destructive contract as /videos/purge-all but keyed
        // off the PlaybackIssue flag instead of MarkedForDeletion —
        // skips the two-step "queue then purge" flow when the user
        // has decided everything in this triage list is junk.
        // Same parent-before-clip ordering, same per-row try/catch,
        // same { purged, failed[] } response shape so the existing
        // bulk-progress modal on the frontend can consume it.
        api.MapPost("/videos/purge-all-playback-issues", async (
            VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var videos = await db.Videos.Where(v => v.PlaybackIssue).ToListAsync(ct);
            logger.LogInformation(
                "Purge-all-playback-issues starting — {TotalCandidates} videos flagged",
                videos.Count);

            var purged = 0;
            var failed = new List<object>();
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
                    logger.LogError(ex,
                        "Purge-all-playback-issues: failed on {VideoId} ({FileName}) at {Path}",
                        video.Id, video.FileName, video.FilePath);
                    failed.Add(new { id = video.Id, fileName = video.FileName, error = ex.Message });
                }
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Purge-all-playback-issues complete — {Purged} purged, {Failed} failed of {Total} candidates",
                purged, failed.Count, videos.Count);
            return Results.Ok(new { purged, failed });
        }).WithName("PurgeAllPlaybackIssues");

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
            // Open with FileShare.Read|Delete so a concurrent File.Move
            // (e.g. user presses W to mark Playback Issue, which moves
            // the file into _PlaybackIssue/) doesn't fail with "file in use".
            // Default Results.File(path, ...) opens with FileShare.Read
            // only on Windows, which locks the inode against renames.
            // Results.File takes ownership of the FileStream and
            // disposes it after the response writes.
            var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            return Results.File(stream, contentType, enableRangeProcessing: true);
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
            // Two queries instead of one-with-correlated-subquery. The
            // earlier `db.Tags.Count(t => t.TagGroupId == g.Id)` inside
            // the projection threw 500 on certain Npgsql/EF combos
            // (specifically when TagGroups was empty or partially
            // seeded). Pre-fetching the counts as a separate dictionary
            // sidesteps the correlated-subquery translation entirely.
            var counts = await db.Tags.AsNoTracking()
                .GroupBy(t => t.TagGroupId)
                .Select(g => new { TagGroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TagGroupId, x => x.Count, ct);

            // For each group, count distinct videos that have at least
            // one tag from that group. Subtracting from the total
            // video count gives the "Missing / None" badge value the
            // browse sidebar shows next to the missing-leaf for each
            // group. Distinct over (TagGroupId, VideoId) avoids
            // counting a video twice if it has multiple tags in the
            // same group.
            var totalVideos = await db.Videos.CountAsync(ct);
            var videosWithTagInGroup = await db.VideoTags.AsNoTracking()
                .Where(vt => vt.Tag != null)
                .Select(vt => new { vt.Tag!.TagGroupId, vt.VideoId })
                .Distinct()
                .GroupBy(x => x.TagGroupId)
                .Select(g => new { TagGroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TagGroupId, x => x.Count, ct);

            var groups = await db.TagGroups.AsNoTracking()
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
                .Select(g => new { g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes })
                .ToListAsync(ct);

            var rows = groups.Select(g => new TagGroupDto(
                g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes,
                counts.GetValueOrDefault(g.Id, 0),
                Math.Max(0, totalVideos - videosWithTagInGroup.GetValueOrDefault(g.Id, 0)))).ToList();
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

        // POST /api/tags/bulk — create many tags in one request (issue #49).
        // The Tag Management paste box used to fire one POST per name, which
        // fell over for thousands of tags. This inserts the whole batch in a
        // single round-trip. Names are trimmed; blanks ignored; names that
        // collide with an existing tag in the group (or repeat earlier in the
        // batch), case-insensitively, are skipped so the per-group unique-name
        // rule can't trip the insert.
        tagsGroup.MapPost("/bulk", async (
            BulkCreateTagsRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!await db.TagGroups.AnyAsync(g => g.Id == req.TagGroupId, ct))
                return Results.BadRequest(new { error = "TagGroup not found." });

            var seen = new HashSet<string>(
                await db.Tags.Where(t => t.TagGroupId == req.TagGroupId)
                    .Select(t => t.Name).ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            var toAdd = new List<Tag>();
            var skipped = 0;
            foreach (var raw in req.Names ?? Array.Empty<string>())
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(name)) { skipped++; continue; }
                toAdd.Add(new Tag
                {
                    Id = Guid.NewGuid(),
                    TagGroupId = req.TagGroupId,
                    Name = name,
                    Aliases = new(),
                    IsFavorite = req.IsFavorite,
                    SortOrder = 0,
                    Notes = string.Empty
                });
            }

            if (toAdd.Count > 0)
            {
                db.Tags.AddRange(toAdd);
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation(
                "Bulk-created {Created} tag(s) in TagGroup {TagGroupId} ({Skipped} skipped)",
                toAdd.Count, req.TagGroupId, skipped);
            return Results.Ok(new BulkCreateTagsResponse(toAdd.Count, skipped));
        }).WithName("BulkCreateTags");

        tagsGroup.MapPut("/{id:guid}", async (
            Guid id, UpdateTagRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });

            // Optional group move. VideoTag rows reference the tag by id, so
            // re-pointing TagGroupId carries every existing video tagging
            // into the new group untouched — exactly the contract a "I made
            // this tag in the wrong group" fix needs. The name-uniqueness
            // check below runs against the TARGET group so the move can't
            // land on a name collision.
            var targetGroupId = req.TagGroupId ?? t.TagGroupId;
            if (targetGroupId != t.TagGroupId
                && !await db.TagGroups.AnyAsync(g => g.Id == targetGroupId, ct))
            {
                return Results.BadRequest(new { error = "Target TagGroup not found." });
            }
            if (await db.Tags.AnyAsync(x => x.TagGroupId == targetGroupId && x.Name == req.Name && x.Id != id, ct))
                return Results.Conflict(new { error = "A tag with that name already exists in this group." });

            // Capture before-state so the log can show what actually changed —
            // a tag rename + alias edit affects search/typeahead behavior, so
            // it's worth being able to grep for "when did this tag get
            // renamed".
            var oldName = t.Name;
            var oldAliasCount = t.Aliases.Count;
            var oldGroupId = t.TagGroupId;

            t.Name = req.Name;
            t.Aliases = req.Aliases.ToList();
            t.IsFavorite = req.IsFavorite;
            t.SortOrder = req.SortOrder;
            t.Notes = req.Notes;
            t.TagGroupId = targetGroupId;
            await db.SaveChangesAsync(ct);

            if (oldGroupId != t.TagGroupId)
            {
                var attachedVideos = await db.VideoTags.CountAsync(vt => vt.TagId == id, ct);
                logger.LogInformation(
                    "Moved Tag {TagId} '{Name}' from TagGroup {OldGroupId} to {NewGroupId} ({AttachedVideos} video taggings preserved)",
                    t.Id, t.Name, oldGroupId, t.TagGroupId, attachedVideos);
            }
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

        // === Duplicates =====================================================
        // User-flagged "these two videos might be the same content" pairs.
        // Created from the browse page's duplicate-hunt flow, reviewed on
        // the /duplicates page (confirm / reject / reopen / delete).

        var duplicates = api.MapGroup("/duplicates").WithTags("Duplicates");

        // Project a candidate plus both fully-loaded videos. The videos
        // are loaded separately through IncludeForVideoDto so the tag /
        // property projections match every other Video endpoint.
        static DuplicateCandidateDto ToDuplicateDto(DuplicateCandidate d, Video a, Video b) =>
            new(d.Id, (DuplicateStatusDto)(int)d.Status, d.CreatedAt, ToDto(a), ToDto(b));

        duplicates.MapPost("/", async (
            CreateDuplicateCandidateRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (req.VideoAId == req.VideoBId)
                return Results.BadRequest(new { error = "A video cannot be a duplicate of itself." });

            // Normalize the pair ordering so (A,B) and (B,A) land on the
            // same row — the unique index then guarantees one row per pair.
            var (aId, bId) = req.VideoAId.CompareTo(req.VideoBId) < 0
                ? (req.VideoAId, req.VideoBId)
                : (req.VideoBId, req.VideoAId);

            var videos = await IncludeForVideoDto(db.Videos.AsNoTracking())
                .Where(v => v.Id == aId || v.Id == bId)
                .ToListAsync(ct);
            var a = videos.FirstOrDefault(v => v.Id == aId);
            var b = videos.FirstOrDefault(v => v.Id == bId);
            if (a is null || b is null)
                return Results.NotFound(new { error = "One or both videos not found." });

            var existing = await db.DuplicateCandidates
                .FirstOrDefaultAsync(d => d.VideoAId == aId && d.VideoBId == bId, ct);
            if (existing is not null)
            {
                // Idempotent: re-flagging an existing pair just returns it
                // (whatever its review status) instead of erroring — the
                // user pressing "mark" twice mid-hunt shouldn't see a 409.
                return Results.Ok(ToDuplicateDto(existing, a, b));
            }

            var candidate = new DuplicateCandidate { VideoAId = aId, VideoBId = bId };
            db.DuplicateCandidates.Add(candidate);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Flagged duplicate candidate {CandidateId}: {VideoAId} ('{FileA}') vs {VideoBId} ('{FileB}')",
                candidate.Id, aId, a.FileName, bId, b.FileName);
            return Results.Created($"/api/duplicates/{candidate.Id}", ToDuplicateDto(candidate, a, b));
        }).WithName("CreateDuplicateCandidate");

        duplicates.MapGet("/", async (
            string? status, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            IQueryable<DuplicateCandidate> query = db.DuplicateCandidates.AsNoTracking();
            // status filter: pending / confirmed / rejected; omitted or
            // "all" returns everything.
            if (!string.IsNullOrWhiteSpace(status)
                && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<DuplicateStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                query = query.Where(d => d.Status == parsed);
            }
            var candidates = await query.OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
            if (candidates.Count == 0) return Results.Ok(Array.Empty<DuplicateCandidateDto>());

            // Load every referenced video once (with the standard tag /
            // property includes) instead of Include()-ing through both
            // navigations on every row.
            var videoIds = candidates.SelectMany(d => new[] { d.VideoAId, d.VideoBId }).Distinct().ToList();
            var videoById = await IncludeForVideoDto(db.Videos.AsNoTracking())
                .Where(v => videoIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, ct);

            // A cascade-deleted video can't leave an orphan row (FK), but
            // guard anyway so one inconsistent row can't 500 the page.
            var dtos = candidates
                .Where(d => videoById.ContainsKey(d.VideoAId) && videoById.ContainsKey(d.VideoBId))
                .Select(d => ToDuplicateDto(d, videoById[d.VideoAId], videoById[d.VideoBId]))
                .ToList();
            return Results.Ok(dtos);
        }).WithName("ListDuplicateCandidates");

        // Review transitions. Reopen lets a mis-click on Confirm/Reject be
        // undone without deleting and re-flagging the pair.
        async Task<IResult> SetDuplicateStatus(
            Guid id, DuplicateStatus newStatus, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct)
        {
            var d = await db.DuplicateCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            var old = d.Status;
            d.Status = newStatus;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Duplicate candidate {CandidateId} status {OldStatus} -> {NewStatus}", id, old, newStatus);

            var videos = await IncludeForVideoDto(db.Videos.AsNoTracking())
                .Where(v => v.Id == d.VideoAId || v.Id == d.VideoBId)
                .ToListAsync(ct);
            var a = videos.FirstOrDefault(v => v.Id == d.VideoAId);
            var b = videos.FirstOrDefault(v => v.Id == d.VideoBId);
            if (a is null || b is null) return Results.NoContent();
            return Results.Ok(ToDuplicateDto(d, a, b));
        }

        duplicates.MapPost("/{id:guid}/confirm",
            (Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
                SetDuplicateStatus(id, DuplicateStatus.Confirmed, db, logger, ct))
            .WithName("ConfirmDuplicateCandidate");

        duplicates.MapPost("/{id:guid}/reject",
            (Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
                SetDuplicateStatus(id, DuplicateStatus.Rejected, db, logger, ct))
            .WithName("RejectDuplicateCandidate");

        duplicates.MapPost("/{id:guid}/reopen",
            (Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
                SetDuplicateStatus(id, DuplicateStatus.Pending, db, logger, ct))
            .WithName("ReopenDuplicateCandidate");

        duplicates.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var d = await db.DuplicateCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            db.DuplicateCandidates.Remove(d);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Deleted duplicate candidate {CandidateId} ({Status})", id, d.Status);
            return Results.NoContent();
        }).WithName("DeleteDuplicateCandidate");

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
