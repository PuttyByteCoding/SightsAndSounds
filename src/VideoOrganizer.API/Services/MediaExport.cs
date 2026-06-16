using System.Diagnostics;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
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

    // "<dir>/<sanitized name><ext>" (ext + dir taken from the source), then
    // "-2", "-3"… on collision. For user-named exports (#173). Strips path
    // separators and characters illegal in a file name; falls back to "clip".
    public static string ComputeNamedOutputPath(string sourcePath, string baseName)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var ext = Path.GetExtension(sourcePath);

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((baseName ?? "")
            .Where(c => !invalid.Contains(c) && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
            .ToArray()).Trim().Trim('.');
        // Don't let a name re-introduce the source extension twice.
        if (ext.Length > 0 && cleaned.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^ext.Length];
        if (cleaned.Length == 0) cleaned = "clip";

        for (var i = 1; ; i++)
        {
            var name = i == 1 ? cleaned : $"{cleaned}-{i}";
            var candidate = Path.Combine(dir, $"{name}{ext}");
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
