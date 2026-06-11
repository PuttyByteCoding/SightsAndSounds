using VideoOrganizer.Shared;
using Xunit;

namespace VideoOrganizer.Tests;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\videos\concert.mp4", "C:/videos/concert.mp4")]
    [InlineData(@"\\server\share\file.mp4", "//server/share/file.mp4")]
    [InlineData(@"a\b\c\d", "a/b/c/d")]
    public void Normalize_ConvertsBackslashesToForwardSlashes(string input, string expected)
    {
        Assert.Equal(expected, PathNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("C:/videos/concert.mp4")]
    [InlineData("a/b/c")]
    [InlineData("")]
    public void Normalize_AlreadyForwardSlashedPath_IsUnchanged(string input)
    {
        // Idempotency matters: paths get round-tripped through StartsWith
        // comparisons in VideoSet lookups, so re-normalizing must be a no-op.
        Assert.Equal(input, PathNormalizer.Normalize(input));
        Assert.Equal(input, PathNormalizer.Normalize(PathNormalizer.Normalize(input)));
    }

    [Fact]
    public void Normalize_NullInput_ReturnedAsIs()
    {
        // Public API contract — the implementation guards against
        // NullReferenceException by returning the input untouched. Lock
        // that contract in so a future "string.IsNullOrEmpty" refactor
        // doesn't change it without us noticing.
        Assert.Null(PathNormalizer.Normalize(null!));
    }

    [Fact]
    public void Normalize_MixedSeparators_AllBecomeForward()
    {
        Assert.Equal("a/b/c/d", PathNormalizer.Normalize(@"a\b/c\d"));
    }
}
