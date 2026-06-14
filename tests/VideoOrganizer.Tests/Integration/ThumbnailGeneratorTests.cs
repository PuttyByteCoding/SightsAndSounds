using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared.Configuration;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Exercises the real ffmpeg frame extraction + ImageSharp sprite compositing
/// in ThumbnailGenerator against a synthetic clip — previously untested, and the
/// subsystem behind the "missing thumbnails" issues.
/// </summary>
[Collection("SyntheticMedia")]
public sealed class ThumbnailGeneratorTests
{
    private readonly SyntheticMediaFixture _media;

    public ThumbnailGeneratorTests(SyntheticMediaFixture media) => _media = media;

    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), "sas-thumb-test-" + Guid.NewGuid().ToString("N"));

    private static ThumbnailGenerator NewGenerator(string cacheDir) =>
        new(NullLogger<ThumbnailGenerator>.Instance,
            new VideoStorageOptions { ThumbnailsDirectory = cacheDir });

    [SkippableFact]
    public async Task Generates_sprite_and_vtt_from_real_video()
    {
        Skip.IfNot(_media.FfmpegAvailable, _media.SkipReason);
        var cacheDir = NewCacheDir();
        try
        {
            var generator = NewGenerator(cacheDir);
            var videoId = Guid.NewGuid();

            var (spritePath, vtt) = await generator.GenerateThumbnailsAsync(
                _media.Clip(ClipKind.H264).Path, videoId);

            Assert.True(File.Exists(spritePath), "sprite.jpg should be written to disk");
            Assert.True(new FileInfo(spritePath).Length > 0, "sprite.jpg should be non-empty");
            Assert.Contains("WEBVTT", vtt);
            Assert.Contains("sprite.jpg#xywh=", vtt);

            // The exact lookup the serving endpoint uses must now resolve.
            Assert.Equal(spritePath, generator.GetSpriteImagePath(videoId));

            // And the sprite is a genuinely decodable image, not just bytes.
            using var img = await Image.LoadAsync(spritePath);
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        finally
        {
            TryDelete(cacheDir);
        }
    }

    [SkippableFact]
    public async Task Second_call_reuses_cached_sprite()
    {
        Skip.IfNot(_media.FfmpegAvailable, _media.SkipReason);
        var cacheDir = NewCacheDir();
        try
        {
            var generator = NewGenerator(cacheDir);
            var videoId = Guid.NewGuid();
            var clipPath = _media.Clip(ClipKind.H264).Path;

            var first = await generator.GenerateThumbnailsAsync(clipPath, videoId);
            var second = await generator.GenerateThumbnailsAsync(clipPath, videoId);

            Assert.Equal(first.spriteImagePath, second.spriteImagePath);
            Assert.Equal(first.vttContent, second.vttContent);
        }
        finally
        {
            TryDelete(cacheDir);
        }
    }

    [SkippableFact]
    public void Missing_sprite_resolves_to_empty_path()
    {
        Skip.IfNot(_media.FfmpegAvailable, _media.SkipReason);
        var cacheDir = NewCacheDir();
        try
        {
            // No generation has happened, so the lookup must report "not found"
            // (empty string) rather than a path that 404s downstream.
            Assert.Equal(string.Empty, NewGenerator(cacheDir).GetSpriteImagePath(Guid.NewGuid()));
        }
        finally
        {
            TryDelete(cacheDir);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
