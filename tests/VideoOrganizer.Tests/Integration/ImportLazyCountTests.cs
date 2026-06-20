using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Issue #197 — /import/browse returns the folder tree immediately WITHOUT the
/// slow recursive video count (VideoCount is null); the count is fetched lazily
/// per folder via /import/folder-count. Needs Docker (real Postgres); no ffmpeg
/// (listing is extension-only).
/// </summary>
[Collection("PostgresApi")]
public sealed class ImportLazyCountTests
{
    private readonly PostgresApiFixture _api;

    public ImportLazyCountTests(PostgresApiFixture api) => _api = api;

    private sealed record BrowseDir(string name, string fullPath, int? videoCount, int importedCount);
    private sealed record Browse(string currentPath, string? parentPath, List<BrowseDir> directories);
    private sealed record FolderCount(string path, int videoCount);

    [SkippableFact]
    public async Task Browse_omits_counts_and_folder_count_returns_them()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var dir = Path.Combine(Path.GetTempPath(), "sas-lazy-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "a.mp4"), "x");
        await File.WriteAllTextAsync(Path.Combine(sub, "b.mp4"), "x");
        await File.WriteAllTextAsync(Path.Combine(sub, "notes.txt"), "x");

        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.VideoSets.Add(new VideoSet
                {
                    Id = Guid.NewGuid(), Name = "lazy-test", Path = dir, Enabled = true, SortOrder = 0,
                });
                await db.SaveChangesAsync();
            });

            // Browse returns the "sub" folder with NO video count (lazy).
            var browse = await _api.Client.GetFromJsonAsync<Browse>(
                $"/api/import/browse?path={Uri.EscapeDataString(dir)}");
            var subDir = browse!.directories.Single(d => d.name == "sub");
            Assert.Null(subDir.videoCount);

            // The lazy per-folder endpoint returns the real recursive count.
            var fc = await _api.Client.GetFromJsonAsync<FolderCount>(
                $"/api/import/folder-count?path={Uri.EscapeDataString(sub)}");
            Assert.Equal(2, fc!.videoCount);

            // A path outside any source is rejected.
            var outside = await _api.Client.GetAsync(
                $"/api/import/folder-count?path={Uri.EscapeDataString(Path.GetTempPath())}");
            Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        }
        finally
        {
            await _api.WithDbAsync(db => db.VideoSets.Where(s => s.Path == dir).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
