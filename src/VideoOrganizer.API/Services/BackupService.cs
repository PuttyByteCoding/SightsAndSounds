using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API.Services;

// Database backup (issue #32). Self-contained, no external tooling.
//   · JSON snapshot — a quick one-file dump of every table.
//   · SQLite full backup — a portable .sqlite (follow-up PR).
// The API runs on the host (not containerized), so backups can be written to
// any directory the user picks. The chosen directory is persisted to a small
// per-user settings file so it survives restarts; default is
// <LocalAppData>/SightsAndSounds/backups, overridable by Backup:Directory.
public sealed class BackupService
{
    private readonly string _defaultDir;
    private readonly string _settingsPath;
    private string? _overrideDir;

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BackupService(IConfiguration config)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SightsAndSounds");
        _settingsPath = Path.Combine(appData, "backup-settings.json");
        _defaultDir = config["Backup:Directory"] is { Length: > 0 } d
            ? d
            : Path.Combine(appData, "backups");
        _overrideDir = LoadOverride();
    }

    // Where backups are written: the user-set directory, else the default.
    public string Directory =>
        string.IsNullOrWhiteSpace(_overrideDir) ? _defaultDir : _overrideDir!;

    public BackupSettings GetSettings()
    {
        var (writable, error) = CheckWritable(Directory);
        return new BackupSettings(Directory, writable, error);
    }

    public BackupSettings SetDirectory(string? dir)
    {
        dir = (dir ?? string.Empty).Trim();
        if (dir.Length == 0) throw new InvalidOperationException("A directory is required.");
        var (writable, error) = CheckWritable(dir);
        if (!writable) throw new InvalidOperationException($"Can't use that directory: {error}");
        _overrideDir = dir;
        SaveOverride(dir);
        return new BackupSettings(dir, true, null);
    }

    // Quick local snapshot: dump every table to a timestamped JSON file.
    public async Task<BackupInfo> CreateJsonSnapshotAsync(VideoOrganizerDbContext db, CancellationToken ct)
    {
        var dir = Directory;
        System.IO.Directory.CreateDirectory(dir);
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
            await JsonSerializer.SerializeAsync(fs, snapshot, SnapshotJson, ct);
        }
        return ToInfo(new FileInfo(path));
    }

    public List<BackupInfo> List()
    {
        var dir = Directory;
        if (!System.IO.Directory.Exists(dir)) return new();
        return System.IO.Directory.EnumerateFiles(dir)
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
        var path = Path.Combine(Directory, fileName);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string fileName)
    {
        var path = ResolvePath(fileName);
        if (path is null) return false;
        File.Delete(path);
        return true;
    }

    private static (bool writable, string? error) CheckWritable(string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string? LoadOverride()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_settingsPath));
            return stored?.Directory;
        }
        catch
        {
            return null;
        }
    }

    private void SaveOverride(string dir)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new StoredSettings { Directory = dir }));
    }

    private sealed class StoredSettings
    {
        public string? Directory { get; set; }
    }

    private static BackupInfo ToInfo(FileInfo fi) => new(
        fi.Name,
        fi.Extension.TrimStart('.').ToLowerInvariant(),
        fi.Length,
        fi.LastWriteTimeUtc);
}
