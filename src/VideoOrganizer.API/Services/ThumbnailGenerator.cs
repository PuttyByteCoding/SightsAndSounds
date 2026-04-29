using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
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

    // Target ~25 frames across the whole video, with a minimum 2s spacing so
    // very short clips don't produce a pile of near-duplicate frames.
    private const int TargetFrameCount = 25;
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

        // Pick interval so we target 25 frames, but no tighter than MinIntervalSeconds.
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

        // Hard ceiling — defends against a short MinInterval producing too many
        // frames if the override kicks in with a pathological value.
        thumbnailCount = Math.Min(thumbnailCount, TargetFrameCount);

        var thumbnailPaths = new List<string>();
        var vttBuilder = new System.Text.StringBuilder();
        vttBuilder.AppendLine("WEBVTT");
        vttBuilder.AppendLine();

        // Track partial-success scenarios so the completion log can flag
        // a sprite that was generated with gaps. Without these counters
        // a video with half its frames missing looks identical to a
        // clean run in Seq.
        var extractionFailures = 0;
        var assemblyFailures = 0;
        var assemblyMissingFiles = 0;

        // Extract thumbnails
        for (int i = 0; i < thumbnailCount; i++)
        {
            var timestamp = TimeSpan.FromSeconds(i * intervalSeconds);
            var thumbnailPath = Path.Combine(videoDir, $"thumb_{i:D4}.jpg");
            thumbnailPaths.Add(thumbnailPath);

            try
            {
                var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(
                    videoPath,
                    thumbnailPath,
                    timestamp);

                // Pass `ct` so cancellation (timeout / user skip) actually
                // kills the ffmpeg process. Without this, a hung ffmpeg
                // ignored CancelAfter and ran forever.
                await conversion
                    .AddParameter($"-s {thumbnailWidth}x{thumbnailHeight}")
                    .Start(ct);

                _logger.LogDebug("Generated thumbnail {Index}/{Total} at {Timestamp}", i + 1, thumbnailCount, timestamp);
            }
            catch (OperationCanceledException)
            {
                // Bubble out of the loop so the outer service's catch can
                // attribute this to timeout vs user-skip.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate thumbnail {Index}/{Total} at {Timestamp} for video {VideoId}",
                    i + 1, thumbnailCount, timestamp, videoId);
                extractionFailures++;
            }
        }

        // Create sprite image
        var rows = (int)Math.Ceiling((double)thumbnailCount / columns);
        var spriteWidth = thumbnailWidth * columns;
        var spriteHeight = thumbnailHeight * rows;

        using (var spriteImage = new Image<Rgb24>(spriteWidth, spriteHeight))
        {
            spriteImage.Mutate(ctx => ctx.BackgroundColor(Color.Black));

            for (int i = 0; i < thumbnailPaths.Count; i++)
            {
                if (!File.Exists(thumbnailPaths[i]))
                {
                    // ffmpeg said success but the output isn't on disk —
                    // rare but worth knowing about because the sprite will
                    // have a black tile here.
                    assemblyMissingFiles++;
                    continue;
                }

                try
                {
                    using (var thumbnail = await Image.LoadAsync<Rgb24>(thumbnailPaths[i], ct))
                    {
                        var col = i % columns;
                        var row = i / columns;
                        var x = col * thumbnailWidth;
                        var y = row * thumbnailHeight;

                        spriteImage.Mutate(ctx => ctx.DrawImage(thumbnail, new Point(x, y), 1f));

                        // Generate VTT entry
                        var startTime = TimeSpan.FromSeconds(i * intervalSeconds);
                        var endTime = TimeSpan.FromSeconds((i + 1) * intervalSeconds);

                        vttBuilder.AppendLine($"{FormatVttTime(startTime)} --> {FormatVttTime(endTime)}");
                        vttBuilder.AppendLine($"sprite.jpg#xywh={x},{y},{thumbnailWidth},{thumbnailHeight}");
                        vttBuilder.AppendLine();
                    }

                    // Clean up individual thumbnail. A delete failure here
                    // (file in use, AV scan, etc.) doesn't break the sprite,
                    // but it leaks a temp file — log so disk-bloat is debuggable.
                    try
                    {
                        File.Delete(thumbnailPaths[i]);
                    }
                    catch (Exception delEx)
                    {
                        _logger.LogWarning(delEx,
                            "Failed to delete temp thumbnail {Path} for video {VideoId} (sprite still generated correctly)",
                            thumbnailPaths[i], videoId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process thumbnail {Index} ({Path}) for video {VideoId}",
                        i, thumbnailPaths[i], videoId);
                    assemblyFailures++;
                }
            }

            await spriteImage.SaveAsJpegAsync(spriteImagePath, ct);
        }

            var vttContent = vttBuilder.ToString();
            await File.WriteAllTextAsync(vttFilePath, vttContent, ct);

            // Promote the completion log to Warning when any frame went
            // missing — the sprite is still usable but visually patchy, and
            // a regression that takes the failure rate from 0 to N% needs
            // to be greppable.
            var totalFailed = extractionFailures + assemblyFailures + assemblyMissingFiles;
            if (totalFailed > 0)
            {
                _logger.LogWarning(
                    "Generated sprite + VTT for video {VideoId} with gaps — {Generated}/{Total} frames usable ({ExtractionFailures} ffmpeg failures, {AssemblyFailures} assembly failures, {MissingFiles} missing files). Sprite will have black tiles.",
                    videoId, thumbnailCount - totalFailed, thumbnailCount,
                    extractionFailures, assemblyFailures, assemblyMissingFiles);
            }
            else
            {
                _logger.LogInformation(
                    "Generated sprite + VTT for video {VideoId} ({FrameCount} frames at {IntervalSeconds}s spacing)",
                    videoId, thumbnailCount, intervalSeconds);
            }

            return (spriteImagePath, vttContent);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private string FormatVttTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
    }

    public string GetSpriteImagePath(Guid videoId)
    {
        var spriteImagePath = Path.Combine(_thumbnailCacheDir, videoId.ToString(), "sprite.jpg");
        return File.Exists(spriteImagePath) ? spriteImagePath : string.Empty;
    }
}
