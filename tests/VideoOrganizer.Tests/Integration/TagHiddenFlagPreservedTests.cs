using System.Net.Http.Json;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Issue #194 — updating a tag must NOT clear its "hidden by default" flag when
/// the caller omits it (rename / favorite via FilterDialog, the Tags page,
/// video tag pills). Omitting HiddenByDefault leaves it unchanged; sending a
/// value sets it. Runs against a real Postgres container.
/// </summary>
[Collection("PostgresApi")]
public sealed class TagHiddenFlagPreservedTests
{
    private readonly PostgresApiFixture _api;

    public TagHiddenFlagPreservedTests(PostgresApiFixture api) => _api = api;

    private sealed record CreatedGroup(Guid id);
    private sealed record TagView(Guid id, string name, bool hiddenByDefault);

    [SkippableFact]
    public async Task Update_without_hiddenByDefault_leaves_it_unchanged()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N")[..8];
        var group = (await (await _api.Client.PostAsJsonAsync("/api/tag-groups", new { name = $"G {token}" }))
            .Content.ReadFromJsonAsync<CreatedGroup>())!;

        // Create a tag that is hidden by default.
        var created = (await (await _api.Client.PostAsJsonAsync("/api/tags",
            new { tagGroupId = group.id, name = $"hidden {token}", hiddenByDefault = true }))
            .Content.ReadFromJsonAsync<TagView>())!;
        Assert.True(created.hiddenByDefault);

        // A rename-style update that omits hiddenByDefault must NOT clear it.
        var put = await _api.Client.PutAsJsonAsync($"/api/tags/{created.id}", new
        {
            name = $"renamed {token}",
            aliases = Array.Empty<string>(),
            isFavorite = false,
            sortOrder = 0,
            notes = "",
        });
        put.EnsureSuccessStatusCode();

        var afterOmit = await _api.Client.GetFromJsonAsync<TagView>($"/api/tags/{created.id}");
        Assert.True(afterOmit!.hiddenByDefault, "omitting hiddenByDefault should leave it hidden");

        // Explicitly sending false turns it off.
        var put2 = await _api.Client.PutAsJsonAsync($"/api/tags/{created.id}", new
        {
            name = $"renamed {token}",
            aliases = Array.Empty<string>(),
            isFavorite = false,
            sortOrder = 0,
            notes = "",
            hiddenByDefault = false,
        });
        put2.EnsureSuccessStatusCode();

        var afterFalse = await _api.Client.GetFromJsonAsync<TagView>($"/api/tags/{created.id}");
        Assert.False(afterFalse!.hiddenByDefault, "explicitly sending false should clear it");
    }
}
