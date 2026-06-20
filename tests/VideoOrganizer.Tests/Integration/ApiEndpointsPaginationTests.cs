using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Keyset pagination (#127): pages cover the whole result set exactly once, in
/// the right order per sort mode, and shuffle is stable for a fixed seed. Also
/// exercises the EF translation of the keyset (Guid tiebreaker, string.Compare,
/// the md5 DB function) — a translation failure surfaces here as a 500.
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsPaginationTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsPaginationTests(PostgresApiFixture api) => _api = api;

    private sealed record VideoRow(Guid id, string fileName);
    private sealed record Page(List<VideoRow> videos, string? nextCursor, int totalCount, int hiddenCount);

    // 7 videos sharing one fresh tag (so the filter isolates them), with distinct
    // sortable fields plus one duplicate FileName to exercise the Id tiebreaker.
    private sealed record World(string Root, Guid Tag, List<Guid> Ids);

    private async Task<World> SeedAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var root = $"/pg-{token}";
        var groupId = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var ids = new List<Guid>();

        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = Guid.NewGuid(), Name = "pg-" + token, Path = root, Enabled = true });
            db.TagGroups.Add(new TagGroup { Id = groupId, Name = "pg-" + token });
            db.Tags.Add(new Tag { Id = tag, Name = "T-" + token, TagGroupId = groupId });

            // names dup-1..dup-1 (two share a name) to force the Id tiebreaker.
            var specs = new (string name, long size, int minutes)[]
            {
                ("alpha", 50, 5), ("bravo", 10, 1), ("charlie", 30, 3),
                ("delta", 40, 4), ("echo", 20, 2), ("dup", 25, 6), ("dup", 35, 7),
            };
            var i = 0;
            foreach (var (name, size, minutes) in specs)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Videos.Add(new Video
                {
                    Id = id,
                    FileName = $"{name}.mp4",
                    FilePath = $"{root}/{name}-{i}.mp4",
                    FileSize = size,
                    Duration = TimeSpan.FromMinutes(minutes),
                });
                db.VideoTags.Add(new VideoTag { VideoId = id, TagId = tag });
                i++;
            }
            await db.SaveChangesAsync();
        });

        return new World(root, tag, ids);
    }

    private Task CleanupAsync(World w) => _api.WithDbAsync(async db =>
    {
        await db.VideoTags.Where(vt => w.Ids.Contains(vt.VideoId)).ExecuteDeleteAsync();
        await db.Videos.Where(v => w.Ids.Contains(v.Id)).ExecuteDeleteAsync();
        await db.Tags.Where(t => t.Id == w.Tag).ExecuteDeleteAsync();
        await db.VideoSets.Where(s => s.Path == w.Root).ExecuteDeleteAsync();
    });

    // Page through the whole result for `sort`, returning rows in page order.
    private async Task<List<VideoRow>> PageAllAsync(World w, string sort, string? seed = null, int pageSize = 2)
    {
        var ordered = new List<VideoRow>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var url = $"/api/videos/filter-page?sort={sort}&limit={pageSize}";
            if (seed is not null) url += $"&seed={seed}";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";

            var res = await _api.Client.PostAsJsonAsync(url,
                new { required = new[] { new { type = "tag", value = w.Tag.ToString() } } });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var page = await res.Content.ReadFromJsonAsync<Page>();
            ordered.AddRange(page!.videos);
            Assert.True(page.videos.Count <= pageSize, "page must not exceed the limit");

            cursor = page.nextCursor;
            if (cursor is null) break;
        }
        return ordered;
    }

    [SkippableFact]
    public async Task FileName_pages_cover_everything_in_order()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();
        try
        {
            var rows = await PageAllAsync(w, "fileName");
            var ids = rows.Select(r => r.id).ToList();
            Assert.Equal(w.Ids.Count, ids.Count);              // complete
            Assert.Equal(ids.Count, ids.Distinct().Count());   // no overlap/dupes across keyset pages
            Assert.Equal(w.Ids.ToHashSet(), ids.ToHashSet());  // exactly the seeded set

            // And actually in ascending filename order (the two "dup.mp4" rows
            // are adjacent, ordered by the Id tiebreaker).
            var names = rows.Select(r => r.fileName).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
        }
        finally { await CleanupAsync(w); }
    }

    [SkippableFact]
    public async Task FileSize_and_duration_pages_cover_everything()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();
        try
        {
            foreach (var sort in new[] { "fileSize", "duration", "folderFile" })
            {
                var ids = (await PageAllAsync(w, sort)).Select(r => r.id).ToList();
                Assert.Equal(w.Ids.Count, ids.Count);
                Assert.Equal(ids.Count, ids.Distinct().Count());
                Assert.Equal(w.Ids.ToHashSet(), ids.ToHashSet());
            }
        }
        finally { await CleanupAsync(w); }
    }

    [SkippableFact]
    public async Task Total_count_is_the_full_match_set_not_the_page()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();
        try
        {
            // Page size 2, but totalCount must be the whole 7-row match set so the
            // "video N of M" badge shows M, not the loaded count.
            var res = await _api.Client.PostAsJsonAsync(
                "/api/videos/filter-page?sort=fileName&limit=2",
                new { required = new[] { new { type = "tag", value = w.Tag.ToString() } } });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var page = await res.Content.ReadFromJsonAsync<Page>();
            Assert.Equal(2, page!.videos.Count);
            Assert.Equal(w.Ids.Count, page.totalCount); // 7
            Assert.NotNull(page.nextCursor);
        }
        finally { await CleanupAsync(w); }
    }

    [SkippableFact]
    public async Task Shuffle_is_complete_and_stable_for_a_seed()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();
        try
        {
            var first = (await PageAllAsync(w, "shuffle", seed: "abc123")).Select(r => r.id).ToList();
            Assert.Equal(w.Ids.Count, first.Count);
            Assert.Equal(first.Count, first.Distinct().Count());
            Assert.Equal(w.Ids.ToHashSet(), first.ToHashSet());

            // Same seed → identical order (pages don't drift).
            var second = (await PageAllAsync(w, "shuffle", seed: "abc123")).Select(r => r.id).ToList();
            Assert.Equal(first, second);

            // A different seed should (almost surely) reorder 7 items.
            var other = (await PageAllAsync(w, "shuffle", seed: "zzz999")).Select(r => r.id).ToList();
            Assert.Equal(w.Ids.ToHashSet(), other.ToHashSet());
            Assert.NotEqual(first, other);
        }
        finally { await CleanupAsync(w); }
    }
}
