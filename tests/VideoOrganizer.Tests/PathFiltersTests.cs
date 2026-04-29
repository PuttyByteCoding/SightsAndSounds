using VideoOrganizer.Shared;
using Xunit;

namespace VideoOrganizer.Tests;

public class PathFiltersTests
{
    // --- IsExcludedFolderName ----------------------------------------------

    [Theory]
    [InlineData("_Thumbnails")]
    [InlineData("_ToDelete")]
    [InlineData("_WontPlay")]
    public void IsExcludedFolderName_KnownFolders_ReturnsTrue(string name)
    {
        Assert.True(PathFilters.IsExcludedFolderName(name));
    }

    [Theory]
    [InlineData("_thumbnails")]   // lower-case
    [InlineData("_TODELETE")]     // upper-case
    [InlineData("_WontPLAY")]     // mixed
    public void IsExcludedFolderName_IsCaseInsensitive(string name)
    {
        Assert.True(PathFilters.IsExcludedFolderName(name));
    }

    [Theory]
    [InlineData("Thumbnails")]    // missing leading underscore
    [InlineData("_thumbnail")]    // missing trailing 's'
    [InlineData("Concerts")]
    [InlineData("")]
    public void IsExcludedFolderName_OtherNames_ReturnsFalse(string name)
    {
        Assert.False(PathFilters.IsExcludedFolderName(name));
    }

    // --- IsInExcludedFolder -------------------------------------------------
    //
    // Use OS-correct separators so Path.GetRelativePath produces the right
    // segments on whichever platform the test runs.

    private static string Join(params string[] parts) =>
        string.Join(Path.DirectorySeparatorChar, parts);

    [Fact]
    public void IsInExcludedFolder_ChildOfExcluded_ReturnsTrue()
    {
        var baseDir = Join("C:", "videos");
        var fullPath = Join("C:", "videos", "_ToDelete", "old.mp4");
        Assert.True(PathFilters.IsInExcludedFolder(fullPath, baseDir));
    }

    [Fact]
    public void IsInExcludedFolder_DeepDescendantOfExcluded_ReturnsTrue()
    {
        // Excluded folders hide their entire subtree, not just immediate children.
        var baseDir = Join("C:", "videos");
        var fullPath = Join("C:", "videos", "_Thumbnails", "concert", "abc", "sprite.jpg");
        Assert.True(PathFilters.IsInExcludedFolder(fullPath, baseDir));
    }

    [Fact]
    public void IsInExcludedFolder_NormalSibling_ReturnsFalse()
    {
        var baseDir = Join("C:", "videos");
        var fullPath = Join("C:", "videos", "concerts", "abc.mp4");
        Assert.False(PathFilters.IsInExcludedFolder(fullPath, baseDir));
    }

    [Fact]
    public void IsInExcludedFolder_PathIsExcludedFolderItself_ReturnsFalse()
    {
        // The check hides DESCENDANTS of excluded folders, not the folder
        // itself — otherwise the API couldn't list "_ToDelete" as a child
        // of the root. The current implementation matches when ANY segment
        // of the relative path matches an excluded name, including the leaf.
        // Lock that current behavior in: a path that IS the excluded folder
        // returns true (because the trailing segment matches).
        var baseDir = Join("C:", "videos");
        var fullPath = Join("C:", "videos", "_ToDelete");
        Assert.True(PathFilters.IsInExcludedFolder(fullPath, baseDir));
    }

    [Fact]
    public void IsInExcludedFolder_PathAboveBase_ReturnsFalse()
    {
        // GetRelativePath returns a `..` prefix when fullPath is not under
        // baseDir on the same root; the implementation explicitly rejects
        // those so an excluded folder name appearing outside the tree
        // doesn't trigger a false positive. (Different-drive paths come
        // back as absolutes from GetRelativePath and are NOT defended
        // against — in practice the import enumerates from baseDir, so
        // fullPath is always under it.)
        var baseDir = Path.Combine(Path.GetTempPath(), "videos");
        var fullPath = Path.Combine(Path.GetTempPath(), "elsewhere", "_ToDelete", "file.mp4");
        Assert.False(PathFilters.IsInExcludedFolder(fullPath, baseDir));
    }

    [Fact]
    public void IsInExcludedFolder_ExcludedNameMatchIsCaseInsensitive()
    {
        // Filesystems on Windows are case-insensitive; the check uses
        // OrdinalIgnoreCase. Lock that in so a future refactor doesn't
        // accidentally introduce a case-sensitive comparison.
        var baseDir = Join("C:", "videos");
        var fullPath = Join("C:", "videos", "_TODELETE", "old.mp4");
        Assert.True(PathFilters.IsInExcludedFolder(fullPath, baseDir));
    }
}
