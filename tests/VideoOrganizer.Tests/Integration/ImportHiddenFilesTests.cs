using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Issue #62 (regression) — the import tool must not surface hidden entries:
/// dot-prefixed DIRECTORIES (.git, .cache) are excluded from the folder tree,
/// and files INSIDE them never appear in the importable / other / hidden
/// buckets. Top-level dotfiles still show on the dedicated Hidden tab.
/// Needs Docker (real Postgres); no ffmpeg required (extension-only listing).
/// </summary>
[Collection("PostgresApi")]
public sealed class ImportHiddenFilesTests
{
    private readonly PostgresApiFixture _api;

    public ImportHiddenFilesTests(PostgresApiFixture api) => _api = api;

    private sealed record FileList(
        string directoryPath,
        List<string> files,
        List<string> nonImportableFiles,
        List<string> importedFiles,
        List<string> hiddenFiles);

    private sealed record BrowseDir(string name);
    private sealed record Browse(string currentPath, string? parentPath, List<BrowseDir> directories);

    [SkippableFact]
    public async Task Hidden_dirs_and_their_contents_are_excluded_from_import_listings()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var dir = Path.Combine(Path.GetTempPath(), "sas-hidden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, ".cache"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        foreach (var (rel, _) in new[]
        {
            ("normal.mp4", ""), (".hidden.mp4", ""), (".DS_Store", ""), ("notes.txt", ""),
            (".git/config", ""), (".cache/cached.mp4", ""), ("sub/sub.mp4", ""),
        })
            await File.WriteAllTextAsync(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)), "x");

        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.VideoSets.Add(new VideoSet
                {
                    Id = Guid.NewGuid(), Name = "hidden-test", Path = dir, Enabled = true, SortOrder = 0,
                });
                await db.SaveChangesAsync();
            });

            var list = await _api.Client.GetFromJsonAsync<FileList>(
                $"/api/import/files?directoryPath={Uri.EscapeDataString(dir)}&includeSubdirectories=true");
            Assert.NotNull(list);

            string Leaf(string p) => Path.GetFileName(p);
            var importable = list!.files.Select(Leaf).ToList();
            var other = list.nonImportableFiles.Select(Leaf).ToList();
            var hidden = list.hiddenFiles.Select(Leaf).ToList();

            // Real videos importable; the video inside .cache is NOT.
            Assert.Contains("normal.mp4", importable);
            Assert.Contains("sub.mp4", importable);
            Assert.DoesNotContain("cached.mp4", importable);

            // .git/config does not leak into the "other" bucket.
            Assert.Contains("notes.txt", other);
            Assert.DoesNotContain("config", other);

            // Top-level dotfiles still surface on the Hidden tab.
            Assert.Contains(".hidden.mp4", hidden);
            Assert.Contains(".DS_Store", hidden);

            // Nothing anywhere should come from a hidden directory.
            var all = list.files.Concat(list.nonImportableFiles).Concat(list.hiddenFiles);
            Assert.DoesNotContain(all, p =>
                p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") ||
                p.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}"));

            // Folder tree hides dot-directories.
            var browse = await _api.Client.GetFromJsonAsync<Browse>(
                $"/api/import/browse?path={Uri.EscapeDataString(dir)}");
            var dirNames = browse!.directories.Select(d => d.name).ToList();
            Assert.Contains("sub", dirNames);
            Assert.DoesNotContain(".git", dirNames);
            Assert.DoesNotContain(".cache", dirNames);
        }
        finally
        {
            await _api.WithDbAsync(db => db.VideoSets.Where(s => s.Path == dir).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
