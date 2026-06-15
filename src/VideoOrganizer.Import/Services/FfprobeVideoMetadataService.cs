using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using Microsoft.Extensions.Logging;

namespace VideoOrganizer.Import.Services;

public class FfprobeVideoMetadataService : IVideoMetadataService
{
    private readonly ILogger<FfprobeVideoMetadataService> _logger;
    private static bool _binariesEnsured;

    public FfprobeVideoMetadataService(ILogger<FfprobeVideoMetadataService> logger)
    {
        _logger = logger;
    }

    private async Task EnsureBinariesAsync()
    {
        if (_binariesEnsured) return;
        // Skip the download when ffmpeg/ffprobe are already present where Xabe is
        // configured to look (Program.cs already fetches them into that dir at
        // startup). Besides saving a redundant fetch, this avoids the no-path
        // downloader's default target — a *file* at <BaseDir>/ffmpeg — colliding
        // with the *directory* the app and tests use there. Mirrors Program.cs.
        if (!BinariesPresent())
        {
            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
        }
        _binariesEnsured = true;
    }

    private static bool BinariesPresent()
    {
        var dir = FFmpeg.ExecutablesPath;
        if (string.IsNullOrEmpty(dir)) return false;
        var ext = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return File.Exists(Path.Combine(dir, "ffmpeg" + ext))
            && File.Exists(Path.Combine(dir, "ffprobe" + ext));
    }

    public async Task<VideoMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureBinariesAsync();
            var mediaInfo = await FFmpeg.GetMediaInfo(filePath, cancellationToken);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();

            return new VideoMetadata
            {
                Duration = mediaInfo.Duration,
                Width = videoStream?.Width,
                Height = videoStream?.Height,
                VideoCodec = videoStream?.Codec,
                CreationTime = mediaInfo.CreationTime,
                VideoStreamCount = mediaInfo.VideoStreams.Count(),
                VideoBitrate = videoStream?.Bitrate,
                FrameRate = videoStream?.Framerate,
                PixelFormat = videoStream?.PixelFormat,
                Ratio = videoStream?.Ratio,
                AudioStreamCount = mediaInfo.AudioStreams.Count(),
            };
        }
        catch (TaskCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error getting metadata for file: {FilePath}", filePath);
            return null;
        }
    }
}
