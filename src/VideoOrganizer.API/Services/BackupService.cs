using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API.Services;

// Database backup (issue #32). Self-contained, no external tooling, so it
// behaves identically on Windows and the Linux deployment:
//   · JSON snapshot — a quick one-file dump of every table (this PR).
//   · SQLite full backup — a portable .sqlite (follow-up PR).
// Files live under Backup:Directory (default <app base>/backups).
public sealed class BackupService
{
    private readonly string _dir;

    // Indented + cycle-safe. AsNoTracking queries leave navigation properties
    // null, and WhenWritingNull keeps them out of the file, so each table is a
    // clean array of its own columns.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BackupService(IConfiguration config)
    {
        _dir = config["Backup:Directory"] is { Length: > 0 } d
            ? d
            : Path.Combine(AppContext.BaseDirectory, "backups");
    }

    public string BackupsDirectory
    {
        get { Directory.CreateDirectory(_dir); return _dir; }
    }

    // Snapshot the whole database to a single timestamped JSON file.
    public async Task<BackupInfo> CreateJsonSnapshotAsync(VideoOrganizerDbContext db, CancellationToken ct)
    {
        var dir = BackupsDirectory;
        var snapshot = new DbSnapshot
        {
            SchemaVersion = 1,
            CreatedUtc = DateTime.UtcNow,
            VideoSets = await db.VideoSets.AsNoTracking().ToListAsync(ct),
            TagGroups = await db.TagGroups.AsNoTracking().ToListAsync(ct),
            Tags = await db.Tags.AsNoTracking().ToListAsync(ct),
            PropertyDefinitions = await db.PropertyDefinitions.AsNoTracking().ToListAsync(ct),
            Videos = await db.Videos.AsNoTracking().ToListAsync(ct),
            VideoTags = await db.VideoTags.AsNoTracking().ToListAsync(ct),
            TagPropertyValues = await db.TagPropertyValues.AsNoTracking().ToListAsync(ct),
            VideoPropertyValues = await db.VideoPropertyValues.AsNoTracking().ToListAsync(ct),
            DuplicateCandidates = await db.DuplicateCandidates.AsNoTracking().ToListAsync(ct),
            FileMoveLogs = await db.FileMoveLogs.AsNoTracking().ToListAsync(ct)
        };

        var fileName = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var path = Path.Combine(dir, fileName);
        await using (var fs = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(fs, snapshot, JsonOpts, ct);
        }
        return ToInfo(new FileInfo(path));
    }

    public List<BackupInfo> List()
    {
        if (!Directory.Exists(_dir)) return new();
        return Directory.EnumerateFiles(_dir)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Select(ToInfo)
            .ToList();
    }

    // Resolve a backup by file name, rejecting anything that tries to escape
    // the backups directory (path-traversal guard for download/delete/restore).
    public string? ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")) return null;
        var path = Path.Combine(_dir, fileName);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string fileName)
    {
        var path = ResolvePath(fileName);
        if (path is null) return false;
        File.Delete(path);
        return true;
    }

    private static BackupInfo ToInfo(FileInfo fi) => new(
        fi.Name,
        fi.Extension.TrimStart('.').ToLowerInvariant(),
        fi.Length,
        fi.LastWriteTimeUtc);
}
