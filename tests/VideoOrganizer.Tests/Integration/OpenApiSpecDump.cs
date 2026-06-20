using System.Net;
using System.Text.Json;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Regeneration tool (not an assertion test): writes the live OpenAPI document
/// to the committed <c>ci/openapi.json</c>, which is the single source of truth
/// for the generated frontend types (#125). It only runs when
/// <c>SAS_DUMP_OPENAPI=1</c> so a normal test run never rewrites the file.
///
/// Usage (from the repo root):
///   SAS_DUMP_OPENAPI=1 dotnet test tests/VideoOrganizer.Tests \
///       --filter FullyQualifiedName~OpenApiSpecDump
///   cd src/VideoOrganizer.SvelteUI &amp;&amp; npm run gen:types
///
/// Hermetic: the spec comes from the same Testcontainers-backed API the rest of
/// the suite uses (fresh migrated DB, no real data), so it captures the schema
/// exactly as served — primitives inline, enums with camelCase values, response
/// DTOs marked required.
/// </summary>
[Collection("PostgresApi")]
public sealed class OpenApiSpecDump
{
    private readonly PostgresApiFixture _api;

    public OpenApiSpecDump(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Dump_writes_ci_openapi_json()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("SAS_DUMP_OPENAPI") == "1",
            "Set SAS_DUMP_OPENAPI=1 to regenerate ci/openapi.json.");
        Skip.IfNot(_api.Available, _api.SkipReason);

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();

        // Pretty-print with stable formatting so the committed file diffs cleanly.
        using var doc = JsonDocument.Parse(raw);
        var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });

        var target = Path.Combine(RepoRoot(), "ci", "openapi.json");
        await File.WriteAllTextAsync(target, pretty + "\n");
    }

    // Walk up from the test assembly location until we find the repo root (the
    // directory that contains the `ci/` folder and a `.git` entry).
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ci")) &&
                (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (no ancestor with ci/ and .git).");
    }
}
