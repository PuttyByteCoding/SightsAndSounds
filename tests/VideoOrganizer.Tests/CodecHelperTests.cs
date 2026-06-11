using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using Xunit;

namespace VideoOrganizer.Tests;

public class CodecHelperTests
{
    [Theory]
    [InlineData("hevc", VideoCodec.HEVC)]
    [InlineData("h264", VideoCodec.H264)]
    [InlineData("h265", VideoCodec.H265)]
    public void GetVideoCodec_KnownLowercase_ReturnsEnum(string codec, VideoCodec expected)
    {
        Assert.Equal(expected, CodecHelper.GetVideoCodec(codec));
    }

    [Theory]
    [InlineData("HEVC", VideoCodec.HEVC)]
    [InlineData("H264", VideoCodec.H264)]
    [InlineData("h265", VideoCodec.H265)]
    [InlineData("Hevc", VideoCodec.HEVC)]
    public void GetVideoCodec_IsCaseInsensitive(string codec, VideoCodec expected)
    {
        // ffprobe's codec_name field is conventionally lowercase but the
        // helper guards against vendor variations by lowercasing first —
        // pin that so a refactor doesn't reintroduce case-sensitivity.
        Assert.Equal(expected, CodecHelper.GetVideoCodec(codec));
    }

    [Theory]
    [InlineData("vp9")]
    [InlineData("av1")]
    [InlineData("mpeg4")]
    [InlineData("")]
    public void GetVideoCodec_UnknownCodec_ReturnsOther(string codec)
    {
        Assert.Equal(VideoCodec.Other, CodecHelper.GetVideoCodec(codec));
    }
}
