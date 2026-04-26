using System.Text.Json;
using System.Text.Json.Serialization;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API.Services;

// Reads config/tags.seed.json on startup. If the tag_groups table is empty,
// populates groups + tags + property definitions from the file. Idempotent —
// once any TagGroup row exists, this is a no-op (the user is now editing
// runtime state via the API, not the file). Load/save/update of the file
// itself is intentionally not implemented yet.
public sealed class TagSeedService
{
    private readonly VideoOrganizerDbContext _db;
    private readonly ILogger<TagSeedService> _log;
    private readonly IHostEnvironment _env;

    public TagSeedService(
        VideoOrganizerDbContext db,
        ILogger<TagSeedService> log,
        IHostEnvironment env)
    {
        _db = db;
        _log = log;
        _env = env;
    }

    public async Task SeedIfEmptyAsync(CancellationToken ct = default)
    {
        if (_db.TagGroups.Any())
        {
            _log.LogInformation("Tag groups already present — skipping seed.");
            return;
        }

        var path = Path.Combine(_env.ContentRootPath, "config", "tags.seed.json");
        if (!File.Exists(path))
        {
            _log.LogWarning("Seed file not found at {Path}; starting with no tag groups.", path);
            return;
        }

        SeedFile? file;
        try
        {
            await using var stream = File.OpenRead(path);
            file = await JsonSerializer.DeserializeAsync<SeedFile>(stream, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read seed file at {Path}; aborting seed.", path);
            return;
        }

        if (file is null || file.TagGroups is null)
        {
            _log.LogWarning("Seed file at {Path} parsed to null or no tagGroups; nothing to seed.", path);
            return;
        }

        var groupsByName = new Dictionary<string, TagGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in file.TagGroups)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) continue;

            var group = new TagGroup
            {
                Id = Guid.NewGuid(),
                Name = g.Name,
                AllowMultiple = g.AllowMultiple ?? true,
                DisplayAsCheckboxes = g.DisplayAsCheckboxes ?? false,
                SortOrder = g.SortOrder ?? 0,
                Notes = g.Notes ?? string.Empty
            };
            _db.TagGroups.Add(group);
            groupsByName[g.Name] = group;

            if (g.Tags is not null)
            {
                var tagSort = 0;
                foreach (var t in g.Tags)
                {
                    if (string.IsNullOrWhiteSpace(t.Name)) continue;
                    _db.Tags.Add(new Tag
                    {
                        Id = Guid.NewGuid(),
                        TagGroupId = group.Id,
                        Name = t.Name,
                        Aliases = t.Aliases?.ToList() ?? new(),
                        IsFavorite = t.IsFavorite ?? false,
                        SortOrder = t.SortOrder ?? tagSort,
                        Notes = t.Notes ?? string.Empty
                    });
                    tagSort += 10;
                }
            }
        }

        if (file.Properties is not null)
        {
            foreach (var p in file.Properties)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;

                Guid? tagGroupId = null;
                if (p.Scope == PropertyScope.Tag)
                {
                    if (p.TagGroup is null || !groupsByName.TryGetValue(p.TagGroup, out var grp))
                    {
                        _log.LogWarning(
                            "Property '{Name}' is Tag-scoped but TagGroup '{Group}' not found in seed; skipping.",
                            p.Name, p.TagGroup);
                        continue;
                    }
                    tagGroupId = grp.Id;
                }

                _db.PropertyDefinitions.Add(new PropertyDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = p.Name,
                    DataType = p.DataType ?? PropertyDataType.Text,
                    Scope = p.Scope,
                    TagGroupId = tagGroupId,
                    Required = p.Required ?? false,
                    SortOrder = p.SortOrder ?? 0,
                    Notes = p.Notes ?? string.Empty
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Seeded {Groups} tag groups and {Properties} property definitions from {Path}.",
            file.TagGroups.Count,
            file.Properties?.Count ?? 0,
            path);
    }

    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return opts;
    }

    private sealed class SeedFile
    {
        public List<SeedTagGroup>? TagGroups { get; set; }
        public List<SeedProperty>? Properties { get; set; }
    }

    private sealed class SeedTagGroup
    {
        public string? Name { get; set; }
        public bool? AllowMultiple { get; set; }
        public bool? DisplayAsCheckboxes { get; set; }
        public int? SortOrder { get; set; }
        public string? Notes { get; set; }
        public List<SeedTag>? Tags { get; set; }
    }

    private sealed class SeedTag
    {
        public string? Name { get; set; }
        public List<string>? Aliases { get; set; }
        public bool? IsFavorite { get; set; }
        public int? SortOrder { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class SeedProperty
    {
        public string? Name { get; set; }
        public PropertyDataType? DataType { get; set; }
        public PropertyScope Scope { get; set; }
        public string? TagGroup { get; set; }
        public bool? Required { get; set; }
        public int? SortOrder { get; set; }
        public string? Notes { get; set; }
    }
}
