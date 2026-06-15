using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.IO;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

// VideoSet (source root) endpoints, including re-root (issue #32/#96). Split out
// of the ApiEndpoints monolith — see MapApiEndpoints in ApiEndpoints.cs.
public static partial class ApiEndpoints
{
    private static void MapVideoSetEndpoints(RouteGroupBuilder api)
    {
        var videoSets = api.MapGroup("/video-sets").WithTags("VideoSets");

        videoSets.MapGet("/", async (
            VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            // Same per-row resilience as /import/browse: a single bad
            // path (permission-denied, broken symlink, unreachable
            // mount) should not 500 the whole listing. Project to an
            // anonymous shape outside the EF query so the PathExists
            // probe runs once per row in C# memory, with TryDirectoryExists
            // already swallowing filesystem failures.
            var sets = await db.VideoSets
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                .ToListAsync(ct);
            var result = sets.Select(s => new
            {
                s.Id,
                s.Name,
                s.Path,
                s.Enabled,
                s.SortOrder,
                PathExists = TryDirectoryExists(s.Path, logger)
            }).ToList();
            return Results.Ok(result);
        }).WithName("ListVideoSets");

        videoSets.MapPost("/", async (
            VideoSet input, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            input.Path = PathNormalizer.Normalize(input.Path ?? string.Empty);
            var error = ValidateVideoSet(input, db, currentId: null);
            if (error is not null) return Results.BadRequest(new { error });

            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            db.VideoSets.Add(input);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created VideoSet {VideoSetId} '{Name}' at {Path} (enabled={Enabled})",
                input.Id, input.Name, input.Path, input.Enabled);
            return Results.Created($"/api/video-sets/{input.Id}", input);
        }).WithName("CreateVideoSet");

        videoSets.MapPut("/{id:guid}", async (
            Guid id, VideoSet input, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var existing = await db.VideoSets.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (existing is null) return Results.NotFound();

            var error = ValidateVideoSet(input, db, currentId: id);
            if (error is not null) return Results.BadRequest(new { error });

            // Capture before-state. Path changes are especially worth flagging
            // because every Video below the old path becomes an orphan; the
            // app uses Path as a prefix lookup, not an FK.
            var oldPath = existing.Path;
            var oldEnabled = existing.Enabled;

            existing.Name = input.Name;
            existing.Path = PathNormalizer.Normalize(input.Path ?? string.Empty);
            existing.Enabled = input.Enabled;
            existing.SortOrder = input.SortOrder;
            await db.SaveChangesAsync(ct);

            if (!string.Equals(oldPath, existing.Path, StringComparison.OrdinalIgnoreCase))
            {
                var orphans = await db.Videos.CountAsync(v => v.FilePath.StartsWith(oldPath), ct);
                logger.LogWarning(
                    "VideoSet {VideoSetId} '{Name}' Path changed {OldPath}→{NewPath} — {OrphanCount} videos still point at the old prefix and won't be browsable until they're moved or re-rooted",
                    existing.Id, existing.Name, oldPath, existing.Path, orphans);
            }
            else if (oldEnabled != existing.Enabled)
            {
                logger.LogInformation(
                    "VideoSet {VideoSetId} '{Name}' enabled={Enabled} (was {OldEnabled})",
                    existing.Id, existing.Name, existing.Enabled, oldEnabled);
            }
            return Results.Ok(existing);
        }).WithName("UpdateVideoSet");

        videoSets.MapGet("/{id:guid}/orphan-count",
            async (Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var set = await db.VideoSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (set is null) return Results.NotFound();
            var count = await db.Videos.CountAsync(v => v.FilePath.StartsWith(set.Path), ct);
            return Results.Ok(new { count });
        }).WithName("GetVideoSetOrphanCount");

        videoSets.MapDelete("/{id:guid}", async (
            Guid id, bool? force, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var set = await db.VideoSets.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (set is null) return Results.NotFound();

            var orphanCount = await db.Videos.CountAsync(v => v.FilePath.StartsWith(set.Path), ct);
            if (orphanCount > 0 && force != true)
            {
                logger.LogWarning(
                    "VideoSet {VideoSetId} ({Name}, {Path}) delete blocked — would orphan {OrphanCount} videos. Caller must retry with ?force=true to override.",
                    set.Id, set.Name, set.Path, orphanCount);
                return Results.Conflict(new { orphanCount, error = "Deleting this set would orphan videos. Pass ?force=true to proceed." });
            }

            logger.LogInformation(
                "VideoSet {VideoSetId} ({Name}, {Path}) deleted{ForcedSuffix} — {OrphanCount} videos now point at a missing root",
                set.Id, set.Name, set.Path,
                orphanCount > 0 ? " (forced)" : string.Empty,
                orphanCount);
            db.VideoSets.Remove(set);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteVideoSet");

        // Dry-run spot check for a proposed re-root (issue #32). Counts the
        // videos that would be repointed and stats a small sample at the new
        // base so the caller can confirm the mapping before committing. Reads
        // only — never touches the DB or files.
        videoSets.MapPost("/{id:guid}/re-root/preview", async (
            Guid id, ReRootRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var set = await db.VideoSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (set is null) return Results.NotFound();

            var newBase = PathNormalizer.Normalize(req.NewPath ?? string.Empty).TrimEnd('/');
            if (newBase.Length == 0) return Results.BadRequest(new { error = "A new path is required." });
            var oldBase = PathNormalizer.Normalize(set.Path).TrimEnd('/');
            var oldPrefix = oldBase + "/";

            var query = db.Videos.AsNoTracking()
                .Where(v => v.FilePath == oldBase || v.FilePath.StartsWith(oldPrefix));
            var total = await query.CountAsync(ct);
            const int sampleSize = 10;
            var sample = await query
                .OrderBy(v => v.FilePath).Take(sampleSize)
                .Select(v => v.FilePath).ToListAsync(ct);

            var examples = new List<ReRootPreviewItem>(sample.Count);
            var found = 0;
            foreach (var oldPath in sample)
            {
                var newPath = newBase + oldPath[oldBase.Length..];
                bool exists;
                try { exists = File.Exists(newPath); }
                catch { exists = false; }
                if (exists) found++;
                examples.Add(new ReRootPreviewItem(oldPath, newPath, exists));
            }

            return Results.Ok(new ReRootPreview(
                total, sample.Count, found, sample.Count - found,
                TryDirectoryExists(newBase, logger), examples));
        }).WithName("ReRootVideoSetPreview");

        // Commit a re-root (issue #32): change the source Path and rewrite the
        // FilePath prefix on every video under it, atomically. This is what
        // lets a source "move" (e.g. S:/Videos → /mnt/videos after migrating
        // to Linux) without orphaning the library — every Video.FilePath is an
        // absolute path matched by prefix, not an FK, so the source path and
        // the child paths must change together.
        videoSets.MapPost("/{id:guid}/re-root", async (
            Guid id, ReRootRequest req, VideoOrganizerDbContext db,
            ThumbnailWarmingSignal thumbSignal,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var set = await db.VideoSets.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (set is null) return Results.NotFound();

            var newBase = PathNormalizer.Normalize(req.NewPath ?? string.Empty).TrimEnd('/');
            if (newBase.Length == 0) return Results.BadRequest(new { error = "A new path is required." });
            var oldBase = PathNormalizer.Normalize(set.Path).TrimEnd('/');
            if (string.Equals(oldBase, newBase, StringComparison.Ordinal))
            {
                // Nothing to move — only normalization differed (or no change).
                set.Path = newBase;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { reRooted = 0, newPath = newBase });
            }

            var oldPrefix = oldBase + "/";
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Single UPDATE: swap the old base prefix for the new one on every
            // child path. Substring(oldBase.Length) keeps the leading slash +
            // the rest of the relative path intact.
            var reRooted = await db.Videos
                .Where(v => v.FilePath == oldBase || v.FilePath.StartsWith(oldPrefix))
                .ExecuteUpdateAsync(s => s.SetProperty(
                    v => v.FilePath, v => newBase + v.FilePath.Substring(oldBase.Length)), ct);
            set.Path = newBase;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "VideoSet {VideoSetId} '{Name}' re-rooted {OldPath}→{NewPath} — {Count} videos repointed",
                set.Id, set.Name, oldBase, newBase, reRooted);

            // Wake the thumbnail warmer (issue #96). Re-rooting is the "moved to
            // a new machine" path: the source files were unreachable at their old
            // paths when the warmer last ran (so it skipped them and went to
            // sleep), and the sprite cache from the old machine isn't here. Now
            // that the paths point at reachable files again, signal the worker so
            // it regenerates the missing sprites instead of leaving them 404ing.
            thumbSignal.Signal();
            return Results.Ok(new { reRooted, newPath = newBase });
        }).WithName("ReRootVideoSet");
    }
}
