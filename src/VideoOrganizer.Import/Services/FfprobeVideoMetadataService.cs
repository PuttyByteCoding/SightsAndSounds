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
        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
        _binariesEnsured = true;
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
