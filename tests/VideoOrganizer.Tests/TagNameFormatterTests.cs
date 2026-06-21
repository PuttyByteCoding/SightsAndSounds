using VideoOrganizer.Shared.Dto;
using VideoOrganizer.Shared.Helpers;
using Xunit;

namespace VideoOrganizer.Tests;

public class TagNameFormatterTests
{
    [Theory]
    [InlineData(TextFormatOption.NoFormatting, "live SHOW at The Venue", "live SHOW at The Venue")]
    [InlineData(TextFormatOption.AllLowercase, "Live SHOW", "live show")]
    [InlineData(TextFormatOption.AllUppercase, "Live show", "LIVE SHOW")]
    [InlineData(TextFormatOption.TitleCase, "live show", "Live Show")]
    [InlineData(TextFormatOption.TitleCase, "LIVE SHOW", "Live Show")]
    [InlineData(TextFormatOption.TitleCase, "mixED cAse", "Mixed Case")]
    public void Format_applies_the_option(TextFormatOption option, string input, string expected)
    {
        Assert.Equal(expected, TagNameFormatter.Format(input, option));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Format_passes_empty_through(string? input)
    {
        Assert.Equal(input, TagNameFormatter.Format(input!, TextFormatOption.TitleCase));
    }

    [Fact]
    public void TitleCase_preserves_internal_whitespace()
    {
        Assert.Equal("Two  Spaces", TagNameFormatter.Format("two  spaces", TextFormatOption.TitleCase));
    }
}
