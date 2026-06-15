using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

// Backup / restore endpoints (issue #32). Split out of the ApiEndpoints
// monolith — see MapApiEndpoints in ApiEndpoints.cs, which calls this.
public static partial class ApiEndpoints
{
    private static void MapBackupEndpoints(RouteGroupBuilder api)
    {
        // === Backups (issue #32) ============================================
        var backup = api.MapGroup("/backup").WithTags("Backup");

        // Quick local snapshot: dump every table to a timestamped JSON file in
        // the backups folder.
        backup.MapPost("/snapshot", async (
            VideoOrganizerDbContext db, BackupService svc,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var info = await svc.CreateJsonSnapshotAsync(db, ct);
            logger.LogInformation(
                "Created JSON snapshot backup {File} ({Bytes} bytes)", info.FileName, info.SizeBytes);
            return Results.Ok(info);
        }).WithName("CreateBackupSnapshot");

        backup.MapGet("/", (BackupService svc) => Results.Ok(svc.List())).WithName("ListBackups");

        // Where backups are written (a host directory — the API runs locally,
        // so no volume needed). GET reports it + whether it's writable; PUT
        // changes it (validated + persisted).
        backup.MapGet("/settings", (BackupService svc) => Results.Ok(svc.GetSettings()))
            .WithName("GetBackupSettings");

        backup.MapPut("/settings", (SetBackupDirectoryRequest req, BackupService svc) =>
        {
            try
            {
                return Results.Ok(svc.SetDirectory(req.Directory));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("SetBackupDirectory");

        backup.MapGet("/{fileName}/download", (string fileName, BackupService svc) =>
        {
            var path = svc.ResolvePath(fileName);
            if (path is null) return Results.NotFound();
            var contentType = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "application/octet-stream";
            return Results.File(path, contentType, fileName);
        }).WithName("DownloadBackup");

        backup.MapDelete("/{fileName}", (string fileName, BackupService svc) =>
            svc.Delete(fileName) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteBackup");

        // Restore the whole database from a JSON snapshot (issue #32). Takes a
        // pre-restore safety snapshot first so a bad restore is undoable, then
        // replaces every table from the file in one transaction. After this the
        // caller should re-check each source's path (a snapshot from another
        // machine carries that machine's paths) and re-root any that moved.
        backup.MapPost("/{fileName}/restore", async (
            string fileName, VideoOrganizerDbContext db, BackupService svc,
            ThumbnailWarmingSignal thumbSignal,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            DbSnapshot snap;
            try
            {
                snap = await svc.ReadSnapshotAsync(fileName, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            // Safety net: snapshot the current DB before overwriting it.
            BackupInfo safety;
            try
            {
                safety = await svc.CreateJsonSnapshotAsync(db, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = $"Aborted — couldn't take a pre-restore safety snapshot: {ex.Message}"
                });
            }

            try
            {
                await svc.RestoreAsync(db, snap, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Restore from {File} failed; data left unchanged", fileName);
                return Results.Problem(
                    $"Restore failed: {ex.Message}. Your data is unchanged; a safety snapshot was saved as {safety.FileName}.");
            }

            logger.LogWarning(
                "Restored database from {File} — {Videos} videos, {Tags} tags, {Sets} sources. Pre-restore safety snapshot: {Safety}",
                fileName, snap.Videos.Count, snap.Tags.Count, snap.VideoSets.Count, safety.FileName);

            // Wake the thumbnail warmer (issue #96). The restored sprite cache
            // lives on whatever machine made the snapshot, not here. If the
            // restored paths are already reachable on this machine the worker can
            // regenerate immediately; if they aren't, it harmlessly finds nothing
            // pending and a later re-root will signal it again.
            thumbSignal.Signal();
            return Results.Ok(new
            {
                restoredFrom = fileName,
                safetySnapshot = safety.FileName,
                videos = snap.Videos.Count,
                tags = snap.Tags.Count,
                videoSets = snap.VideoSets.Count
            });
        }).WithName("RestoreBackup");
    }
}
