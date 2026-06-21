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
    private static void MapTagGroupEndpoints(RouteGroupBuilder api)
    {
        var tagGroups = api.MapGroup("/tag-groups").WithTags("TagGroups");

        tagGroups.MapGet("/", async (VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            // Two queries instead of one-with-correlated-subquery. The
            // earlier `db.Tags.Count(t => t.TagGroupId == g.Id)` inside
            // the projection threw 500 on certain Npgsql/EF combos
            // (specifically when TagGroups was empty or partially
            // seeded). Pre-fetching the counts as a separate dictionary
            // sidesteps the correlated-subquery translation entirely.
            var counts = await db.Tags.AsNoTracking()
                .GroupBy(t => t.TagGroupId)
                .Select(g => new { TagGroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TagGroupId, x => x.Count, ct);

            // For each group, count distinct videos that have at least
            // one tag from that group. Subtracting from the total
            // video count gives the "Missing / None" badge value the
            // browse sidebar shows next to the missing-leaf for each
            // group. Distinct over (TagGroupId, VideoId) avoids
            // counting a video twice if it has multiple tags in the
            // same group.
            var totalVideos = await db.Videos.CountAsync(ct);
            var videosWithTagInGroup = await db.VideoTags.AsNoTracking()
                .Where(vt => vt.Tag != null)
                .Select(vt => new { vt.Tag!.TagGroupId, vt.VideoId })
                .Distinct()
                .GroupBy(x => x.TagGroupId)
                .Select(g => new { TagGroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TagGroupId, x => x.Count, ct);

            var groups = await db.TagGroups.AsNoTracking()
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
                .Select(g => new { g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes, g.TextFormat })
                .ToListAsync(ct);

            var rows = groups.Select(g => new TagGroupDto(
                g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes,
                (Shared.Dto.TextFormatOption)(int)g.TextFormat,
                counts.GetValueOrDefault(g.Id, 0),
                Math.Max(0, totalVideos - videosWithTagInGroup.GetValueOrDefault(g.Id, 0)))).ToList();
            return Results.Ok(rows);
        }).Produces<List<TagGroupDto>>(StatusCodes.Status200OK)
          .WithName("ListTagGroups");

        tagGroups.MapGet("/{id:guid}", async (Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var g = await db.TagGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound();
            var count = await db.Tags.CountAsync(t => t.TagGroupId == id, ct);
            return Results.Ok(new TagGroupDto(g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes,
                (Shared.Dto.TextFormatOption)(int)g.TextFormat, count));
        }).Produces<TagGroupDto>(StatusCodes.Status200OK)
          .WithName("GetTagGroup");

        tagGroups.MapPost("/", async (
            CreateTagGroupRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (await db.TagGroups.AnyAsync(g => g.Name == req.Name, ct))
                return Results.Conflict(new { error = "A tag group with that name already exists." });
            var g = new TagGroup
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                AllowMultiple = req.AllowMultiple,
                DisplayAsCheckboxes = req.DisplayAsCheckboxes,
                SortOrder = req.SortOrder,
                Notes = req.Notes,
                TextFormat = (Domain.Models.TextFormatOption)(int)req.TextFormat
            };
            db.TagGroups.Add(g);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created TagGroup {TagGroupId} '{Name}' (allowMultiple={AllowMultiple}, checkboxes={Checkboxes}, textFormat={TextFormat})",
                g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.TextFormat);
            return Results.Created($"/api/tag-groups/{g.Id}",
                new TagGroupDto(g.Id, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes, g.SortOrder, g.Notes,
                    (Shared.Dto.TextFormatOption)(int)g.TextFormat, 0));
        }).Produces<TagGroupDto>(StatusCodes.Status201Created)
          .WithName("CreateTagGroup");

        tagGroups.MapPut("/{id:guid}", async (
            Guid id, UpdateTagGroupRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var g = await db.TagGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (await db.TagGroups.AnyAsync(x => x.Name == req.Name && x.Id != id, ct))
                return Results.Conflict(new { error = "A tag group with that name already exists." });

            // Capture meaningful before-state so we can flag policy changes —
            // particularly AllowMultiple flipping true→false, which can leave
            // existing videos with multi-tag assignments that violate the new
            // rule. The DB doesn't repair those automatically.
            var oldName = g.Name;
            var oldAllowMultiple = g.AllowMultiple;

            g.Name = req.Name;
            g.AllowMultiple = req.AllowMultiple;
            g.DisplayAsCheckboxes = req.DisplayAsCheckboxes;
            g.SortOrder = req.SortOrder;
            g.Notes = req.Notes;
            g.TextFormat = (Domain.Models.TextFormatOption)(int)req.TextFormat;
            await db.SaveChangesAsync(ct);

            if (oldAllowMultiple && !req.AllowMultiple)
            {
                // Count videos currently violating the new policy so the
                // operator can decide whether to clean them up.
                var orphans = await db.VideoTags
                    .Where(vt => vt.Tag!.TagGroupId == id)
                    .GroupBy(vt => vt.VideoId)
                    .Where(grp => grp.Count() > 1)
                    .CountAsync(ct);
                logger.LogWarning(
                    "TagGroup {TagGroupId} '{Name}' AllowMultiple flipped true→false — {ViolatingVideos} videos now have multi-tag assignments that violate the new single-value rule (existing rows are NOT cleaned up automatically)",
                    g.Id, g.Name, orphans);
            }

            logger.LogInformation(
                "Updated TagGroup {TagGroupId}: '{OldName}'→'{NewName}', allowMultiple={AllowMultiple}, checkboxes={Checkboxes}",
                g.Id, oldName, g.Name, g.AllowMultiple, g.DisplayAsCheckboxes);
            return Results.NoContent();
        }).WithName("UpdateTagGroup");

        tagGroups.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var g = await db.TagGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound();

            // Count what's about to cascade away so the audit log answers
            // "I deleted a TagGroup — what data went with it?".
            var tagCount = await db.Tags.CountAsync(t => t.TagGroupId == id, ct);
            var videoTagCount = await db.VideoTags.CountAsync(vt => vt.Tag!.TagGroupId == id, ct);
            var propDefCount = await db.PropertyDefinitions.CountAsync(p => p.TagGroupId == id, ct);

            db.TagGroups.Remove(g);  // cascades to tags + property defs
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Deleted TagGroup {TagGroupId} '{Name}' — cascaded {TagCount} tags ({VideoTagCount} VideoTag rows) and {PropertyDefinitionCount} property definitions",
                g.Id, g.Name, tagCount, videoTagCount, propDefCount);
            return Results.NoContent();
        }).WithName("DeleteTagGroup");
    }
}
