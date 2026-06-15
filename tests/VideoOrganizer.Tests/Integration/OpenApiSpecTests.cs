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

        // Clean schema-id strategy (#125 step 2): DTOs are referenced by their
        // plain short name now.
        foreach (var dto in new[] { "VideoDto", "TagDto", "TagGroupDto", "DuplicateCandidateDto", "BackupInfo" })
        {
            Assert.Contains(dto, schemaNames);
        }
    }

    [SkippableFact]
    public async Task Spec_schema_names_are_clean()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var schemaNames = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).ToList();

        // No namespace-mangled prefixes and no assembly-qualified generic
        // monstrosities (the old FullName strategy emitted both).
        foreach (var name in schemaNames)
        {
            Assert.DoesNotContain("VideoOrganizer_", name);
            Assert.DoesNotContain("[[", name);
            Assert.DoesNotContain("`", name);
        }
    }

    [SkippableFact]
    public async Task Spec_enums_carry_their_camelCase_values()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Without the enum schema transformer these carry no values at all.
        Assert.True(schemas.TryGetProperty("VideoCodec", out var videoCodec), "VideoCodec schema present");
        var values = videoCodec.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("hevc", values);
        Assert.Contains("h264", values);
        Assert.DoesNotContain("HEVC", values); // camelCase wire form, not the C# name
    }
}
