using FluentAssertions;
using VideoOrganizer.Shared.Helpers;
using Xunit;

namespace VideoOrganizer.UnitTests.Helpers;

// First test class — proves the xUnit + FluentAssertions + ProjectRef
// wiring works end-to-end. Also covers the three escape characters
// the helper has to handle for Postgres LIKE / ILIKE:
//   · backslash (the escape character itself — has to be doubled)
//   · %         (the wildcard)
//   · _         (the single-char wildcard)
//
// Anything else passes through unchanged — Unicode, spaces, quotes,
// punctuation. The Theory at the bottom asserts each in isolation
// so a regression points at exactly which character broke.
public class SqlHelpersTests
{
    [Fact]
    public void EscapeLikePattern_LeavesEmptyStringEmpty()
    {
        SqlHelpers.EscapeLikePattern(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void EscapeLikePattern_LeavesPlainAsciiUntouched()
    {
        // Plain alphanumerics + spaces have no meaning to LIKE; the
        // helper must pass them through untouched.
        SqlHelpers.EscapeLikePattern("Bob Marley Live 1979").Should().Be("Bob Marley Live 1979");
    }

    [Fact]
    public void EscapeLikePattern_EscapesPercent()
    {
        // % is the multi-char wildcard in LIKE/ILIKE. The helper must
        // backslash-escape it so a literal "50%" doesn't match
        // anything-anything.
        SqlHelpers.EscapeLikePattern("50%").Should().Be("50\\%");
    }

    [Fact]
    public void EscapeLikePattern_EscapesUnderscore()
    {
        // _ is the single-char wildcard. Common in file names ("set_1").
        SqlHelpers.EscapeLikePattern("set_1").Should().Be("set\\_1");
    }

    [Fact]
    public void EscapeLikePattern_EscapesBackslash()
    {
        // The escape character itself has to be doubled, or it'd
        // escape the *next* character in the user's string.
        SqlHelpers.EscapeLikePattern("a\\b").Should().Be("a\\\\b");
    }

    [Fact]
    public void EscapeLikePattern_HandlesAllThreeAtOnce()
    {
        // Belt + suspenders: a string with all three special chars
        // should round-trip with each one individually escaped.
        SqlHelpers.EscapeLikePattern("50%_off\\path")
            .Should().Be("50\\%\\_off\\\\path");
    }

    [Fact]
    public void EscapeLikePattern_OrderingIsBackslashFirst()
    {
        // Subtle: the implementation does backslash FIRST, then % and _.
        // If you did them in the other order, the backslashes added by
        // escaping % and _ would themselves get doubled by the
        // backslash pass — producing "\\\%" instead of "\%". This
        // single test would fail loudly if anyone "tidied up" the
        // order in SqlHelpers.cs.
        SqlHelpers.EscapeLikePattern("%").Should().Be("\\%");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    [InlineData("100%", "100\\%")]
    [InlineData("foo_bar", "foo\\_bar")]
    [InlineData("C:\\Users", "C:\\\\Users")]
    [InlineData("á é í ó ú", "á é í ó ú")]   // Unicode passes through
    [InlineData("'quoted'", "'quoted'")]    // SQL string quoting is the
                                            //   driver's problem, not ours
    public void EscapeLikePattern_TableDriven(string input, string expected)
    {
        SqlHelpers.EscapeLikePattern(input).Should().Be(expected);
    }
}
