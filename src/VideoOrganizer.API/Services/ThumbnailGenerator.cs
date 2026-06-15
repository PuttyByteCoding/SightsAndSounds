using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using VideoOrganizer.Shared.Configuration;

namespace VideoOrganizer.API.Services;

public class ThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<ThumbnailGenerator> _logger;
    private readonly string _thumbnailCacheDir;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _generationLocks = new();

    public ThumbnailGenerator(ILogger<ThumbnailGenerator> logger, VideoStorageOptions storageOptions)
    {
        _logger = logger;
        _thumbnailCacheDir = !string.IsNullOrWhiteSpace(storageOptions.ThumbnailsDirectory)
            ? storageOptions.ThumbnailsDirectory
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "video-thumbnails");

        // Ensure cache directory exists
        Directory.CreateDirectory(_thumbnailCacheDir);
    }

    // Target ~15 frames across the whole video, with a minimum 2s spacing so
    // very short clips don't produce a pile of near-duplicate frames.
    private const int TargetFrameCount = 15;
    private const int MinIntervalSeconds = 2;

    // Pass intervalSeconds <= 0 to use the adaptive default (TargetFrameCount frames
    // spread across the duration, with a minimum spacing of MinIntervalSeconds).
    public async Task<(string spriteImagePath, string vttContent)> GenerateThumbnailsAsync(
        string videoPath,
        Guid videoId,
        int intervalSeconds = 0,
        int thumbnailWidth = 320,
        int thumbnailHeight = 180,
        int columns = 5,
        CancellationToken ct = default)
    {
        // Get or create a lock for this specific video
        var semaphore = _generationLocks.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(ct);
        try
        {
            var videoDir = Path.Combine(_thumbnailCacheDir, videoId.ToString());
            Directory.CreateDirectory(videoDir);

            var spriteImagePath = Path.Combine(videoDir, "sprite.jpg");
            var vttFilePath = Path.Combine(videoDir, "thumbnails.vtt");

            // Check if already generated
            if (File.Exists(spriteImagePath) && File.Exists(vttFilePath))
            {
                _logger.LogInformation("Using cached thumbnails for video {VideoId}", videoId);
                return (spriteImagePath, await File.ReadAllTextAsync(vttFilePath, ct));
            }

            _logger.LogInformation("Generating thumbnails for video {VideoId} at {VideoPath}", videoId, videoPath);

            var mediaInfo = await FFmpeg.GetMediaInfo(videoPath, ct);
            var duration = mediaInfo.Duration;

            // Pick interval so we target TargetFrameCount frames, but no tighter than MinIntervalSeconds.
            if (intervalSeconds <= 0)
            {
                intervalSeconds = Math.Max(MinIntervalSeconds, (int)Math.Ceiling(duration.TotalSeconds / TargetFrameCount));
            }
            var thumbnailCount = (int)(duration.TotalSeconds / intervalSeconds);

            if (thumbnailCount == 0)
            {
                _logger.LogWarning("Video too short to generate thumbnails: {Duration}s", duration.TotalSeconds);
                return (string.Empty, "WEBVTT\n\n");
            }

            // Hard ceiling — defends against a short MinInterval producing too many frames.
            thumbnailCount = Math.Min(thumbnailCount, TargetFrameCount);
            var rows = (int)Math.Ceiling((double)thumbnailCount / columns);

            // Single ffmpeg pass replaces the old per-frame extraction + image-library
            // compositing (drops the SixLabors.ImageSharp dependency, issue #109):
            //   fps=1/interval  — one frame every `interval` seconds (frame i ≈ i*interval)
            //   scale=w:h       — each frame to the tile size
            //   tile=cols x rows — arranged left-to-right, top-to-bottom into one image
            // -frames:v 1 emits exactly one tiled sprite. Cells beyond the available
            // frames are left as the tile filter's default (black) padding, matching
            // the previous behaviour.
            var filter = $"fps=1/{intervalSeconds},scale={thumbnailWidth}:{thumbnailHeight},tile={columns}x{rows}";
            await RunFfmpegTileAsync(videoPath, filter, spriteImagePath, ct);

            if (!File.Exists(spriteImagePath))
            {
                throw new InvalidOperationException($"ffmpeg did not produce a sprite for video {videoId}");
            }

            // WebVTT: one cue per tile, in the same left-to-right / top-to-bottom
            // order ffmpeg's tile filter lays them out, so cue i maps to frame i.
            var vttBuilder = new System.Text.StringBuilder();
            vttBuilder.AppendLine("WEBVTT");
            vttBuilder.AppendLine();
            for (int i = 0; i < thumbnailCount; i++)
            {
                var col = i % columns;
                var row = i / columns;
                var x = col * thumbnailWidth;
                var y = row * thumbnailHeight;

                var startTime = TimeSpan.FromSeconds(i * intervalSeconds);
                var endTime = TimeSpan.FromSeconds((i + 1) * intervalSeconds);

                vttBuilder.AppendLine($"{FormatVttTime(startTime)} --> {FormatVttTime(endTime)}");
                vttBuilder.AppendLine($"sprite.jpg#xywh={x},{y},{thumbnailWidth},{thumbnailHeight}");
                vttBuilder.AppendLine();
            }

            var vttContent = vttBuilder.ToString();
            await File.WriteAllTextAsync(vttFilePath, vttContent, ct);

            _logger.LogInformation(
                "Generated sprite + VTT for video {VideoId} ({FrameCount} frames at {IntervalSeconds}s spacing)",
                videoId, thumbnailCount, intervalSeconds);

            return (spriteImagePath, vttContent);
        }
        finally
        {
            semaphore.Release();
        }
    }

    // Runs `ffmpeg -i <video> -vf <filter> -frames:v 1 <output>` against the
    // ffmpeg binary Xabe is configured to use. Passing the CancellationToken
    // kills the process on timeout / user-skip so a hung ffmpeg can't run forever
    // (the warming service relies on this to bound per-video work).
    private static async Task RunFfmpegTileAsync(string videoPath, string filter, string outputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(filter);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg");
        using var kill = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        // ffmpeg writes the sprite to a file, not stdout; only stderr carries
        // progress/errors. Read it to EOF (process exit), then await exit.
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
            throw new InvalidOperationException($"ffmpeg tile failed (exit {proc.ExitCode}): {tail}");
        }
    }

    // ffmpeg lives where Xabe was pointed (Program.cs sets this to a bundled dir;
    // tests point it at the system binaries). Fall back to PATH if unset.
    private static string ResolveFfmpegPath()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dir = FFmpeg.ExecutablesPath;
        if (!string.IsNullOrEmpty(dir))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return exe;
    }

    private static string FormatVttTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
    }

    public string GetSpriteImagePath(Guid videoId)
    {
        var spriteImagePath = Path.Combine(_thumbnailCacheDir, videoId.ToString(), "sprite.jpg");
        return File.Exists(spriteImagePath) ? spriteImagePath : string.Empty;
    }
}
