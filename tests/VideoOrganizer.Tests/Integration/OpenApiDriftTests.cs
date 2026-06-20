using System.Net;
using System.Text;
using System.Text.Json;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Drift guard for #125: fails when the live OpenAPI document no longer matches
/// the committed <c>ci/openapi.json</c> that the generated frontend types are
/// built from. When this fails, someone changed an endpoint/DTO without
/// regenerating — run the dump tool + gen:types (see <see cref="OpenApiSpecDump"/>).
///
/// Compares canonically (recursively key-sorted JSON) so cosmetic formatting or
/// property ordering never trips it — only real schema changes do.
/// </summary>
[Collection("PostgresApi")]
public sealed class OpenApiDriftTests
{
    private readonly PostgresApiFixture _api;

    public OpenApiDriftTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Committed_spec_matches_the_live_document()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var committedPath = Path.Combine(RepoRoot(), "ci", "openapi.json");
        Skip.IfNot(File.Exists(committedPath), $"ci/openapi.json not found at {committedPath}");

        var res = await _api.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var live = Canonicalize(await res.Content.ReadAsStringAsync());
        var committed = Canonicalize(await File.ReadAllTextAsync(committedPath));

        Assert.True(live == committed,
            "ci/openapi.json is stale — the live API spec changed. Regenerate it:\n" +
            "  SAS_DUMP_OPENAPI=1 dotnet test tests/VideoOrganizer.Tests --filter FullyQualifiedName~OpenApiSpecDump\n" +
            "  cd src/VideoOrganizer.SvelteUI && npm run gen:types\n" +
            "then commit ci/openapi.json and src/lib/api.generated.ts.");
    }

    // Recursively re-serialize with object keys sorted, so equality ignores
    // key/property ordering and whitespace.
    private static string Canonicalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        Write(doc.RootElement, sb);
        return sb.ToString();
    }

    private static void Write(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Write(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                // Normalize via the logical value so escaping differences between
                // the live endpoint and the re-serialized committed file don't
                // register as drift.
                sb.Append(JsonSerializer.Serialize(el.GetString()));
                break;
            case JsonValueKind.Number:
                // Normalize numeric formatting (e.g. 1 vs 1.0, 1E-05 vs 0.00001).
                if (el.TryGetInt64(out var l))
                    sb.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append(el.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(el.GetRawText()); // true / false / null
                break;
        }
    }

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
