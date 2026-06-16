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

public static partial class ApiEndpoints
{
    // In-memory playlist storage (lost on restart, same as before).
    private static readonly Dictionary<Guid, PlaylistDto> _playlists = new();

    // --- Video DTO projection ----------------------------------------------

    // Project the OcrScanProgress singleton's snapshot tuple to the wire DTO.
    private static OcrScanProgressDto ToScanDto(
        (bool Active, Guid VideoId, double ScannedThroughSeconds, double DurationSeconds,
         int Hits, string Phase, string? Error) s)
        => new(s.Active, s.ScannedThroughSeconds, s.DurationSeconds, s.Hits, s.Phase, s.Error);

    // Project the ClipExportProgress snapshot tuple to the wire DTO.
    private static ClipExportProgressDto ToClipExportDto(
        (bool Active, int Total, int Done, string Current, string Phase, IReadOnlyList<string> Errors) s)
        => new(s.Active, s.Total, s.Done, s.Current, s.Phase, s.Errors);

    // Project the BlockRemovalProgress snapshot tuple to the wire DTO.
    private static BlockRemovalProgressDto ToBlockRemovalDto(
        (bool Active, int Total, int Done, string Current, string Phase, IReadOnlyList<string> Errors) s)
        => new(s.Active, s.Total, s.Done, s.Current, s.Phase, s.Errors);

    // Project the RepairProgress snapshot tuple to the wire DTO.
    private static RepairProgressDto ToRepairDto(
        (bool Active, int Total, int Done, string Current, string Phase, IReadOnlyList<string> Errors) s)
        => new(s.Active, s.Total, s.Done, s.Current, s.Phase, s.Errors);

    // Project the JoinProgress snapshot tuple to the wire DTO.
    private static JoinProgressDto ToJoinDto(
        (bool Active, int Total, int Done, string Current, string Phase, IReadOnlyList<string> Errors) s)
        => new(s.Active, s.Total, s.Done, s.Current, s.Phase, s.Errors);

    // Project the EncodeProgress snapshot tuple to the wire DTO.
    private static EncodeProgressDto ToEncodeDto(
        (bool Active, int Total, int Done, string Current, string Phase, IReadOnlyList<string> Errors) s)
        => new(s.Active, s.Total, s.Done, s.Current, s.Phase, s.Errors);

    // Project the StreamingOptimizeProgress snapshot tuple to the wire DTO.
    private static StreamingOptimizeProgressDto ToOptimizeDto(
        (bool Active, int Total, int Done, int Optimized, int Skipped, string Current, string Phase, IReadOnlyList<string> Errors) s)
        => new(s.Active, s.Total, s.Done, s.Optimized, s.Skipped, s.Current, s.Phase, s.Errors);

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
            v.NeedsReview, v.PlaybackIssue, v.MarkedForDeletion, v.IsFavorite,
            v.ParentVideoId, v.ClipStartSeconds, v.ClipEndSeconds,
            v.ParentVideoId.HasValue || v.IsClip || v.IsExportedClip, // Clip umbrella (#167)
            v.IsExportedClip, v.IsEdited,
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
                if (PathFilters.IsHiddenFile(file)) continue; // issue #62
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

    // Release / quality / codec tokens that show up in file names but are
    // never useful as content tags — dropped from candidate extraction so the
    // "Potential tags" list isn't polluted with "1080p", "x264", etc. (#10).
    private static readonly HashSet<string> TagCandidateNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "1080p", "720p", "480p", "360p", "2160p", "4k", "8k", "uhd", "hd", "sd", "hdr",
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx", "mpeg", "mp4", "mkv", "avi",
        "aac", "ac3", "dts", "mp3", "flac", "opus", "5", "1ch",
        "web", "webrip", "webdl", "bluray", "bdrip", "brrip", "dvdrip", "hdtv", "hdrip", "cam", "ts",
        "proper", "repack", "internal", "extended", "uncut", "remux", "10bit", "8bit", "60fps", "30fps"
    };

    // Splits a file-name stem + folder path into candidate tag names for the
    // Tag panel's "Potential tags" feature (issue #10). The stem is broken on
    // common field delimiters (keeping internal spaces, so "Bob Marley - Live"
    // yields "Bob Marley" and "Live"); folder segments are kept whole so a
    // multi-word "Bob Marley" folder survives. Bare numbers, 1-char tokens, and
    // known release-noise tokens are dropped; results are case-insensitively
    // deduped in first-seen order and capped.
    private static List<string> ExtractTagCandidates(string fileStem, string folderText)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var name = string.Join(' ', raw.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length < 2) return;
            if (name.All(char.IsDigit)) return;
            if (TagCandidateNoise.Contains(name)) return;
            if (seen.Add(name)) result.Add(name);
        }

        var delimiters = new[] { '_', '.', '-', '–', '—', '~', '|', ',', '[', ']', '(', ')', '{', '}' };
        foreach (var part in fileStem.Split(delimiters, StringSplitOptions.RemoveEmptyEntries))
            Add(part);
        foreach (var seg in folderText.Split('/', StringSplitOptions.RemoveEmptyEntries))
            Add(seg);

        return result.Take(25).ToList();
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

    // Hidden-by-default tags (issue #84) whose videos must be suppressed from a
    // listing — except any the caller explicitly filtered FOR (referencedTagIds),
    // which is how "filter for it to see it" works. Hidden means hidden across
    // every surface (browse, playlists, related, search), so each listing query
    // excludes videos carrying one of these.
    private static async Task<HashSet<Guid>> LoadAutoHideTagIdsAsync(
        VideoOrganizerDbContext db, IReadOnlyCollection<Guid> referencedTagIds, CancellationToken ct)
    {
        var hidden = await db.Tags.AsNoTracking()
            .Where(t => t.HiddenByDefault)
            .Select(t => t.Id)
            .ToListAsync(ct);
        return hidden.Where(id => !referencedTagIds.Contains(id)).ToHashSet();
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
                    // Clip flags (#167): "clip" is the umbrella (embedded child
                    // OR user-marked OR exported); the others narrow it.
                    "clip" => v.ParentVideoId.HasValue || v.IsClip || v.IsExportedClip,
                    "embedded" => v.ParentVideoId.HasValue,
                    "exported" => v.IsExportedClip,
                    "edited" => v.IsEdited,
                    "isClip" => v.ParentVideoId.HasValue || v.IsClip || v.IsExportedClip, // back-compat
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
        }).Produces<List<LogEvent>>(StatusCodes.Status200OK)
          .WithName("GetLogs");

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
                // Exported clips (#69) are hidden everywhere, search included.
                .Where(v => !v.ClipExported)
                .Where(v =>
                    EF.Functions.ILike(v.FileName, pat) ||
                    EF.Functions.ILike(v.FilePath, pat) ||
                    EF.Functions.ILike(v.Notes, pat) ||
                    (v.Md5 != null && EF.Functions.ILike(v.Md5, pat)) ||
                    v.VideoTags.Any(vt => vt.Tag != null && EF.Functions.ILike(vt.Tag.Name, pat)) ||
                    // On-screen text found by an OCR scan (issue #5).
                    v.OcrTextLines.Any(o => EF.Functions.ILike(o.Text, pat)));

            // Hidden-by-default videos (#84) are never findable via search — hidden
            // means hidden everywhere. (Search has no tag-filter to "reveal" via.)
            var autoHideTagIds = await LoadAutoHideTagIdsAsync(db, Array.Empty<Guid>(), ct);
            if (autoHideTagIds.Count > 0)
            {
                var hidden = autoHideTagIds.ToList();
                baseQuery = baseQuery.Where(v => !v.VideoTags.Any(vt => hidden.Contains(vt.TagId)));
            }

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

            // Which of this page's videos have an OCR-text hit for the query
            // (issue #5). Done as one narrow id query rather than Include-ing
            // every stored line — a scanned video can have thousands.
            var pageIds = rows.Select(v => v.Id).ToList();
            var ocrMatchIds = (await db.OcrTextLines.AsNoTracking()
                .Where(o => pageIds.Contains(o.VideoId) && EF.Functions.ILike(o.Text, pat))
                .Select(o => o.VideoId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

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
                if (ocrMatchIds.Contains(v.Id)) matched.Add("ocrText");
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
        }).Produces<SearchResponse>(StatusCodes.Status200OK)
          .WithName("Search");

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
        }).Produces<RuntimeInfoDto>(StatusCodes.Status200OK)
          .WithName("GetRuntimeInfo");

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
        }).Produces<List<MissingVideoFileDto>>(StatusCodes.Status200OK)
          .WithName("GetValidationMissingFiles");

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
        }).Produces<PurgeMissingFilesResultDto>(StatusCodes.Status200OK)
          .WithName("PurgeValidationMissingFiles");

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
        }).Produces<List<ExtraDiskFileDto>>(StatusCodes.Status200OK)
          .WithName("GetValidationExtraFiles");

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
        }).Produces<List<Md5CandidateDto>>(StatusCodes.Status200OK)
          .WithName("GetValidationMd5Candidates");

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
        }).Produces<Md5CheckResultDto>(StatusCodes.Status200OK)
          .WithName("PostValidationMd5Check");

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
        }).Produces<List<WorkerFailedRowDto>>(StatusCodes.Status200OK)
          .WithName("GetFailedThumbnails");

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
        }).Produces<List<WorkerQueueRowDto>>(StatusCodes.Status200OK)
          .WithName("GetThumbnailQueue");

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
        }).Produces<List<Md5DuplicateRowDto>>(StatusCodes.Status200OK)
          .WithName("GetMd5Duplicates");

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
        }).Produces<List<WorkerFailedRowDto>>(StatusCodes.Status200OK)
          .WithName("GetFailedMd5");

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
        }).Produces<List<WorkerQueueRowDto>>(StatusCodes.Status200OK)
          .WithName("GetMd5BackfillQueue");

        // === Video sets =====================================================

        MapVideoSetEndpoints(api);

        // === Backups (issue #32) ============================================
        MapBackupEndpoints(api);

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
                return Results.Ok(new FlagCountsDto(0, 0, 0, 0, 0, 0, 0, 0));
            var scoped = db.Videos.AsNoTracking()
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));
            // Small COUNT queries against indexed boolean / FK columns —
            // Postgres handles these in milliseconds. Sequential (rather than
            // one grouped aggregate) keeps the EF translation stable.
            var favorite = await scoped.CountAsync(v => v.IsFavorite, ct);
            var needsReview = await scoped.CountAsync(v => v.NeedsReview, ct);
            var playbackIssue = await scoped.CountAsync(v => v.PlaybackIssue, ct);
            var markedForDeletion = await scoped.CountAsync(v => v.MarkedForDeletion, ct);
            // Clip is the umbrella: embedded (child) OR user-marked OR exported.
            var clip = await scoped.CountAsync(v => v.ParentVideoId.HasValue || v.IsClip || v.IsExportedClip, ct);
            var embedded = await scoped.CountAsync(v => v.ParentVideoId.HasValue, ct);
            var exported = await scoped.CountAsync(v => v.IsExportedClip, ct);
            var edited = await scoped.CountAsync(v => v.IsEdited, ct);
            return Results.Ok(new FlagCountsDto(
                favorite, needsReview, playbackIssue, markedForDeletion, clip, embedded, exported, edited));
        }).Produces<FlagCountsDto>(StatusCodes.Status200OK)
          .WithName("GetFlagCounts");

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
            var referencedTagIds = new HashSet<Guid>();
            foreach (var raw in tagIdParams)
            {
                if (Guid.TryParse(raw, out var tid))
                {
                    referencedTagIds.Add(tid);
                    query = query.Where(v => v.VideoTags.Any(vt => vt.TagId == tid));
                }
            }

            // Suppress hidden-by-default videos (#84) unless the caller is
            // explicitly filtering for that tag.
            var autoHideTagIds = await LoadAutoHideTagIdsAsync(db, referencedTagIds, ct);
            if (autoHideTagIds.Count > 0)
            {
                var hidden = autoHideTagIds.ToList();
                query = query.Where(v => !v.VideoTags.Any(vt => hidden.Contains(vt.TagId)));
            }

            var videos = await query.ToListAsync(ct);
            return Results.Ok(videos.Select(ToDto).ToList());
        }).Produces<List<VideoDto>>(StatusCodes.Status200OK)
          .WithName("GetVideos");

        api.MapPost("/videos/filter", async (
            PlaylistFilterRequest? filter,
            VideoOrganizerDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            // How many videos matched the filter but were suppressed by the
            // hidden-by-default auto-hide (#84). Surfaced as a response header so
            // the browse bar can show an "N hidden" status without the hidden
            // videos themselves leaking into the result (issue: favorite count
            // mismatch — the flag badge counts them, the grid hides them).
            void ReportHidden(int n) => http.Response.Headers["X-Hidden-Count"] = n.ToString();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);

            // Empty-DB short-circuit. With no enabled VideoSets the
            // EF translation of the StartsWith-Any expression below
            // can throw on certain Npgsql/EF combos. There's nothing
            // to filter against anyway — return an empty list so the
            // browse page can render its empty state instead of a 500.
            if (enabledRoots.Count == 0)
            {
                ReportHidden(0);
                return Results.Ok(new List<VideoDto>());
            }

            // Build the SQL-side candidates query. The base filter is the
            // enabled-VideoSet path-prefix predicate; SearchQuery (when
            // present) pushes a free-text ILIKE down through the same
            // query so the trigram GIN indexes do the heavy lifting
            // instead of yanking every video into memory and filtering.
            var candidatesQuery = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                // Clips already exported to their own file (#69) are hidden from
                // the library — the standalone export stands in for them.
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)) && !v.ClipExported);

            var searchQuery = filter?.SearchQuery?.Trim();
            if (!string.IsNullOrEmpty(searchQuery))
            {
                var pat = $"%{SqlHelpers.EscapeLikePattern(searchQuery)}%";
                candidatesQuery = candidatesQuery.Where(v =>
                    EF.Functions.ILike(v.FileName, pat) ||
                    EF.Functions.ILike(v.FilePath, pat) ||
                    EF.Functions.ILike(v.Notes, pat) ||
                    (v.Md5 != null && EF.Functions.ILike(v.Md5, pat)) ||
                    v.VideoTags.Any(vt => vt.Tag != null && EF.Functions.ILike(vt.Tag.Name, pat)) ||
                    // On-screen text found by an OCR scan (issue #5).
                    v.OcrTextLines.Any(o => EF.Functions.ILike(o.Text, pat)));
            }

            var required = filter?.Required ?? new();
            var optional = filter?.Optional ?? new();
            var excluded = filter?.Excluded ?? new();

            // Hidden-by-default tags (issue #84): videos carrying one are
            // suppressed UNLESS the user explicitly filters for that tag
            // (Required/Optional). Tags the user opted into via the filter are
            // removed from the auto-hide set so "filter for it to see it" works.
            var referencedTagIds = required.Concat(optional)
                .Where(f => f.Type == FilterRefType.Tag && Guid.TryParse(f.Value, out _))
                .Select(f => Guid.Parse(f.Value))
                .ToHashSet();
            var autoHideTagIds = await LoadAutoHideTagIdsAsync(db, referencedTagIds, ct);

            // Push every safely-translatable slot into SQL (#127), but NOT the
            // auto-hide — we apply that last, in memory, so we can also count how
            // many matches it suppressed. When a Folder filter is present the
            // query is still narrowed by everything else; the residual in-memory
            // pass below applies the exact MatchesFilter semantics over that
            // (much smaller) set.
            var (narrowed, needsInMemory) =
                VideoFilterTranslator.Apply(candidatesQuery, required, optional, excluded, Array.Empty<Guid>());

            // All videos matching the filter, BEFORE auto-hide suppression.
            List<Video> matchedAll;
            if (!needsInMemory)
            {
                matchedAll = await narrowed.ToListAsync(ct);
            }
            else
            {
                var lookup = await LoadTagLookupAsync(db, ct);
                var candidates = await narrowed.ToListAsync(ct);
                matchedAll = candidates.Where(v =>
                {
                    if (required.Count > 0 && !required.All(t => MatchesFilter(t, v, lookup))) return false;
                    if (optional.Count > 0 && !optional.Any(t => MatchesFilter(t, v, lookup))) return false;
                    if (excluded.Count > 0 && excluded.Any(t => MatchesFilter(t, v, lookup))) return false;
                    return true;
                }).ToList();
            }

            // Suppress hidden-by-default tags from the result, and report how many
            // were suppressed so the UI can say "N hidden".
            var visible = autoHideTagIds.Count == 0
                ? matchedAll
                : matchedAll.Where(v => !v.VideoTags.Any(vt => autoHideTagIds.Contains(vt.TagId))).ToList();
            ReportHidden(matchedAll.Count - visible.Count);

            return Results.Ok(visible.Select(ToDto).ToList());
        }).Produces<List<VideoDto>>(StatusCodes.Status200OK)
          .WithName("FilterVideos");

        // Keyset-paginated variant of /videos/filter (#127). Same filter body;
        // pagination via query params: sort (shuffle|fileName|fileSize|duration|
        // folderFile), dir (asc|desc), limit, cursor (opaque), seed (shuffle).
        // Returns one page + the cursor for the next, so the browser never pulls
        // the whole matched set at once.
        api.MapPost("/videos/filter-page", async (
            PlaylistFilterRequest? filter,
            string? sort, string? dir, int? limit, string? cursor, string? seed,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            var enabledRoots = await db.VideoSets.Where(s => s.Enabled)
                .Select(s => s.Path).ToListAsync(ct);
            if (enabledRoots.Count == 0)
                return Results.Ok(new FilteredVideosPage(Array.Empty<VideoDto>(), null, 0, 0));

            var candidatesQuery = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                // Clips already exported to their own file (#69) are hidden from
                // the library — the standalone export stands in for them.
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)) && !v.ClipExported);

            var searchQuery = filter?.SearchQuery?.Trim();
            if (!string.IsNullOrEmpty(searchQuery))
            {
                var pat = $"%{SqlHelpers.EscapeLikePattern(searchQuery)}%";
                candidatesQuery = candidatesQuery.Where(v =>
                    EF.Functions.ILike(v.FileName, pat) ||
                    EF.Functions.ILike(v.FilePath, pat) ||
                    EF.Functions.ILike(v.Notes, pat) ||
                    (v.Md5 != null && EF.Functions.ILike(v.Md5, pat)) ||
                    v.VideoTags.Any(vt => vt.Tag != null && EF.Functions.ILike(vt.Tag.Name, pat)) ||
                    // On-screen text found by an OCR scan (issue #5).
                    v.OcrTextLines.Any(o => EF.Functions.ILike(o.Text, pat)));
            }

            var required = filter?.Required ?? new();
            var optional = filter?.Optional ?? new();
            var excluded = filter?.Excluded ?? new();
            var referencedTagIds = required.Concat(optional)
                .Where(f => f.Type == FilterRefType.Tag && Guid.TryParse(f.Value, out _))
                .Select(f => Guid.Parse(f.Value))
                .ToHashSet();
            var autoHideTagIds = await LoadAutoHideTagIdsAsync(db, referencedTagIds, ct);

            // Total auto-hidden matches (whole filter, not just this page) for the
            // "N hidden" status — count of filter-matching rows carrying a hidden tag.
            var hiddenCount = 0;
            if (autoHideTagIds.Count > 0)
            {
                var (allMatches, _) = VideoFilterTranslator.Apply(
                    candidatesQuery, required, optional, excluded, Array.Empty<Guid>());
                var hiddenIds = autoHideTagIds.ToList();
                hiddenCount = await allMatches.CountAsync(v => v.VideoTags.Any(vt => hiddenIds.Contains(vt.TagId)), ct);
            }

            // Apply the filter with auto-hide suppressed in SQL so pages exclude
            // hidden rows. Folder filters can't translate → fall back to the
            // full in-memory pass (no cursor; the client windows it).
            var (narrowed, needsInMemory) =
                VideoFilterTranslator.Apply(candidatesQuery, required, optional, excluded, autoHideTagIds);

            if (needsInMemory)
            {
                var lookup = await LoadTagLookupAsync(db, ct);
                var loaded = await narrowed.ToListAsync(ct);
                var matched = loaded.Where(v =>
                {
                    if (required.Count > 0 && !required.All(t => MatchesFilter(t, v, lookup))) return false;
                    if (optional.Count > 0 && !optional.Any(t => MatchesFilter(t, v, lookup))) return false;
                    if (excluded.Count > 0 && excluded.Any(t => MatchesFilter(t, v, lookup))) return false;
                    return true;
                }).ToList();
                return Results.Ok(new FilteredVideosPage(matched.Select(ToDto).ToList(), null, matched.Count, hiddenCount));
            }

            // Full match count (visible, after auto-hide) for the "video N of M"
            // badge — the client only holds the pages it has scrolled to.
            var totalCount = await narrowed.CountAsync(ct);

            var pageSize = Math.Clamp(limit ?? 48, 1, 200);
            var mode = VideoPagination.ParseSort(sort);
            var descending = dir == "desc";
            var theSeed = string.IsNullOrEmpty(seed) ? "0" : seed!;
            var cur = VideoPagination.Decode(cursor);

            // Fetch pageSize + 1 to know whether a further page exists.
            var rows = await VideoPagination.OrderAndSeek(narrowed, mode, descending, cur, theSeed)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            string? nextCursor = null;
            if (rows.Count > pageSize)
            {
                var last = rows[pageSize - 1];
                nextCursor = VideoPagination.Encode(
                    new VideoPagination.Cursor(VideoPagination.KeyOf(last, mode, theSeed), last.Id));
                rows = rows.Take(pageSize).ToList();
            }

            return Results.Ok(new FilteredVideosPage(rows.Select(ToDto).ToList(), nextCursor, totalCount, hiddenCount));
        }).Produces<FilteredVideosPage>(StatusCodes.Status200OK)
          .WithName("FilterVideosPage");

        api.MapGet("/videos/{id:guid}", async (VideoOrganizerDbContext db, Guid id, CancellationToken ct) =>
        {
            var video = await IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            return video is null ? Results.NotFound() : Results.Ok(ToDto(video));
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("GetVideoById");

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

            // Hidden-by-default videos never surface as "related" (#84).
            var autoHideTagIds = await LoadAutoHideTagIdsAsync(db, Array.Empty<Guid>(), ct);

            IQueryable<Video> q = IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => v.Id != id)
                .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));
            if (autoHideTagIds.Count > 0)
            {
                var hidden = autoHideTagIds.ToList();
                q = q.Where(v => !v.VideoTags.Any(vt => hidden.Contains(vt.TagId)));
            }

            if (tagIds.Count == 0)
            {
                // No tags to rank by — random sample.
                var allIds = await q.Select(v => v.Id).ToListAsync(ct);
                var rng = Random.Shared;
                var pickedIds = allIds.OrderBy(_ => rng.Next()).Take(limit).ToHashSet();
                if (pickedIds.Count == 0) return Results.Ok(Array.Empty<VideoDto>());
                var fallback = await IncludeForVideoDto(db.Videos)
                    .AsNoTracking()
                    .Where(v => pickedIds.Contains(v.Id))
                    .ToListAsync(ct);
                return Results.Ok(fallback.Select(ToDto).ToList());
            }

            // Rank in SQL and take only `limit` rows, so we never materialize the
            // entire tag-sharing candidate set (#127). Score = number of tags
            // shared with the current video.
            var tagIdList = tagIds.ToList();
            var ranked = await q
                .Where(v => v.VideoTags.Any(vt => tagIdList.Contains(vt.TagId)))
                .OrderByDescending(v => v.VideoTags.Count(vt => tagIdList.Contains(vt.TagId)))
                .ThenByDescending(v => v.IngestDate)
                .Take(limit)
                .ToListAsync(ct);

            return Results.Ok(ranked.Select(ToDto).ToList());
        }).Produces<List<VideoDto>>(StatusCodes.Status200OK)
          .WithName("GetRelatedVideos");

        api.MapPost("/videos", async (VideoOrganizerDbContext db, Video video, CancellationToken ct) =>
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync(ct);
            return Results.CreatedAtRoute("GetVideoById", new { id = video.Id }, video);
        }).Produces<Video>(StatusCodes.Status201Created)
          .WithName("CreateVideo");

        // PUT /api/videos/{id} — full editable-field update. Tags managed via
        // /videos/{id}/tags, properties via /videos/{id}/properties.
        api.MapPut("/videos/{id:guid}", async (
            Guid id, UpdateVideoRequest input, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await IncludeForVideoDto(db.Videos)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video == null) return Results.NotFound();

            // FileName is the displayed/used name and a non-null DB column;
            // reject blank rather than silently storing an empty string.
            if (string.IsNullOrWhiteSpace(input.FileName))
                return Results.BadRequest(new { error = "FileName is required." });
            if (input.WatchCount < 0)
                return Results.BadRequest(new { error = "WatchCount cannot be negative." });

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

        // GET /api/videos/{id}/ocr?t=<seconds> — read on-screen text from the
        // frame at time t (issue #5). Snapshots that frame with ffmpeg and runs
        // it through the tesseract CLI. 503 (with an install hint) when
        // tesseract isn't available.
        api.MapGet("/videos/{id:guid}/ocr", async (
            Guid id, double? t, VideoOrganizerDbContext db, OcrService ocr,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (!File.Exists(video.FilePath))
                return Results.BadRequest(new { error = "The video's file is missing on disk." });

            var seconds = Math.Max(0, t ?? 0);
            // PNG (lossless) reads better than a JPEG snapshot for OCR.
            var tmp = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():N}.png");
            try
            {
                var snapshot = await FFmpeg.Conversions.FromSnippet.Snapshot(
                    video.FilePath, tmp, TimeSpan.FromSeconds(seconds));
                await snapshot.Start(ct);

                var text = await ocr.RecognizeAsync(tmp, ct);
                return Results.Ok(new OcrResultDto(seconds, text));
            }
            catch (OcrService.OcrUnavailableException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OCR failed for video {VideoId} at {Seconds}s", id, seconds);
                return Results.Problem("Failed to read text from the frame.");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }).Produces<OcrResultDto>(StatusCodes.Status200OK)
          .WithName("OcrVideoFrame");

        // POST /api/videos/{id}/ocr-scan — start (or resume) a full-video OCR
        // text scan (issue #5, ask 2). Returns 202 with the live progress;
        // 409 if a scan is already running, 404/400 if the video or its file
        // is gone. The scan runs in the background; poll the GET below.
        api.MapPost("/videos/{id:guid}/ocr-scan", async (
            Guid id, OcrScanService scanner, OcrScanProgress progress, CancellationToken ct) =>
        {
            var result = await scanner.TryStartAsync(id, ct);
            return result switch
            {
                OcrScanService.StartResult.NotFound => Results.NotFound(),
                OcrScanService.StartResult.FileMissing =>
                    Results.BadRequest(new { error = "The video's file is missing on disk." }),
                OcrScanService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "An OCR scan is already running." }),
                _ => Results.Json(ToScanDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<OcrScanProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartOcrScan");

        // GET /api/videos/{id}/ocr-scan — poll OCR scan progress. Returns the
        // live snapshot while this video is scanning (or just finished); else a
        // synthesized idle state from the durable resume marker + stored hit
        // count, so the panel can render "scanned up to MM:SS, N hits" on load.
        api.MapGet("/videos/{id:guid}/ocr-scan", async (
            Guid id, OcrScanProgress progress, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var snap = progress.Snapshot();
            if (snap.VideoId == id && (snap.Active || snap.Phase is not "idle"))
                return Results.Ok(ToScanDto(snap));

            var video = await db.Videos.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();

            var hits = await db.OcrTextLines.CountAsync(o => o.VideoId == id, ct);
            return Results.Ok(new OcrScanProgressDto(
                Active: false,
                ScannedThroughSeconds: video.OcrScannedThroughSeconds ?? 0,
                DurationSeconds: video.Duration.TotalSeconds,
                Hits: hits,
                Phase: "idle",
                Error: null));
        }).Produces<OcrScanProgressDto>(StatusCodes.Status200OK)
          .WithName("GetOcrScanProgress");

        // POST /api/videos/{id}/ocr-scan/stop — request the running scan stop.
        // Cooperative: the scan halts after the current frame, keeping its
        // resume marker so "Scan more" can pick up where it left off.
        api.MapPost("/videos/{id:guid}/ocr-scan/stop", (
            Guid id, OcrScanProgress progress) =>
        {
            var snap = progress.Snapshot();
            if (snap.Active && snap.VideoId == id) progress.RequestStop();
            return Results.Ok(ToScanDto(progress.Snapshot()));
        }).Produces<OcrScanProgressDto>(StatusCodes.Status200OK)
          .WithName("StopOcrScan");

        // GET /api/videos/{id}/ocr-text?q= — list a video's stored OCR hits in
        // playhead order, optionally filtered to those containing q. Drives the
        // results list (click a line → seek to its timestamp).
        api.MapGet("/videos/{id:guid}/ocr-text", async (
            Guid id, string? q, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var query = db.OcrTextLines.AsNoTracking().Where(o => o.VideoId == id);
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o => EF.Functions.ILike(o.Text, $"%{q.Trim()}%"));

            var lines = await query
                .OrderBy(o => o.TimeSeconds)
                .Select(o => new OcrTextLineDto(o.TimeSeconds, o.Text))
                .ToListAsync(ct);
            return Results.Ok(lines);
        }).Produces<List<OcrTextLineDto>>(StatusCodes.Status200OK)
          .WithName("GetVideoOcrText");

        // POST /api/library/remove-folder — drop every imported video under a
        // folder from the library (issue #53). Files on disk are NOT touched;
        // only the DB rows go, and EF cascades take their tags, properties,
        // duplicate pairs, move logs, and clips with them. Clips reuse the
        // parent's path, so the path-prefix match catches them too.
        api.MapPost("/library/remove-folder", async (
            RemoveFolderRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "No folder path provided." });

            var folder = PathNormalizer.Normalize(Path.GetFullPath(req.Path)).TrimEnd('/');

            // Guard: only a folder that sits under a configured source can be
            // removed — keeps a stray/empty path from wiping unrelated rows.
            var roots = await db.VideoSets.Select(s => s.Path).ToListAsync(ct);
            var underSource = roots.Any(r =>
            {
                var root = PathNormalizer.Normalize(Path.GetFullPath(r)).TrimEnd('/');
                return folder.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || folder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
            });
            if (!underSource)
                return Results.BadRequest(new { error = "That folder is not under any configured source." });

            var prefix = folder + "/";
            var victims = await db.Videos
                .Where(v => v.FilePath.StartsWith(prefix))
                .ToListAsync(ct);
            db.Videos.RemoveRange(victims);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Removed folder {Folder} from library — {Count} video(s) purged", folder, victims.Count);
            return Results.Ok(new RemoveFolderResponse(victims.Count));
        }).Produces<RemoveFolderResponse>(StatusCodes.Status200OK)
          .WithName("RemoveLibraryFolder");

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
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("MoveVideo");

        // POST /api/videos/{id}/rename — rename the file in place (same folder,
        // new base name; original extension kept). A same-volume File.Move, so
        // it's instant. Child clips share the parent's file, so their FilePath
        // is updated too. Rejects on name collision (the user picked the name).
        // (issue #172)
        api.MapPost("/videos/{id:guid}/rename", async (
            Guid id, RenameVideoRequest req, VideoOrganizerDbContext db,
            DirectoryScanCache scanCache, ILogger<Program> logger, CancellationToken ct) =>
        {
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (video.ParentVideoId.HasValue)
                return Results.BadRequest(new { error = "Clips share their parent's file and can't be renamed on their own." });
            if (!File.Exists(video.FilePath))
                return Results.BadRequest(new { error = "The video's file is missing on disk." });

            // Sanitize to a bare file name (no path separators / illegal chars),
            // and keep the original extension regardless of what the user typed.
            var ext = Path.GetExtension(video.FilePath);
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((req.NewName ?? string.Empty)
                .Where(c => !invalid.Contains(c) && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
                .ToArray()).Trim().Trim('.');
            if (ext.Length > 0 && cleaned.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^ext.Length];
            if (cleaned.Length == 0)
                return Results.BadRequest(new { error = "Please provide a valid file name." });

            var dir = Path.GetDirectoryName(video.FilePath) ?? ".";
            var newFileName = cleaned + ext;
            var dest = Path.Combine(dir, newFileName);
            var fromPath = video.FilePath;
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(fromPath), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "That's already the file's name." });
            if (File.Exists(dest))
                return Results.BadRequest(new { error = "A file with that name already exists in this folder." });

            try
            {
                File.Move(fromPath, dest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to rename {Src} to {Dst}", fromPath, dest);
                return Results.Problem($"Could not rename file: {ex.Message}");
            }

            var normalizedDest = PathNormalizer.Normalize(dest);
            video.FilePath = normalizedDest;
            video.FileName = newFileName;
            // Child clips point at the same physical file — keep them in sync.
            await db.Videos.Where(v => v.ParentVideoId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.FilePath, normalizedDest), ct);
            db.FileMoveLogs.Add(new FileMoveLog
            {
                VideoId = id,
                FileName = newFileName,
                FromPath = PathNormalizer.Normalize(fromPath),
                ToPath = normalizedDest,
                MovedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            EvictMovedDirs(scanCache, dir, dir);
            logger.LogInformation("Renamed video {VideoId} from {From} to {To}", id, fromPath, dest);
            return await ReturnFreshDtoAsync(db, id, ct);
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("RenameVideo");

        // GET /api/videos/{id}/move-progress — live byte progress for an
        // in-flight move/undo of this video; idle when nothing is moving it.
        api.MapGet("/videos/{id:guid}/move-progress", (Guid id, FileMoveProgress progress) =>
        {
            var (active, copied, total, phase, vid) = progress.Snapshot();
            return active && vid == id
                ? Results.Ok(new MoveProgressDto(true, copied, total, phase))
                : Results.Ok(new MoveProgressDto(false, 0, 0, "idle"));
        }).Produces<MoveProgressDto>(StatusCodes.Status200OK)
          .WithName("GetMoveProgress");

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
        }).Produces<List<FileMoveLogDto>>(StatusCodes.Status200OK)
          .WithName("ListFileMoves");

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
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("RevertFileMove");

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

        // GET /api/videos/{id}/tag-candidates — names parsed out of the file
        // name + folder path as *potential new tags* for the Tag panel
        // (issue #10). Unlike tag-suggestions this doesn't match the existing
        // tag table — it just proposes raw candidate names; the client treats
        // a picked candidate as a new tag (opens the create modal) and filters
        // out any that already exist in the focused group.
        api.MapGet("/videos/{id:guid}/tag-candidates", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();

            var norm = PathNormalizer.Normalize(video.FilePath);
            var fileStem = Path.GetFileNameWithoutExtension(norm);
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

            return Results.Ok(ExtractTagCandidates(fileStem, folderText));
        }).WithName("GetTagCandidates");

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
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("MarkVideoForDeletion");

        api.MapPost("/videos/{id:guid}/unmark-for-deletion", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await UnmarkAndRestoreAsync(id, "_ToDelete",
                v => v.MarkedForDeletion = false, db, logger, ct);
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("UnmarkVideoForDeletion");

        api.MapPost("/videos/{id:guid}/mark-playback-issue", async (
            Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            return await MarkAndMoveAsync(id, "_PlaybackIssue",
                v => v.PlaybackIssue = true, db, logger, ct);
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("MarkVideoPlaybackIssue");

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
        }).Produces<VideoDto>(StatusCodes.Status200OK)
          .WithName("UnmarkVideoPlaybackIssue");

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

        // Clip flag (#167) — user-settable umbrella so imported standalone clips
        // can be marked. (Embedded/exported clips are flagged automatically.)
        api.MapPost("/videos/{id:guid}/mark-clip", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v is null) return Results.NotFound();
            v.IsClip = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("MarkVideoClip");

        api.MapPost("/videos/{id:guid}/unmark-clip", async (
            Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FindAsync(new object[] { id }, ct);
            if (v is null) return Results.NotFound();
            v.IsClip = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("UnmarkVideoClip");

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
                ClipEndSeconds = end,
                IsClip = true   // embedded clip → Clip flag (#167)
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
                    && v.ClipEndSeconds != null
                    // A deleted (marked-for-deletion) child clip drops its band
                    // from the parent's scrubber (#69 follow-up).
                    && !v.MarkedForDeletion)
                .OrderBy(v => v.ClipStartSeconds)
                .Select(v => new ClipSummaryDto(
                    v.Id, v.FileName, v.ClipStartSeconds!.Value, v.ClipEndSeconds!.Value, v.ClipExported))
                .ToListAsync(ct);
            return Results.Ok(clips);
        }).Produces<List<ClipSummaryDto>>(StatusCodes.Status200OK)
          .WithName("GetClipsOfParent");

        // GET /api/clips-export/queue — parents that still have un-exported
        // clips, each with its clips (issue #69). Drives the clips-export page.
        api.MapGet("/clips-export/queue", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var clips = await db.Videos.AsNoTracking()
                .Where(v => v.ParentVideoId != null && !v.ClipExported && !v.MarkedForDeletion
                    && v.ClipStartSeconds != null && v.ClipEndSeconds != null)
                .Select(v => new
                {
                    v.Id,
                    v.FileName,
                    ParentId = v.ParentVideoId!.Value,
                    Start = v.ClipStartSeconds!.Value,
                    End = v.ClipEndSeconds!.Value
                })
                .ToListAsync(ct);

            var parentIds = clips.Select(c => c.ParentId).Distinct().ToList();
            var parents = await db.Videos.AsNoTracking()
                .Where(v => parentIds.Contains(v.Id))
                .Select(v => new { v.Id, v.FileName, v.Duration })
                .ToListAsync(ct);
            var byId = parents.ToDictionary(p => p.Id);

            var items = clips
                .GroupBy(c => c.ParentId)
                .Where(g => byId.ContainsKey(g.Key))
                .Select(g => new ClipExportQueueItemDto(
                    g.Key,
                    byId[g.Key].FileName,
                    byId[g.Key].Duration.TotalSeconds,
                    g.OrderBy(c => c.Start)
                     .Select(c => new ClipSummaryDto(c.Id, c.FileName, c.Start, c.End, false))
                     .ToList()))
                .OrderBy(i => i.ParentFileName)
                .ToList();
            return Results.Ok(items);
        }).Produces<List<ClipExportQueueItemDto>>(StatusCodes.Status200OK)
          .WithName("GetClipExportQueue");

        // GET /api/videos/{clipId}/keyframe-cut — the keyframe-snapped start the
        // stream-copy export will actually use, for the page's preview (#69).
        api.MapGet("/videos/{clipId:guid}/keyframe-cut", async (
            Guid clipId, VideoOrganizerDbContext db, ClipExportService exporter,
            CancellationToken ct) =>
        {
            var clip = await db.Videos.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == clipId, ct);
            if (clip is null || clip.ParentVideoId is null
                || clip.ClipStartSeconds is null || clip.ClipEndSeconds is null)
                return Results.NotFound();

            var parent = await db.Videos.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == clip.ParentVideoId, ct);
            if (parent is null || !File.Exists(parent.FilePath))
                return Results.NotFound();

            var snapped = await exporter.GetSnappedStartAsync(
                parent.FilePath, clip.ClipStartSeconds.Value, ct);
            return Results.Ok(new KeyframeCutDto(
                clip.ClipStartSeconds.Value, snapped, clip.ClipEndSeconds.Value));
        }).Produces<KeyframeCutDto>(StatusCodes.Status200OK)
          .WithName("GetClipKeyframeCut");

        // POST /api/clips-export — start a background export of the given clips
        // (issue #69). 202 with progress; 409 if a run is already going.
        api.MapPost("/clips-export", async (
            ExportClipsRequest req, ClipExportService exporter,
            ClipExportProgress progress, CancellationToken ct) =>
        {
            var items = req.Clips ?? Array.Empty<ExportClipItem>();
            if (items.Count == 0) return Results.BadRequest(new { error = "No clips selected." });

            var result = await exporter.TryStartAsync(items, ct);
            return result switch
            {
                ClipExportService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "A clip export is already running." }),
                ClipExportService.StartResult.NothingToDo =>
                    Results.BadRequest(new { error = "None of the selected clips can be exported." }),
                _ => Results.Json(ToClipExportDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<ClipExportProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartClipExport");

        // GET /api/clips-export — poll the export run's progress.
        api.MapGet("/clips-export", (ClipExportProgress progress) =>
            Results.Ok(ToClipExportDto(progress.Snapshot())))
          .Produces<ClipExportProgressDto>(StatusCodes.Status200OK)
          .WithName("GetClipExportProgress");

        // POST /api/clips-export/stop — ask the running export to stop after the
        // current clip (already-exported clips stay exported).
        api.MapPost("/clips-export/stop", (ClipExportProgress progress) =>
        {
            progress.RequestStop();
            return Results.Ok(ToClipExportDto(progress.Snapshot()));
        }).Produces<ClipExportProgressDto>(StatusCodes.Status200OK)
          .WithName("StopClipExport");

        // === Remove blocked sections (issue #70) ============================

        // GET /api/remove-blocks/queue — videos with "Hide" blocks that can be
        // trimmed into a new file. VideoBlocks are a JSON column, so the Hide
        // filter is applied in memory after loading the (non-clip) rows.
        api.MapGet("/remove-blocks/queue", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Videos.AsNoTracking()
                .Where(v => v.ParentVideoId == null)
                .Select(v => new { v.Id, v.FileName, v.Duration, v.VideoBlocks })
                .ToListAsync(ct);

            var items = rows
                .Where(r => r.VideoBlocks.Any(b => b.VideoBlockType == Domain.Models.VideoBlockTypes.Hide))
                .Select(r => new BlockRemovalQueueItemDto(
                    r.Id, r.FileName, r.Duration.TotalSeconds,
                    r.VideoBlocks
                        .Where(b => b.VideoBlockType == Domain.Models.VideoBlockTypes.Hide)
                        .OrderBy(b => b.OffsetInSeconds)
                        .Select(b => new VideoBlockDto(b.OffsetInSeconds, b.LengthInSeconds,
                            (Shared.Dto.VideoBlockTypes)(int)b.VideoBlockType))
                        .ToList()))
                .OrderBy(i => i.FileName)
                .ToList();
            return Results.Ok(items);
        }).Produces<List<BlockRemovalQueueItemDto>>(StatusCodes.Status200OK)
          .WithName("GetBlockRemovalQueue");

        // POST /api/remove-blocks — start a background run that builds a trimmed
        // file (Hide sections removed) for each video. 202 / 409 / 400.
        api.MapPost("/remove-blocks", async (
            RemoveBlocksRequest req, BlockRemovalService remover,
            BlockRemovalProgress progress, CancellationToken ct) =>
        {
            var ids = req.VideoIds ?? Array.Empty<Guid>();
            if (ids.Count == 0) return Results.BadRequest(new { error = "No videos selected." });

            var result = await remover.TryStartAsync(ids, ct);
            return result switch
            {
                BlockRemovalService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "A block-removal run is already going." }),
                BlockRemovalService.StartResult.NothingToDo =>
                    Results.BadRequest(new { error = "None of the selected videos have hidden sections." }),
                _ => Results.Json(ToBlockRemovalDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<BlockRemovalProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartBlockRemoval");

        // GET /api/remove-blocks — poll the run's progress.
        api.MapGet("/remove-blocks", (BlockRemovalProgress progress) =>
            Results.Ok(ToBlockRemovalDto(progress.Snapshot())))
          .Produces<BlockRemovalProgressDto>(StatusCodes.Status200OK)
          .WithName("GetBlockRemovalProgress");

        // POST /api/remove-blocks/stop — stop after the current video.
        api.MapPost("/remove-blocks/stop", (BlockRemovalProgress progress) =>
        {
            progress.RequestStop();
            return Results.Ok(ToBlockRemovalDto(progress.Snapshot()));
        }).Produces<BlockRemovalProgressDto>(StatusCodes.Status200OK)
          .WithName("StopBlockRemoval");

        // === Repair unplayable videos (issue #165) ==========================

        // POST /api/repair — re-encode the given videos to browser-friendly
        // H.264, producing a "<stem>_repaired.mp4" per video. 202 / 409 / 400.
        api.MapPost("/repair", async (
            RepairRequest req, RepairService repairer, RepairProgress progress,
            CancellationToken ct) =>
        {
            var ids = req.VideoIds ?? Array.Empty<Guid>();
            if (ids.Count == 0) return Results.BadRequest(new { error = "No videos selected." });

            var result = await repairer.TryStartAsync(ids, ct);
            return result switch
            {
                RepairService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "A repair run is already going." }),
                RepairService.StartResult.NothingToDo =>
                    Results.BadRequest(new { error = "None of the selected videos can be repaired." }),
                _ => Results.Json(ToRepairDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<RepairProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartRepair");

        // GET /api/repair — poll the repair run's progress.
        api.MapGet("/repair", (RepairProgress progress) =>
            Results.Ok(ToRepairDto(progress.Snapshot())))
          .Produces<RepairProgressDto>(StatusCodes.Status200OK)
          .WithName("GetRepairProgress");

        // POST /api/repair/stop — stop after the current video.
        api.MapPost("/repair/stop", (RepairProgress progress) =>
        {
            progress.RequestStop();
            return Results.Ok(ToRepairDto(progress.Snapshot()));
        }).Produces<RepairProgressDto>(StatusCodes.Status200OK)
          .WithName("StopRepair");

        // === Join (concatenate) videos (issue #163) =========================

        // POST /api/join — concatenate the given videos (in order) into one new
        // file. 202 / 409 (already running) / 400 (need ≥2).
        api.MapPost("/join", async (
            JoinRequest req, JoinService joiner, JoinProgress progress, CancellationToken ct) =>
        {
            var ids = req.VideoIds ?? Array.Empty<Guid>();
            if (ids.Count < 2) return Results.BadRequest(new { error = "Select at least two videos to join." });

            var result = await joiner.TryStartAsync(ids, req.Reencode, req.Name, ct);
            return result switch
            {
                JoinService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "A join is already running." }),
                JoinService.StartResult.NotEnough =>
                    Results.BadRequest(new { error = "Need at least two existing videos to join." }),
                _ => Results.Json(ToJoinDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<JoinProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartJoin");

        // GET /api/join — poll the join run's progress.
        api.MapGet("/join", (JoinProgress progress) =>
            Results.Ok(ToJoinDto(progress.Snapshot())))
          .Produces<JoinProgressDto>(StatusCodes.Status200OK)
          .WithName("GetJoinProgress");

        // POST /api/join/stop — stop the run.
        api.MapPost("/join/stop", (JoinProgress progress) =>
        {
            progress.RequestStop();
            return Results.Ok(ToJoinDto(progress.Snapshot()));
        }).Produces<JoinProgressDto>(StatusCodes.Status200OK)
          .WithName("StopJoin");

        // === Encode/convert to a profile (issue #164) =======================

        // POST /api/encode — encode the given videos to the configured profile
        // ("<stem>_encoded.mp4" each). 202 / 409 / 400.
        api.MapPost("/encode", async (
            EncodeRequest req, EncodeService encoder, EncodeProgress progress, CancellationToken ct) =>
        {
            var ids = req.VideoIds ?? Array.Empty<Guid>();
            if (ids.Count == 0) return Results.BadRequest(new { error = "No videos selected." });

            var result = await encoder.TryStartAsync(ids, ct);
            return result switch
            {
                EncodeService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "An encode is already running." }),
                EncodeService.StartResult.NothingToDo =>
                    Results.BadRequest(new { error = "None of the selected videos can be encoded." }),
                _ => Results.Json(ToEncodeDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<EncodeProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartEncode");

        // GET /api/encode — poll the encode run's progress.
        api.MapGet("/encode", (EncodeProgress progress) =>
            Results.Ok(ToEncodeDto(progress.Snapshot())))
          .Produces<EncodeProgressDto>(StatusCodes.Status200OK)
          .WithName("GetEncodeProgress");

        // POST /api/encode/stop — stop after the current video.
        api.MapPost("/encode/stop", (EncodeProgress progress) =>
        {
            progress.RequestStop();
            return Results.Ok(ToEncodeDto(progress.Snapshot()));
        }).Produces<EncodeProgressDto>(StatusCodes.Status200OK)
          .WithName("StopEncode");

        // === Optimize for streaming (faststart) (issue #166) ================

        // POST /api/optimize-streaming — faststart-remux the given videos in
        // place (skips already-optimized / non-MP4). 202 / 409 / 400.
        api.MapPost("/optimize-streaming", async (
            OptimizeStreamingRequest req, StreamingOptimizeService optimizer,
            StreamingOptimizeProgress progress, CancellationToken ct) =>
        {
            var ids = req.VideoIds ?? Array.Empty<Guid>();
            if (ids.Count == 0) return Results.BadRequest(new { error = "No videos selected." });

            var result = await optimizer.TryStartAsync(ids, ct);
            return result switch
            {
                StreamingOptimizeService.StartResult.AlreadyRunning =>
                    Results.Conflict(new { error = "An optimize run is already going." }),
                StreamingOptimizeService.StartResult.NothingToDo =>
                    Results.BadRequest(new { error = "None of the selected videos can be optimized." }),
                _ => Results.Json(ToOptimizeDto(progress.Snapshot()), statusCode: 202),
            };
        }).Produces<StreamingOptimizeProgressDto>(StatusCodes.Status202Accepted)
          .WithName("StartOptimizeStreaming");

        // GET /api/optimize-streaming — poll the optimize run's progress.
        api.MapGet("/optimize-streaming", (StreamingOptimizeProgress progress) =>
            Results.Ok(ToOptimizeDto(progress.Snapshot())))
          .Produces<StreamingOptimizeProgressDto>(StatusCodes.Status200OK)
          .WithName("GetOptimizeStreamingProgress");

        // POST /api/optimize-streaming/stop — stop after the current video.
        api.MapPost("/optimize-streaming/stop", (StreamingOptimizeProgress progress) =>
        {
            progress.RequestStop();
            return Results.Ok(ToOptimizeDto(progress.Snapshot()));
        }).Produces<StreamingOptimizeProgressDto>(StatusCodes.Status200OK)
          .WithName("StopOptimizeStreaming");

        api.MapGet("/videos/marked-for-deletion", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var videos = await IncludeForVideoDto(db.Videos)
                .AsNoTracking()
                .Where(v => v.MarkedForDeletion)
                .OrderBy(v => v.FilePath)
                .ToListAsync(ct);
            return Results.Ok(videos.Select(ToDto).ToList());
        }).Produces<List<VideoDto>>(StatusCodes.Status200OK)
          .WithName("GetMarkedForDeletionVideos");

        // GET /api/videos/purge-clip-warnings — parents marked for deletion that
        // still have embedded (non-exported, non-deleted) clips (#174). Purging
        // them loses those clip ranges, so the Purge page warns + offers export.
        api.MapGet("/videos/purge-clip-warnings", async (
            VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var marked = await db.Videos.AsNoTracking()
                .Where(v => v.MarkedForDeletion && v.ParentVideoId == null)
                .Select(v => new { v.Id, v.FileName })
                .ToListAsync(ct);
            if (marked.Count == 0)
                return Results.Ok(new List<PurgeClipWarningDto>());

            var ids = marked.Select(m => m.Id).ToList();
            var counts = await db.Videos.AsNoTracking()
                .Where(c => c.ParentVideoId != null && ids.Contains(c.ParentVideoId.Value)
                    && !c.ClipExported && !c.MarkedForDeletion
                    && c.ClipStartSeconds != null && c.ClipEndSeconds != null)
                .GroupBy(c => c.ParentVideoId!.Value)
                .Select(g => new { ParentId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var countById = counts.ToDictionary(x => x.ParentId, x => x.Count);

            var result = marked
                .Where(m => countById.ContainsKey(m.Id))
                .Select(m => new PurgeClipWarningDto(m.Id, m.FileName, countById[m.Id]))
                .OrderBy(w => w.FileName)
                .ToList();
            return Results.Ok(result);
        }).Produces<List<PurgeClipWarningDto>>(StatusCodes.Status200OK)
          .WithName("GetPurgeClipWarnings");

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
        }).Produces<List<VideoDto>>(StatusCodes.Status200OK)
          .WithName("GetPlaybackIssueVideos");

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
            if (!IsLocalRequest(http)) return Results.StatusCode(403);

            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
                return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(video.FilePath);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.StatusCode(403);

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
            if (!IsLocalRequest(http)) return Results.StatusCode(403);

            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
                return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(video.FilePath);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.StatusCode(403);

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
            if (!IsLocalRequest(http)) return Results.StatusCode(403);

            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return Results.NotFound();
            if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
                return Results.NotFound();

            var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
            var fullPath = Path.GetFullPath(video.FilePath);
            if (!enabledRoots.Any(r => fullPath.StartsWith(Path.GetFullPath(r), StringComparison.Ordinal)))
                return Results.StatusCode(403);

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
        }).Produces<FfprobeResultDto>(StatusCodes.Status200OK)
          .WithName("RunFfprobeOnVideo");

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
                return Results.StatusCode(403);

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
                return Results.StatusCode(403);

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
        }).Produces<List<VideoDto>>(StatusCodes.Status200OK)
          .WithName("GetVideosByFolder");

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
                return Results.StatusCode(403);

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
                return Results.StatusCode(403);

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
        MapTagGroupEndpoints(api);

        // === Tags ===========================================================
        MapTagEndpoints(api);

        // === Property definitions ===========================================
        MapPropertyEndpoints(api);

        // === Playlists ======================================================
        MapPlaylistEndpoints(api);

        // === Duplicates =====================================================
        // User-flagged "these two videos might be the same content" pairs.
        // Created from the browse page's duplicate-hunt flow, reviewed on
        // the /duplicates page (confirm / reject / reopen / delete).
        MapDuplicateEndpoints(api);

        // === Import =========================================================

        api.MapGet("/worker-pause-status", (WorkerPauseStatus pause) =>
        {
            return Results.Ok(new
            {
                importPaused = pause.ImportPaused,
                thumbnailsPaused = pause.ThumbnailsPaused,
                md5Paused = pause.Md5Paused,
            });
        }).WithName("GetWorkerPauseStatus");

        MapImportEndpoints(api);
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
