using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using Xunit;

namespace VideoOrganizer.Tests;

public class VideoDimensionFormatHelperTests
{
    // The mapping is a pure (height, width) → enum lookup. These cases pin
    // every entry so a future "swap UHD8k/UHD4K resolutions" typo in the
    // helper fails immediately.

    [Theory]
    [InlineData(4320, 7680, VideoDimensionFormat.UHD8k)]
    [InlineData(2160, 3840, VideoDimensionFormat.UHD4K)]
    [InlineData(3840, 2160, VideoDimensionFormat.VerticalUHD4k)]
    [InlineData(1080, 1920, VideoDimensionFormat.HD1080p)]
    [InlineData(1920, 1080, VideoDimensionFormat.Vertical1080p)]
    [InlineData(720, 1280, VideoDimensionFormat.HD720p)]
    [InlineData(1280, 720, VideoDimensionFormat.Vertical720p)]
    [InlineData(576, 768, VideoDimensionFormat.SD576p4x3)]
    [InlineData(576, 1024, VideoDimensionFormat.SD576p16x9)]
    [InlineData(480, 640, VideoDimensionFormat.SD480p4x3)]
    [InlineData(480, 854, VideoDimensionFormat.SD480p16x9)]
    public void GetDimensionFormat_KnownResolutions_ReturnsExactEnumMember(
        int height, int width, VideoDimensionFormat expected)
    {
        Assert.Equal(expected, VideoDimensionFormatHelper.GetDimensionFormat(height, width));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(123, 456)]
    [InlineData(1080, 1080)]      // square — close to known but not on the list
    [InlineData(1081, 1920)]      // off-by-one from HD1080p
    public void GetDimensionFormat_UnknownResolutions_FallsBackToNonStandard(int height, int width)
    {
        Assert.Equal(VideoDimensionFormat.NonStandard,
            VideoDimensionFormatHelper.GetDimensionFormat(height, width));
    }

    [Fact]
    public void GetDimensionFormat_OrderingMatters_HeightBeforeWidth()
    {
        // Regression guard: (1080, 1920) is HD1080p; (1920, 1080) is the
        // VERTICAL variant. Easy to swap the two in a refactor since the
        // tuple deconstruction is positional.
        Assert.Equal(VideoDimensionFormat.HD1080p,
            VideoDimensionFormatHelper.GetDimensionFormat(1080, 1920));
        Assert.Equal(VideoDimensionFormat.Vertical1080p,
            VideoDimensionFormatHelper.GetDimensionFormat(1920, 1080));
    }
}
