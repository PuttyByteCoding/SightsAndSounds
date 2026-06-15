using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

            // Two-phase generation, issue #111 (still no SixLabors.ImageSharp, #109):
            //
            //   Phase 1 — extract each frame with a fast *input* seek
            //     (`-ss <t>` before `-i`). This jumps straight to the timestamp
            //     instead of decoding the whole file, so cost is ~constant per
            //     frame regardless of video length. A single tile pass with
            //     `fps=1/interval` (the previous approach) decoded the entire
            //     video and was multiples slower on long clips.
            //
            //   Phase 2 — tile the small extracted JPEGs into the sprite with one
            //     more ffmpeg pass (`tile=cols x rows`), which is near-instant on
            //     320x180 inputs.
            var framePaths = await ExtractFramesAsync(
                videoPath, videoDir, thumbnailCount, intervalSeconds, thumbnailWidth, thumbnailHeight, ct);

            try
            {
                // `-framerate 1 -i _frame_%04d.jpg` feeds the frames (0..count-1, in
                // order) into the tile filter. Missing frames were backfilled with a
                // black placeholder so the sequence is contiguous; any cells past the
                // last frame stay black, matching the old behaviour.
                var framePattern = Path.Combine(videoDir, "_frame_%04d.jpg");
                await RunFfmpegAsync(ct,
                    "-framerate", "1", "-start_number", "0", "-i", framePattern,
                    "-vf", $"tile={columns}x{rows}", "-frames:v", "1", spriteImagePath);
            }
            finally
            {
                CleanupFrames(framePaths, videoDir);
            }

            if (!File.Exists(spriteImagePath))
            {
                throw new InvalidOperationException($"ffmpeg did not produce a sprite for video {videoId}");
            }

            // WebVTT: one cue per tile, in the same left-to-right / top-to-bottom
            // order the tile filter lays them out, so cue i maps to frame i.
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

    // Phase 1: extract `count` frames at i*interval via fast input seeks. Returns
    // the (contiguous, 0-based) frame file paths. A frame that fails to extract is
    // backfilled with a black placeholder so phase 2's image sequence has no gaps.
    private async Task<List<string>> ExtractFramesAsync(
        string videoPath, string videoDir, int count, int intervalSeconds,
        int width, int height, CancellationToken ct)
    {
        var framePaths = new List<string>(count);
        string? blackPlaceholder = null;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var framePath = Path.Combine(videoDir, $"_frame_{i:D4}.jpg");
            framePaths.Add(framePath);

            try
            {
                // `-ss` *before* `-i` = input seeking: fast jump to ~the timestamp.
                await RunFfmpegAsync(ct,
                    "-ss", (i * intervalSeconds).ToString(),
                    "-i", videoPath,
                    "-frames:v", "1", "-s", $"{width}x{height}", framePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract frame {Index} at {Seconds}s", i, i * intervalSeconds);
            }

            if (!File.Exists(framePath))
            {
                blackPlaceholder ??= await MakeBlackPlaceholderAsync(videoDir, width, height, ct);
                File.Copy(blackPlaceholder, framePath, overwrite: true);
            }
        }

        return framePaths;
    }

    private async Task<string> MakeBlackPlaceholderAsync(string videoDir, int width, int height, CancellationToken ct)
    {
        var path = Path.Combine(videoDir, "_black.jpg");
        await RunFfmpegAsync(ct,
            "-f", "lavfi", "-i", $"color=c=black:s={width}x{height}",
            "-frames:v", "1", path);
        return path;
    }

    private void CleanupFrames(IEnumerable<string> framePaths, string videoDir)
    {
        foreach (var p in framePaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* leaked temp frame; not fatal */ }
        }
        var black = Path.Combine(videoDir, "_black.jpg");
        try { if (File.Exists(black)) File.Delete(black); } catch { /* ignore */ }
    }

    // Runs ffmpeg with the given args (we always prepend the quiet/overwrite flags)
    // against the binary Xabe is configured to use. The CancellationToken kills the
    // process on timeout / user-skip so a hung ffmpeg can't run forever.
    private static async Task RunFfmpegAsync(CancellationToken ct, params string[] args)
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
