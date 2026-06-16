using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;
using Xabe.FFmpeg;

namespace VideoOrganizer.API.Services;

// Shared ffmpeg + ingest helpers for the file-producing features (issues #69
// clip export and #70 block removal): both stream-copy part of a parent file
// into a new standalone file and then register it as a brand-new top-level
// Video, exactly like a normal import.
public static class MediaExport
{
    // "<dir>/<stem><suffix><ext>", then "<suffix>-2", "<suffix>-3"… so a second
    // export from the same source never overwrites the first.
    public static string ComputeOutputPath(string sourcePath, string suffix)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        for (var i = 1; ; i++)
        {
            var s = i == 1 ? suffix : $"{suffix}-{i}";
            var candidate = Path.Combine(dir, $"{stem}{s}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // Build a fresh top-level Video for a produced file: ffprobe metadata +
    // import bookkeeping (Md5 deferred to the backfill worker, NeedsReview,
    // ImportJobId). Tags are applied by the caller.
    public static async Task<Video> BuildVideoFromFileAsync(
        IVideoMetadataService metadata, string path, Guid jobId, ILogger logger, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var video = new Video
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(path),
            FilePath = path,
            Md5 = null,
            FileSize = info.Length,
            IngestDate = DateTime.UtcNow,
            VideoQuality = VideoQuality.NotChecked,
            CameraType = CameraTypes.NotChecked,
            ImportJobId = jobId,
            NeedsReview = true,
        };

        try
        {
            using var metaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            metaCts.CancelAfter(TimeSpan.FromSeconds(30));
            var m = await metadata.GetMetadataAsync(path, metaCts.Token);
            if (m != null)
            {
                video.Duration = m.Duration ?? TimeSpan.Zero;
                video.Height = m.Height ?? 0;
                video.Width = m.Width ?? 0;
                video.VideoCodec = CodecHelper.GetVideoCodec(m.VideoCodec ?? string.Empty);
                video.Bitrate = m.VideoBitrate ?? 0;
                video.FrameRate = m.FrameRate ?? 0;
                video.PixelFormat = m.PixelFormat ?? string.Empty;
                video.Ratio = m.Ratio ?? string.Empty;
                video.CreationTime = m.CreationTime;
                video.VideoStreamCount = m.VideoStreamCount ?? 0;
                video.AudioStreamCount = m.AudioStreamCount ?? 0;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Timed out probing produced file {Path}", path);
        }
        video.VideoDimensionFormat = VideoDimensionFormatHelper.GetDimensionFormat(video.Height, video.Width);
        return video;
    }

    // Find an existing tag by name (any group) or create it in the named group
    // (creating the group too if needed). Used to stamp produced files: "Clip"
    // on exported clips (#69), "Trimmed" on block-removed files (#70).
    public static async Task<Guid> GetOrCreateTagAsync(
        VideoOrganizerDbContext db, string tagName, string groupName, CancellationToken ct)
    {
        var lowered = tagName.ToLower();
        var existing = await db.Tags
            .Where(t => t.Name.ToLower() == lowered)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty) return existing;

        var groupLower = groupName.ToLower();
        var group = await db.TagGroups.FirstOrDefaultAsync(g => g.Name.ToLower() == groupLower, ct);
        if (group is null)
        {
            group = new TagGroup { Id = Guid.NewGuid(), Name = groupName, AllowMultiple = true };
            db.TagGroups.Add(group);
        }
        var tag = new Tag { Id = Guid.NewGuid(), Name = tagName, TagGroupId = group.Id };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return tag.Id;
    }

    // Run ffmpeg with the given args (quiet/overwrite flags prepended). The
    // CancellationToken kills the process so a hung ffmpeg can't run forever.
    public static async Task RunFfmpegAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveTool("ffmpeg"),
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg");
        using var kill = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}): {tail}");
        }
    }

    // ffmpeg/ffprobe live where Xabe was pointed (Program.cs bundled dir; tests
    // point at system binaries). Fall back to PATH if unset.
    public static string ResolveTool(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var dir = FFmpeg.ExecutablesPath;
        if (!string.IsNullOrEmpty(dir))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return exe;
    }
}
