using System.Net;
using System.Text.Json;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Guards that the OpenAPI document actually describes response shapes. Endpoints
/// used to return untyped IResult, so response DTOs (VideoDto, TagDto, …) were
/// absent from the spec — which blocks generating frontend types from it (#125).
/// The .Produces&lt;T&gt;() annotations put them back; this test keeps them there.
/// </summary>
[Collection("PostgresApi")]
public sealed class OpenApiSpecTests
{
    private readonly PostgresApiFixture _api;

    public OpenApiSpecTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Spec_describes_the_key_response_dtos()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var schemaNames = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).ToList();

        // These are response-only DTOs the frontend depends on. The schema ref
        // ids are namespace-mangled (e.g. VideoOrganizer_Shared_Dto_VideoDto), so
        // match by substring rather than exact name.
        foreach (var dto in new[] { "VideoDto", "TagDto", "TagGroupDto", "DuplicateCandidateDto", "BackupInfo" })
        {
            Assert.True(
                schemaNames.Any(n => n.Contains(dto, StringComparison.Ordinal)),
                $"OpenAPI spec should include a schema for {dto} (have: {schemaNames.Count} schemas)");
        }
    }
}
