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
    public async Task Spec_does_not_hoist_framework_primitives_into_components()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var schemaNames = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).ToList();

        // Framework primitives must stay inline. A reference-id strategy that named
        // every type (#138) hoisted these into components and made each property a
        // $ref to "String"/"Guid"/… — junk for client codegen.
        foreach (var prim in new[] { "String", "Guid", "Int32", "Int64", "Boolean", "Double", "DateTime", "DateTimeOffset", "TimeSpan" })
        {
            Assert.DoesNotContain(prim, schemaNames);
        }

        // And our own DTOs/enums are still componentised by their clean short name.
        Assert.Contains("VideoDto", schemaNames);
    }

    [SkippableFact]
    public async Task Spec_marks_non_nullable_response_fields_required()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var video = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("VideoDto");
        var required = video.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        var props = video.GetProperty("properties");

        // Response DTOs are always fully serialised, so every member is required —
        // the key is always present. This is what makes generated TS keys
        // non-optional (e.g. `id: string`, not `id?: string`).
        Assert.Contains("id", required);
        Assert.Contains("fileName", required);
        Assert.Contains("md5", required);

        // A non-nullable member's value is non-null...
        Assert.False(AllowsNull(props.GetProperty("fileName")), "fileName should not allow null");
        // ...while a nullable member is required-but-nullable -> generates `md5: string | null`.
        Assert.True(AllowsNull(props.GetProperty("md5")), "md5 should allow null");
    }

    // A property allows null in any of the encodings .NET 10 / OpenAPI 3.1 use:
    //   inline primitive:  { "type": ["string", "null"] }
    //   bare null:         { "type": "null" }
    //   $ref union:        { "oneOf": [ { "type": "null" }, { "$ref": … } ] }
    private static bool AllowsNull(JsonElement schema)
    {
        if (HasNullType(schema)) return true;
        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
            return oneOf.EnumerateArray().Any(HasNullType);
        return false;
    }

    private static bool HasNullType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var t)) return false;
        if (t.ValueKind == JsonValueKind.String) return t.GetString() == "null";
        if (t.ValueKind == JsonValueKind.Array)
            return t.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == "null");
        return false;
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
