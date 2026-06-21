using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Pins the exact semantics of the three-way tag filter and the playlist /
/// related endpoints so the #127 SQL push-down (which moves filtering out of
/// in-memory C# and into the database) cannot silently change which videos
/// match. Each case seeds a private VideoSet root and intersects results with
/// its own ids, so it is independent of whatever else lives in the DB.
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsFilterParityTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsFilterParityTests(PostgresApiFixture api) => _api = api;

    private sealed record VideoRow(Guid id);

    // A self-contained world: one enabled root, one tag group with two tags
    // (one of them hidden-by-default), and a spread of videos exercising every
    // filter dimension.
    private sealed class World
    {
        public string Root = "";
        public Guid GroupId, OtherGroupId;
        public Guid TagA, TagB, TagHidden;
        public Guid VOnlyA, VOnlyB, VAB, VNone, VHidden, VClip, VFav, VReview, VSub, VDeeper;
        public Guid[] All = Array.Empty<Guid>();
    }

    private async Task<World> SeedAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var w = new World
        {
            Root = $"/parity-{token}",
            GroupId = Guid.NewGuid(),
            OtherGroupId = Guid.NewGuid(),
            TagA = Guid.NewGuid(),
            TagB = Guid.NewGuid(),
            TagHidden = Guid.NewGuid(),
            VOnlyA = Guid.NewGuid(),
            VOnlyB = Guid.NewGuid(),
            VAB = Guid.NewGuid(),
            VNone = Guid.NewGuid(),
            VHidden = Guid.NewGuid(),
            VClip = Guid.NewGuid(),
            VFav = Guid.NewGuid(),
            VReview = Guid.NewGuid(),
            VSub = Guid.NewGuid(),
            VDeeper = Guid.NewGuid(),
        };
        w.All = new[] { w.VOnlyA, w.VOnlyB, w.VAB, w.VNone, w.VHidden, w.VClip, w.VFav, w.VReview, w.VSub, w.VDeeper };

        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = Guid.NewGuid(), Name = "parity-" + token, Path = w.Root, Enabled = true });
            db.TagGroups.Add(new TagGroup { Id = w.GroupId, Name = "g-" + token });
            db.TagGroups.Add(new TagGroup { Id = w.OtherGroupId, Name = "o-" + token });
            db.Tags.Add(new Tag { Id = w.TagA, Name = "A-" + token, TagGroupId = w.GroupId });
            db.Tags.Add(new Tag { Id = w.TagB, Name = "B-" + token, TagGroupId = w.GroupId });
            db.Tags.Add(new Tag { Id = w.TagHidden, Name = "H-" + token, TagGroupId = w.GroupId, HiddenByDefault = true });

            // NeedsReview defaults to true on the model; set it explicitly so the
            // status assertions are about intent, not defaults.
            Video V(Guid id, string rel, bool review = false) =>
                new() { Id = id, FileName = $"{id:N}.mp4", FilePath = $"{w.Root}/{rel}", NeedsReview = review };

            db.Videos.AddRange(
                V(w.VOnlyA, "a.mp4"),
                V(w.VOnlyB, "b.mp4"),
                V(w.VAB, "ab.mp4"),
                V(w.VNone, "none.mp4"),
                V(w.VHidden, "hidden.mp4"),
                new Video { Id = w.VClip, FileName = "clip.mp4", FilePath = $"{w.Root}/clip.mp4", ParentVideoId = w.VOnlyA, NeedsReview = false },
                new Video { Id = w.VFav, FileName = "fav.mp4", FilePath = $"{w.Root}/fav.mp4", IsFavorite = true, NeedsReview = false },
                V(w.VReview, "review.mp4", review: true),
                V(w.VSub, "sub/in.mp4"),
                V(w.VDeeper, "sub/deeper/down.mp4"));

            db.VideoTags.AddRange(
                new VideoTag { VideoId = w.VOnlyA, TagId = w.TagA },
                new VideoTag { VideoId = w.VOnlyB, TagId = w.TagB },
                new VideoTag { VideoId = w.VAB, TagId = w.TagA },
                new VideoTag { VideoId = w.VAB, TagId = w.TagB },
                new VideoTag { VideoId = w.VHidden, TagId = w.TagHidden });
            await db.SaveChangesAsync();
        });
        return w;
    }

    private async Task<HashSet<Guid>> FilterAsync(object body)
    {
        var res = await _api.Client.PostAsJsonAsync("/api/videos/filter", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<List<VideoRow>>();
        return rows!.Select(r => r.id).ToHashSet();
    }

    private sealed record FlagCountsRow(
        int favorite, int needsReview, int playbackIssue, int markedForDeletion,
        int clip, int embedded, int exported, int edited);
    private sealed record FilteredCountsRow(Dictionary<Guid, int> tagCounts, FlagCountsRow flags);

    private async Task<FilteredCountsRow> CountsAsync(object body)
    {
        var res = await _api.Client.PostAsJsonAsync("/api/videos/filtered-counts", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<FilteredCountsRow>())!;
    }

    private static object Tag(Guid id) => new { type = "tag", value = id.ToString() };
    private static object Folder(string path) => new { type = "folder", value = path };
    private static object Missing(Guid groupId) => new { type = "missing", value = $"tagGroup:{groupId}" };
    private static object Status(string v) => new { type = "status", value = v };

    [SkippableFact]
    public async Task Required_is_conjunctive()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        var onlyA = (await FilterAsync(new { required = new[] { Tag(w.TagA) } })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VOnlyA, w.VAB }.ToHashSet(), onlyA);

        var aAndB = (await FilterAsync(new { required = new[] { Tag(w.TagA), Tag(w.TagB) } })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VAB }.ToHashSet(), aAndB);
    }

    [SkippableFact]
    public async Task Optional_is_disjunctive()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        var aOrB = (await FilterAsync(new { optional = new[] { Tag(w.TagA), Tag(w.TagB) } })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VOnlyA, w.VOnlyB, w.VAB }.ToHashSet(), aOrB);
    }

    [SkippableFact]
    public async Task Excluded_removes_matches()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // Has A but not B -> VOnlyA (VAB has B).
        var aNotB = (await FilterAsync(new { required = new[] { Tag(w.TagA) }, excluded = new[] { Tag(w.TagB) } }))
            .Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VOnlyA }.ToHashSet(), aNotB);
    }

    [SkippableFact]
    public async Task Missing_from_group_matches_videos_with_no_tag_in_that_group()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // Videos with NO tag in GroupId. Tagged-in-group: VOnlyA, VOnlyB, VAB,
        // VHidden. Everything else in the world qualifies — but VHidden carries
        // the hidden tag and is auto-suppressed, so it never appears anyway.
        var missing = (await FilterAsync(new { required = new[] { Missing(w.GroupId) } })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VNone, w.VClip, w.VFav, w.VReview, w.VSub, w.VDeeper }.ToHashSet(), missing);
    }

    [SkippableFact]
    public async Task Status_flags_match_their_columns()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        Assert.Equal(new[] { w.VClip }.ToHashSet(),
            (await FilterAsync(new { required = new[] { Status("isClip") } })).Intersect(w.All).ToHashSet());
        Assert.Equal(new[] { w.VFav }.ToHashSet(),
            (await FilterAsync(new { required = new[] { Status("favorite") } })).Intersect(w.All).ToHashSet());
        Assert.Equal(new[] { w.VReview }.ToHashSet(),
            (await FilterAsync(new { required = new[] { Status("needsReview") } })).Intersect(w.All).ToHashSet());
    }

    [SkippableFact]
    public async Task Folder_matches_exact_directory_only()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // {root}/sub contains only VSub. VDeeper is in {root}/sub/deeper.
        var sub = (await FilterAsync(new { required = new[] { Folder($"{w.Root}/sub") } })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VSub }.ToHashSet(), sub);
    }

    [SkippableFact]
    public async Task Folder_combined_with_tag_still_narrows_correctly()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // Folder {root} (top level) AND tag A -> top-level videos carrying A.
        // VOnlyA and VAB are at top level with A; VSub/VDeeper are nested.
        var topWithA = (await FilterAsync(new
        {
            required = new object[] { Folder(w.Root), Tag(w.TagA) }
        })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VOnlyA, w.VAB }.ToHashSet(), topWithA);
    }

    [SkippableFact]
    public async Task Hidden_by_default_tag_is_suppressed_unless_filtered_for()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // No filter -> VHidden absent.
        var unfiltered = (await FilterAsync(new { })).Intersect(w.All).ToHashSet();
        Assert.DoesNotContain(w.VHidden, unfiltered);

        // Explicitly filter for the hidden tag -> it appears.
        var revealed = (await FilterAsync(new { required = new[] { Tag(w.TagHidden) } })).Intersect(w.All).ToHashSet();
        Assert.Equal(new[] { w.VHidden }.ToHashSet(), revealed);
    }

    // --- #208: filtered-counts scope to the same "shown" set as /videos/filter

    [SkippableFact]
    public async Task Filtered_counts_scope_tag_counts_to_the_shown_set()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // Required A shows VOnlyA + VAB. Over that set, A appears on both (2)
        // and B only on VAB (1); the hidden tag never appears.
        var counts = await CountsAsync(new { required = new[] { Tag(w.TagA) } });
        Assert.Equal(2, counts.tagCounts[w.TagA]);
        Assert.Equal(1, counts.tagCounts[w.TagB]);
        Assert.False(counts.tagCounts.ContainsKey(w.TagHidden));
    }

    [SkippableFact]
    public async Task Filtered_counts_scope_flag_counts_to_the_shown_set()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        // A private world of three videos all carrying ONE unique tag — so a
        // filter on that tag isolates exactly them from everything else in the
        // shared test DB (an aggregate flag filter like "favorite" would count
        // every favorite video other tests seeded too). One is favorite, one
        // needs review, one plain, so the flag counts over the shown set are
        // deterministic.
        var token = Guid.NewGuid().ToString("N");
        var root = $"/fc-{token}";
        var groupId = Guid.NewGuid();
        var tagX = Guid.NewGuid();
        var vFav = Guid.NewGuid();
        var vReview = Guid.NewGuid();
        var vPlain = Guid.NewGuid();
        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = Guid.NewGuid(), Name = "fc-" + token, Path = root, Enabled = true });
            db.TagGroups.Add(new TagGroup { Id = groupId, Name = "fc-" + token });
            db.Tags.Add(new Tag { Id = tagX, Name = "X-" + token, TagGroupId = groupId });
            // NeedsReview defaults to true on the model — set it explicitly so
            // the assertions are about intent, not defaults.
            db.Videos.Add(new Video { Id = vFav, FileName = "fav.mp4", FilePath = $"{root}/fav.mp4", IsFavorite = true, NeedsReview = false });
            db.Videos.Add(new Video { Id = vReview, FileName = "review.mp4", FilePath = $"{root}/review.mp4", NeedsReview = true });
            db.Videos.Add(new Video { Id = vPlain, FileName = "plain.mp4", FilePath = $"{root}/plain.mp4", NeedsReview = false });
            db.VideoTags.AddRange(
                new VideoTag { VideoId = vFav, TagId = tagX },
                new VideoTag { VideoId = vReview, TagId = tagX },
                new VideoTag { VideoId = vPlain, TagId = tagX });
            await db.SaveChangesAsync();
        });

        // Filter to the unique tag -> exactly the three videos above are shown.
        var counts = await CountsAsync(new { required = new[] { Tag(tagX) } });
        Assert.Equal(1, counts.flags.favorite);
        Assert.Equal(1, counts.flags.needsReview);
        Assert.Equal(0, counts.flags.playbackIssue);
        Assert.Equal(0, counts.flags.clip);
        Assert.Equal(3, counts.tagCounts[tagX]);
    }

    [SkippableFact]
    public async Task Playlist_random_matches_the_same_set_as_filter()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        var viaFilter = (await FilterAsync(new { optional = new[] { Tag(w.TagA), Tag(w.TagB) } })).Intersect(w.All).ToHashSet();

        var res = await _api.Client.PostAsJsonAsync("/api/playlists/random", new { optional = new[] { Tag(w.TagA), Tag(w.TagB) } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pl = await res.Content.ReadFromJsonAsync<PlaylistResult>();
        var viaPlaylist = pl!.videoIds.Intersect(w.All).ToHashSet();

        // Playlists don't apply the hidden-by-default suppression, so compare on
        // the explicitly-tagged set, which carries no hidden tag anyway.
        Assert.Equal(viaFilter, viaPlaylist);
    }

    [SkippableFact]
    public async Task Related_ranks_by_shared_tags_excludes_self_and_respects_limit()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var w = await SeedAsync();

        // VAB shares A with VOnlyA and B with VOnlyB. Related to VAB should
        // surface both and never VAB itself.
        var res = await _api.Client.GetAsync($"/api/videos/{w.VAB}/related?take=5");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<List<VideoRow>>();
        var ids = rows!.Select(r => r.id).ToList();

        Assert.DoesNotContain(w.VAB, ids);
        Assert.Contains(w.VOnlyA, ids);
        Assert.Contains(w.VOnlyB, ids);
    }

    // A shared world for the "hidden means hidden everywhere" tests: three
    // videos all carrying a fresh visible tag (so they're isolated from other
    // data), one of which also carries a hidden-by-default tag and must be
    // suppressed from every surface. Filenames include the token so search can
    // target them precisely.
    private sealed record HiddenScenario(
        string Token, string Root, Guid GroupId, Guid TagVisible, Guid TagHidden,
        Guid VAnchor, Guid VBoth, Guid VPlain);

    private async Task<HiddenScenario> SeedHiddenScenarioAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var s = new HiddenScenario(token, $"/hc-{token}", Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = Guid.NewGuid(), Name = "hc-" + token, Path = s.Root, Enabled = true });
            db.TagGroups.Add(new TagGroup { Id = s.GroupId, Name = "hc-" + token });
            db.Tags.Add(new Tag { Id = s.TagVisible, Name = "V-" + token, TagGroupId = s.GroupId });
            db.Tags.Add(new Tag { Id = s.TagHidden, Name = "H-" + token, TagGroupId = s.GroupId, HiddenByDefault = true });
            db.Videos.Add(new Video { Id = s.VAnchor, FileName = $"anchor-{token}.mp4", FilePath = $"{s.Root}/anchor-{token}.mp4" });
            db.Videos.Add(new Video { Id = s.VBoth, FileName = $"both-{token}.mp4", FilePath = $"{s.Root}/both-{token}.mp4" });
            db.Videos.Add(new Video { Id = s.VPlain, FileName = $"plain-{token}.mp4", FilePath = $"{s.Root}/plain-{token}.mp4" });
            db.VideoTags.AddRange(
                new VideoTag { VideoId = s.VAnchor, TagId = s.TagVisible },
                new VideoTag { VideoId = s.VBoth, TagId = s.TagVisible },
                new VideoTag { VideoId = s.VBoth, TagId = s.TagHidden }, // suppressed everywhere
                new VideoTag { VideoId = s.VPlain, TagId = s.TagVisible });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private Task CleanupHiddenScenarioAsync(HiddenScenario s) => _api.WithDbAsync(async db =>
    {
        var ids = new[] { s.VAnchor, s.VBoth, s.VPlain };
        await db.VideoTags.Where(vt => ids.Contains(vt.VideoId)).ExecuteDeleteAsync();
        await db.Videos.Where(v => ids.Contains(v.Id)).ExecuteDeleteAsync();
        await db.Tags.Where(t => t.TagGroupId == s.GroupId).ExecuteDeleteAsync();
        await db.TagGroups.Where(g => g.Id == s.GroupId).ExecuteDeleteAsync();
        await db.VideoSets.Where(vs => vs.Path == s.Root).ExecuteDeleteAsync();
    });

    [SkippableFact]
    public async Task Filter_reports_auto_hidden_match_count_in_header()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var s = await SeedHiddenScenarioAsync();
        try
        {
            var res = await _api.Client.PostAsJsonAsync("/api/videos/filter", new { required = new[] { Tag(s.TagVisible) } });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var ids = (await res.Content.ReadFromJsonAsync<List<VideoRow>>())!.Select(r => r.id).ToHashSet();
            Assert.Contains(s.VPlain, ids);
            Assert.DoesNotContain(s.VBoth, ids);

            // Only the seeded videos carry this fresh tag, so the auto-hide count
            // is exactly the one suppressed match (vBoth).
            Assert.True(res.Headers.TryGetValues("X-Hidden-Count", out var vals), "X-Hidden-Count header present");
            Assert.Equal("1", vals!.Single());
        }
        finally { await CleanupHiddenScenarioAsync(s); }
    }

    [SkippableFact]
    public async Task Playlist_excludes_hidden_by_default_videos()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var s = await SeedHiddenScenarioAsync();
        try
        {
            var res = await _api.Client.PostAsJsonAsync("/api/playlists/random", new { required = new[] { Tag(s.TagVisible) } });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var pl = await res.Content.ReadFromJsonAsync<PlaylistResult>();
            Assert.Contains(s.VPlain, pl!.videoIds);
            Assert.DoesNotContain(s.VBoth, pl.videoIds);
        }
        finally { await CleanupHiddenScenarioAsync(s); }
    }

    [SkippableFact]
    public async Task Related_excludes_hidden_by_default_videos()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var s = await SeedHiddenScenarioAsync();
        try
        {
            // vPlain and vBoth both share TagVisible with the anchor; the hidden
            // one must not surface as related.
            var rows = await _api.Client.GetFromJsonAsync<List<VideoRow>>($"/api/videos/{s.VAnchor}/related?take=50");
            var ids = rows!.Select(r => r.id).ToHashSet();
            Assert.Contains(s.VPlain, ids);
            Assert.DoesNotContain(s.VBoth, ids);
        }
        finally { await CleanupHiddenScenarioAsync(s); }
    }

    [SkippableFact]
    public async Task Search_excludes_hidden_by_default_videos()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var s = await SeedHiddenScenarioAsync();
        try
        {
            // The token matches all three filenames; the hidden one is unfindable.
            var resp = await _api.Client.GetFromJsonAsync<SearchResp>($"/api/search?q={s.Token}&limit=50");
            var ids = resp!.results.Select(r => r.id).ToHashSet();
            Assert.Contains(s.VPlain.ToString(), ids);
            Assert.Contains(s.VAnchor.ToString(), ids);
            Assert.DoesNotContain(s.VBoth.ToString(), ids);
        }
        finally { await CleanupHiddenScenarioAsync(s); }
    }

    private sealed record SearchResp(List<SearchHit> results);
    private sealed record SearchHit(string id);

    private sealed record PlaylistResult(Guid id, List<Guid> videoIds, DateTime createdAt);
}
