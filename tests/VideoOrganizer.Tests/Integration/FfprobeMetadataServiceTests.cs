using Microsoft.Extensions.Logging.Abstractions;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// End-to-end tests of the real ffprobe metadata path against synthetic clips,
/// including the flow into the domain helpers. These are the services that had
/// zero coverage before — and where the HEVC codec-detection bug lived.
/// </summary>
[Collection("SyntheticMedia")]
public sealed class FfprobeMetadataServiceTests
{
    private readonly SyntheticMediaFixture _media;

    public FfprobeMetadataServiceTests(SyntheticMediaFixture media) => _media = media;

    public static IEnumerable<object[]> AllGoodClips() =>
        new[] { ClipKind.H264, ClipKind.Hevc, ClipKind.Hd1080, ClipKind.Uhd2160 }
            .Select(k => new object[] { k });

    [SkippableTheory]
    [MemberData(nameof(AllGoodClips))]
    public async Task Probe_extracts_expected_metadata(ClipKind kind)
    {
        Skip.IfNot(_media.FfmpegAvailable, _media.SkipReason);
        var clip = _media.Clip(kind);
        var service = new FfprobeVideoMetadataService(NullLogger<FfprobeVideoMetadataService>.Instance);

        var meta = await service.GetMetadataAsync(clip.Path);

        Assert.NotNull(meta);
        Assert.Equal(clip.Width, meta!.Width);
        Assert.Equal(clip.Height, meta.Height);
        Assert.Equal(clip.Codec, meta.VideoCodec, ignoreCase: true);
        Assert.Equal(1, meta.VideoStreamCount);
        Assert.Equal(clip.AudioStreams, meta.AudioStreamCount);
        Assert.NotNull(meta.Duration);
        Assert.True(meta.Duration!.Value.TotalSeconds > 0, "probed duration should be positive");
    }

    [SkippableTheory]
    [MemberData(nameof(AllGoodClips))]
    public async Task Probed_metadata_maps_through_domain_helpers(ClipKind kind)
    {
        Skip.IfNot(_media.FfmpegAvailable, _media.SkipReason);
        var clip = _media.Clip(kind);
        var service = new FfprobeVideoMetadataService(NullLogger<FfprobeVideoMetadataService>.Instance);

        var meta = await service.GetMetadataAsync(clip.Path);
        Assert.NotNull(meta);

        // Real probe output → the helpers that classify it. Guards the HEVC
        // detection and the height/width dimension mapping against drift.
        Assert.Equal(clip.ExpectedCodec, CodecHelper.GetVideoCodec(meta!.VideoCodec!));
        Assert.Equal(
            clip.ExpectedFormat,
            VideoDimensionFormatHelper.GetDimensionFormat(meta.Height!.Value, meta.Width!.Value));
    }

    [SkippableFact]
    public async Task Probe_of_corrupt_file_returns_null()
    {
        Skip.IfNot(_media.FfmpegAvailable, _media.SkipReason);
        var service = new FfprobeVideoMetadataService(NullLogger<FfprobeVideoMetadataService>.Instance);

        var meta = await service.GetMetadataAsync(_media.CorruptPath);

        // The service swallows the ffprobe failure and returns null; the import
        // pipeline relies on this to skip unreadable files instead of crashing.
        Assert.Null(meta);
    }
}
