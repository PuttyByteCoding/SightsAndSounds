using System.Net;
using System.Net.Http.Json;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Issue #191 — a tag NAME is unique per group, not globally: the same name may
/// exist in two different groups (e.g. a Producer "Tom Petty" and an Artist
/// "Tom Petty") as distinct tags, while a duplicate within one group is still
/// rejected. Runs against a real Postgres container.
/// </summary>
[Collection("PostgresApi")]
public sealed class TagNamePerGroupTests
{
    private readonly PostgresApiFixture _api;

    public TagNamePerGroupTests(PostgresApiFixture api) => _api = api;

    private sealed record CreatedGroup(Guid id, string name);
    private sealed record CreatedTag(Guid id, Guid tagGroupId, string name);

    [SkippableFact]
    public async Task Same_name_allowed_in_different_groups_but_not_within_one()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N")[..8];
        var name = $"Tom Petty {token}";

        var producer = await CreateGroupAsync($"Producer {token}");
        var artist = await CreateGroupAsync($"Artist {token}");

        // Same name in two different groups -> two distinct tags.
        var (status1, tag1) = await CreateTagAsync(producer.id, name);
        var (status2, tag2) = await CreateTagAsync(artist.id, name);

        Assert.Equal(HttpStatusCode.Created, status1);
        Assert.Equal(HttpStatusCode.Created, status2);
        Assert.NotNull(tag1);
        Assert.NotNull(tag2);
        Assert.NotEqual(tag1!.id, tag2!.id);
        Assert.Equal(producer.id, tag1.tagGroupId);
        Assert.Equal(artist.id, tag2.tagGroupId);

        // Same name AGAIN in the same group -> rejected.
        var (statusDup, _) = await CreateTagAsync(producer.id, name);
        Assert.Equal(HttpStatusCode.Conflict, statusDup);
    }

    private async Task<CreatedGroup> CreateGroupAsync(string name)
    {
        var res = await _api.Client.PostAsJsonAsync("/api/tag-groups", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CreatedGroup>())!;
    }

    private async Task<(HttpStatusCode, CreatedTag?)> CreateTagAsync(Guid groupId, string name)
    {
        var res = await _api.Client.PostAsJsonAsync("/api/tags", new { tagGroupId = groupId, name });
        var tag = res.IsSuccessStatusCode
            ? await res.Content.ReadFromJsonAsync<CreatedTag>()
            : null;
        return (res.StatusCode, tag);
    }
}
