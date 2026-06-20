using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Issue #200 — after import a video is NeedsReview=true; adding a tag or
/// setting a flag (favorite / clip / playback-issue / to-delete) counts as
/// reviewing it and auto-clears NeedsReview. The reviewed toggle itself and
/// no-op updates leave it alone. Real Postgres.
/// </summary>
[Collection("PostgresApi")]
public sealed class NeedsReviewAutoClearTests
{
    private readonly PostgresApiFixture _api;

    public NeedsReviewAutoClearTests(PostgresApiFixture api) => _api = api;

    private async Task<Guid> NewNeedsReviewVideoAsync(string token)
    {
        var id = Guid.NewGuid();
        await _api.WithDbAsync(async db =>
        {
            db.Videos.Add(new Video
            {
                Id = id, FileName = $"{token}.mp4", FilePath = $"/nr-{token}/{token}.mp4",
                NeedsReview = true,
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<bool> NeedsReviewAsync(Guid id)
    {
        bool nr = true;
        await _api.WithDbAsync(async db =>
            nr = (await db.Videos.AsNoTracking().FirstAsync(v => v.Id == id)).NeedsReview);
        return nr;
    }

    [SkippableFact]
    public async Task Marking_favorite_clears_needs_review()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var id = await NewNeedsReviewVideoAsync(Guid.NewGuid().ToString("N")[..8]);
        (await _api.Client.PostAsync($"/api/videos/{id}/mark-favorite", null)).EnsureSuccessStatusCode();
        Assert.False(await NeedsReviewAsync(id));
    }

    [SkippableFact]
    public async Task Marking_clip_clears_needs_review()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var id = await NewNeedsReviewVideoAsync(Guid.NewGuid().ToString("N")[..8]);
        (await _api.Client.PostAsync($"/api/videos/{id}/mark-clip", null)).EnsureSuccessStatusCode();
        Assert.False(await NeedsReviewAsync(id));
    }

    [SkippableFact]
    public async Task Adding_a_tag_clears_needs_review_but_a_noop_set_does_not()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        var token = Guid.NewGuid().ToString("N")[..8];
        var groupId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var id = await NewNeedsReviewVideoAsync(token);
        await _api.WithDbAsync(async db =>
        {
            db.TagGroups.Add(new TagGroup { Id = groupId, Name = $"G-{token}" });
            db.Tags.Add(new Tag { Id = tagId, Name = $"T-{token}", TagGroupId = groupId });
            await db.SaveChangesAsync();
        });

        // Adding the tag clears NeedsReview.
        (await _api.Client.PutAsJsonAsync($"/api/videos/{id}/tags", new { tagIds = new[] { tagId } }))
            .EnsureSuccessStatusCode();
        Assert.False(await NeedsReviewAsync(id));

        // Re-flag for review, then re-send the SAME tag set: no tag added -> stays.
        await _api.WithDbAsync(async db =>
        {
            var v = await db.Videos.FirstAsync(x => x.Id == id);
            v.NeedsReview = true;
            await db.SaveChangesAsync();
        });
        (await _api.Client.PutAsJsonAsync($"/api/videos/{id}/tags", new { tagIds = new[] { tagId } }))
            .EnsureSuccessStatusCode();
        Assert.True(await NeedsReviewAsync(id));
    }
}
