using FluentAssertions;
using VideoOrganizer.Shared.Helpers;
using Xunit;

namespace VideoOrganizer.UnitTests.Helpers;

// Covers the pure path logic behind the file-move feature (issue #4):
// collision-free destination naming and same-volume detection.
public class MovePathHelpersTests
{
    [Fact]
    public void UniqueDestination_ReturnsDesired_WhenNothingExists()
    {
        var result = MovePathHelpers.UniqueDestination(
            "/media/dest/clip.mp4", _ => false);
        result.Should().Be("/media/dest/clip.mp4");
    }

    [Fact]
    public void UniqueDestination_AppendsCounter_OnCollision()
    {
        // The desired path exists; (2) is free. Assert on the file name +
        // directory rather than an exact joined string, so the test isn't
        // sensitive to OS separator normalization in Path.GetDirectoryName.
        var desired = "/media/dest/clip.mp4";
        var taken = new System.Collections.Generic.HashSet<string> { desired };
        var result = MovePathHelpers.UniqueDestination(desired, taken.Contains);
        System.IO.Path.GetFileName(result).Should().Be("clip (2).mp4");
        System.IO.Path.GetDirectoryName(result)
            .Should().Be(System.IO.Path.GetDirectoryName(desired));
    }

    [Fact]
    public void UniqueDestination_SkipsToFirstFreeCounter()
    {
        // Seed the taken set using the SAME directory form the helper
        // derives (GetDirectoryName + Combine) so the predicate matches its
        // candidates exactly regardless of platform separators.
        var desired = "/media/dest/clip.mp4";
        var dir = System.IO.Path.GetDirectoryName(desired)!;
        var taken = new System.Collections.Generic.HashSet<string>
        {
            desired,
            System.IO.Path.Combine(dir, "clip (2).mp4"),
            System.IO.Path.Combine(dir, "clip (3).mp4")
        };
        var result = MovePathHelpers.UniqueDestination(desired, taken.Contains);
        System.IO.Path.GetFileName(result).Should().Be("clip (4).mp4");
    }

    [Fact]
    public void IsSameVolume_TrueForSameRoot()
    {
        MovePathHelpers.IsSameVolume(@"C:\a\b\x.mp4", @"C:\c\d\y.mp4").Should().BeTrue();
    }

    [Fact]
    public void IsSameVolume_FalseForDifferentRoots()
    {
        // Skip on non-Windows where C:/D: aren't distinct volumes.
        if (System.IO.Path.DirectorySeparatorChar != '\\') return;
        MovePathHelpers.IsSameVolume(@"C:\a\x.mp4", @"D:\a\x.mp4").Should().BeFalse();
    }
}
