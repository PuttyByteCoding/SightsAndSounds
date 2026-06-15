using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.IO;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Helpers;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

public static partial class ApiEndpoints
{
    private static void MapDuplicateEndpoints(RouteGroupBuilder api)
    {
        var duplicates = api.MapGroup("/duplicates").WithTags("Duplicates");

        // Project a candidate plus both fully-loaded videos. The videos
        // are loaded separately through IncludeForVideoDto so the tag /
        // property projections match every other Video endpoint.
        static DuplicateCandidateDto ToDuplicateDto(DuplicateCandidate d, Video a, Video b) =>
            new(d.Id, (DuplicateStatusDto)(int)d.Status, d.CreatedAt, ToDto(a), ToDto(b));

        duplicates.MapPost("/", async (
            CreateDuplicateCandidateRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (req.VideoAId == req.VideoBId)
                return Results.BadRequest(new { error = "A video cannot be a duplicate of itself." });

            // Normalize the pair ordering so (A,B) and (B,A) land on the
            // same row — the unique index then guarantees one row per pair.
            var (aId, bId) = req.VideoAId.CompareTo(req.VideoBId) < 0
                ? (req.VideoAId, req.VideoBId)
                : (req.VideoBId, req.VideoAId);

            var videos = await IncludeForVideoDto(db.Videos.AsNoTracking())
                .Where(v => v.Id == aId || v.Id == bId)
                .ToListAsync(ct);
            var a = videos.FirstOrDefault(v => v.Id == aId);
            var b = videos.FirstOrDefault(v => v.Id == bId);
            if (a is null || b is null)
                return Results.NotFound(new { error = "One or both videos not found." });

            var existing = await db.DuplicateCandidates
                .FirstOrDefaultAsync(d => d.VideoAId == aId && d.VideoBId == bId, ct);
            if (existing is not null)
            {
                // Idempotent: re-flagging an existing pair just returns it
                // (whatever its review status) instead of erroring — the
                // user pressing "mark" twice mid-hunt shouldn't see a 409.
                return Results.Ok(ToDuplicateDto(existing, a, b));
            }

            var candidate = new DuplicateCandidate { VideoAId = aId, VideoBId = bId };
            db.DuplicateCandidates.Add(candidate);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Flagged duplicate candidate {CandidateId}: {VideoAId} ('{FileA}') vs {VideoBId} ('{FileB}')",
                candidate.Id, aId, a.FileName, bId, b.FileName);
            return Results.Created($"/api/duplicates/{candidate.Id}", ToDuplicateDto(candidate, a, b));
        }).Produces<DuplicateCandidateDto>(StatusCodes.Status201Created)
          .WithName("CreateDuplicateCandidate");

        duplicates.MapGet("/", async (
            string? status, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            IQueryable<DuplicateCandidate> query = db.DuplicateCandidates.AsNoTracking();
            // status filter: pending / confirmed / rejected; omitted or
            // "all" returns everything.
            if (!string.IsNullOrWhiteSpace(status)
                && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<DuplicateStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                query = query.Where(d => d.Status == parsed);
            }
            var candidates = await query.OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
            if (candidates.Count == 0) return Results.Ok(Array.Empty<DuplicateCandidateDto>());

            // Load every referenced video once (with the standard tag /
            // property includes) instead of Include()-ing through both
            // navigations on every row.
            var videoIds = candidates.SelectMany(d => new[] { d.VideoAId, d.VideoBId }).Distinct().ToList();
            var videoById = await IncludeForVideoDto(db.Videos.AsNoTracking())
                .Where(v => videoIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, ct);

            // A cascade-deleted video can't leave an orphan row (FK), but
            // guard anyway so one inconsistent row can't 500 the page.
            var dtos = candidates
                .Where(d => videoById.ContainsKey(d.VideoAId) && videoById.ContainsKey(d.VideoBId))
                .Select(d => ToDuplicateDto(d, videoById[d.VideoAId], videoById[d.VideoBId]))
                .ToList();
            return Results.Ok(dtos);
        }).Produces<List<DuplicateCandidateDto>>(StatusCodes.Status200OK)
          .WithName("ListDuplicateCandidates");

        // Review transitions. Reopen lets a mis-click on Confirm/Reject be
        // undone without deleting and re-flagging the pair.
        async Task<IResult> SetDuplicateStatus(
            Guid id, DuplicateStatus newStatus, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct)
        {
            var d = await db.DuplicateCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            var old = d.Status;
            d.Status = newStatus;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Duplicate candidate {CandidateId} status {OldStatus} -> {NewStatus}", id, old, newStatus);

            var videos = await IncludeForVideoDto(db.Videos.AsNoTracking())
                .Where(v => v.Id == d.VideoAId || v.Id == d.VideoBId)
                .ToListAsync(ct);
            var a = videos.FirstOrDefault(v => v.Id == d.VideoAId);
            var b = videos.FirstOrDefault(v => v.Id == d.VideoBId);
            if (a is null || b is null) return Results.NoContent();
            return Results.Ok(ToDuplicateDto(d, a, b));
        }

        duplicates.MapPost("/{id:guid}/confirm",
            (Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
                SetDuplicateStatus(id, DuplicateStatus.Confirmed, db, logger, ct))
            .Produces<DuplicateCandidateDto>(StatusCodes.Status200OK)
            .WithName("ConfirmDuplicateCandidate");

        duplicates.MapPost("/{id:guid}/reject",
            (Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
                SetDuplicateStatus(id, DuplicateStatus.Rejected, db, logger, ct))
            .Produces<DuplicateCandidateDto>(StatusCodes.Status200OK)
            .WithName("RejectDuplicateCandidate");

        duplicates.MapPost("/{id:guid}/reopen",
            (Guid id, VideoOrganizerDbContext db, ILogger<Program> logger, CancellationToken ct) =>
                SetDuplicateStatus(id, DuplicateStatus.Pending, db, logger, ct))
            .Produces<DuplicateCandidateDto>(StatusCodes.Status200OK)
            .WithName("ReopenDuplicateCandidate");

        duplicates.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var d = await db.DuplicateCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            db.DuplicateCandidates.Remove(d);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Deleted duplicate candidate {CandidateId} ({Status})", id, d.Status);
            return Results.NoContent();
        }).WithName("DeleteDuplicateCandidate");
    }
}
