using VideoOrganizer.Shared;
using Xunit;

namespace VideoOrganizer.Tests;

public class VideoFileExtensionsTests
{
    [Theory]
    [InlineData("concert.mp4")]
    [InlineData("concert.m4v")]
    [InlineData("concert.MP4")]      // case-insensitive
    [InlineData("concert.M4V")]
    [InlineData("path/to/video.mp4")]
    public void IsVideo_AcceptedExtensions_ReturnsTrue(string path)
    {
        Assert.True(VideoFileExtensions.IsVideo(path));
    }

    [Theory]
    [InlineData("concert.mkv")]
    [InlineData("concert.avi")]
    [InlineData("concert.mov")]
    [InlineData("concert.webm")]
    [InlineData("README.md")]
    [InlineData("noextension")]
    [InlineData("")]
    public void IsVideo_NonVideoExtensions_ReturnsFalse(string path)
    {
        // Important guarantee: the import service relies on this method
        // matching the API's browse counts, so anything that isn't an
        // explicit video extension must come back false.
        Assert.False(VideoFileExtensions.IsVideo(path));
    }

    [Fact]
    public void All_ExposesTheTwoSupportedExtensions()
    {
        // Public surface — the UI uses this list to render "supported
        // formats" hints. Pin the count so accidentally adding a new
        // entry surfaces in code review via a failing test.
        Assert.Equal(2, VideoFileExtensions.All.Count);
        Assert.Contains(".mp4", VideoFileExtensions.All);
        Assert.Contains(".m4v", VideoFileExtensions.All);
    }
}
