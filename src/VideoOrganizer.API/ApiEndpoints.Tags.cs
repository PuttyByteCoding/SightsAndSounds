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
    private static void MapTagEndpoints(RouteGroupBuilder api)
    {
        var tagsGroup = api.MapGroup("/tags").WithTags("Tags");

        // GET /api/tags?groupId=&withCounts=&q=
        tagsGroup.MapGet("/", async (
            Guid? groupId,
            bool? withCounts,
            string? q,
            VideoOrganizerDbContext db,
            CancellationToken ct) =>
        {
            IQueryable<Tag> query = db.Tags.AsNoTracking().Include(t => t.TagGroup);
            if (groupId.HasValue) query = query.Where(t => t.TagGroupId == groupId.Value);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var lower = q.Trim().ToLower();
                query = query.Where(t => t.Name.ToLower().Contains(lower));
            }
            var rows = await query
                .OrderBy(t => t.TagGroup!.SortOrder)
                .ThenBy(t => t.SortOrder)
                .ThenBy(t => t.Name)
                .ToListAsync(ct);

            Dictionary<Guid, int> counts = new();
            if (withCounts == true)
            {
                var tagIds = rows.Select(t => t.Id).ToList();
                counts = await db.VideoTags.AsNoTracking()
                    .Where(vt => tagIds.Contains(vt.TagId))
                    .GroupBy(vt => vt.TagId)
                    .Select(g => new { TagId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.TagId, x => x.Count, ct);
            }

            var dtos = rows.Select(t => new TagDto(
                t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty,
                t.Name, t.Aliases, t.IsFavorite, t.SortOrder, t.Notes,
                counts.TryGetValue(t.Id, out var c) ? c : 0, t.HiddenByDefault)).ToList();
            return Results.Ok(dtos);
        }).Produces<List<TagDto>>(StatusCodes.Status200OK)
          .WithName("ListTags");

        tagsGroup.MapGet("/{id:guid}", async (Guid id, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            var t = await db.Tags.AsNoTracking()
                .Include(x => x.TagGroup)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            var count = await db.VideoTags.CountAsync(vt => vt.TagId == id, ct);
            return Results.Ok(new TagDto(
                t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty,
                t.Name, t.Aliases, t.IsFavorite, t.SortOrder, t.Notes, count, t.HiddenByDefault));
        }).Produces<TagDto>(StatusCodes.Status200OK)
          .WithName("GetTag");

        tagsGroup.MapPost("/", async (
            CreateTagRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (!await db.TagGroups.AnyAsync(g => g.Id == req.TagGroupId, ct))
                return Results.BadRequest(new { error = "TagGroup not found." });
            if (await db.Tags.AnyAsync(t => t.TagGroupId == req.TagGroupId && t.Name == req.Name, ct))
                return Results.Conflict(new { error = "A tag with that name already exists in this group." });

            var t = new Tag
            {
                Id = Guid.NewGuid(),
                TagGroupId = req.TagGroupId,
                Name = req.Name,
                Aliases = req.Aliases?.ToList() ?? new(),
                IsFavorite = req.IsFavorite,
                SortOrder = req.SortOrder,
                Notes = req.Notes,
                HiddenByDefault = req.HiddenByDefault
            };
            db.Tags.Add(t);
            await db.SaveChangesAsync(ct);

            var grp = await db.TagGroups.AsNoTracking().FirstAsync(g => g.Id == t.TagGroupId, ct);
            logger.LogInformation(
                "Created Tag {TagId} '{Name}' in TagGroup {TagGroupId} '{GroupName}' ({AliasCount} aliases)",
                t.Id, t.Name, t.TagGroupId, grp.Name, t.Aliases.Count);
            return Results.Created($"/api/tags/{t.Id}",
                new TagDto(t.Id, t.TagGroupId, grp.Name, t.Name, t.Aliases, t.IsFavorite, t.SortOrder, t.Notes, 0, t.HiddenByDefault));
        }).Produces<TagDto>(StatusCodes.Status201Created)
          .WithName("CreateTag");

        // POST /api/tags/bulk — create many tags in one request (issue #49).
        // The Tag Management paste box used to fire one POST per name, which
        // fell over for thousands of tags. This inserts the whole batch in a
        // single round-trip. Names are trimmed; blanks ignored; names that
        // collide with an existing tag in the group (or repeat earlier in the
        // batch), case-insensitively, are skipped so the per-group unique-name
        // rule can't trip the insert.
        tagsGroup.MapPost("/bulk", async (
            BulkCreateTagsRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!await db.TagGroups.AnyAsync(g => g.Id == req.TagGroupId, ct))
                return Results.BadRequest(new { error = "TagGroup not found." });

            var seen = new HashSet<string>(
                await db.Tags.Where(t => t.TagGroupId == req.TagGroupId)
                    .Select(t => t.Name).ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            var toAdd = new List<Tag>();
            var skipped = 0;
            foreach (var raw in req.Names ?? Array.Empty<string>())
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(name)) { skipped++; continue; }
                toAdd.Add(new Tag
                {
                    Id = Guid.NewGuid(),
                    TagGroupId = req.TagGroupId,
                    Name = name,
                    Aliases = new(),
                    IsFavorite = req.IsFavorite,
                    SortOrder = 0,
                    Notes = string.Empty
                });
            }

            if (toAdd.Count > 0)
            {
                db.Tags.AddRange(toAdd);
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation(
                "Bulk-created {Created} tag(s) in TagGroup {TagGroupId} ({Skipped} skipped)",
                toAdd.Count, req.TagGroupId, skipped);
            return Results.Ok(new BulkCreateTagsResponse(toAdd.Count, skipped));
        }).Produces<BulkCreateTagsResponse>(StatusCodes.Status200OK)
          .WithName("BulkCreateTags");

        tagsGroup.MapPut("/{id:guid}", async (
            Guid id, UpdateTagRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });

            // Optional group move. VideoTag rows reference the tag by id, so
            // re-pointing TagGroupId carries every existing video tagging
            // into the new group untouched — exactly the contract a "I made
            // this tag in the wrong group" fix needs. The name-uniqueness
            // check below runs against the TARGET group so the move can't
            // land on a name collision.
            var targetGroupId = req.TagGroupId ?? t.TagGroupId;
            if (targetGroupId != t.TagGroupId
                && !await db.TagGroups.AnyAsync(g => g.Id == targetGroupId, ct))
            {
                return Results.BadRequest(new { error = "Target TagGroup not found." });
            }
            if (await db.Tags.AnyAsync(x => x.TagGroupId == targetGroupId && x.Name == req.Name && x.Id != id, ct))
                return Results.Conflict(new { error = "A tag with that name already exists in this group." });

            // Capture before-state so the log can show what actually changed —
            // a tag rename + alias edit affects search/typeahead behavior, so
            // it's worth being able to grep for "when did this tag get
            // renamed".
            var oldName = t.Name;
            var oldAliasCount = t.Aliases.Count;
            var oldGroupId = t.TagGroupId;

            t.Name = req.Name;
            t.Aliases = req.Aliases.ToList();
            t.IsFavorite = req.IsFavorite;
            t.SortOrder = req.SortOrder;
            t.Notes = req.Notes;
            // Only change the hidden flag when the caller actually sent it;
            // omitting it leaves the tag's current value intact (#194).
            if (req.HiddenByDefault.HasValue) t.HiddenByDefault = req.HiddenByDefault.Value;
            t.TagGroupId = targetGroupId;
            await db.SaveChangesAsync(ct);

            if (oldGroupId != t.TagGroupId)
            {
                var attachedVideos = await db.VideoTags.CountAsync(vt => vt.TagId == id, ct);
                logger.LogInformation(
                    "Moved Tag {TagId} '{Name}' from TagGroup {OldGroupId} to {NewGroupId} ({AttachedVideos} video taggings preserved)",
                    t.Id, t.Name, oldGroupId, t.TagGroupId, attachedVideos);
            }
            if (!string.Equals(oldName, t.Name, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Renamed Tag {TagId} '{OldName}'→'{NewName}' (aliases {OldAliasCount}→{NewAliasCount})",
                    t.Id, oldName, t.Name, oldAliasCount, t.Aliases.Count);
            }
            else if (oldAliasCount != t.Aliases.Count)
            {
                logger.LogInformation(
                    "Updated Tag {TagId} '{Name}' aliases ({OldAliasCount}→{NewAliasCount})",
                    t.Id, t.Name, oldAliasCount, t.Aliases.Count);
            }
            return Results.NoContent();
        }).WithName("UpdateTag");

        // Toggle just the "hidden by default" flag (issue #84). Lightweight so
        // the filter-tree tag modal and the Hidden-by-default page can flip it
        // without round-tripping a full tag edit.
        tagsGroup.MapPut("/{id:guid}/hidden-by-default", async (
            Guid id, SetHiddenByDefaultRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();
            t.HiddenByDefault = req.Hidden;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Tag {TagId} '{Name}' HiddenByDefault set to {Hidden}", t.Id, t.Name, req.Hidden);
            return Results.NoContent();
        }).WithName("SetTagHiddenByDefault");

        tagsGroup.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound();

            // Pre-count cascaded rows so the audit log includes the blast
            // radius, not just an opaque "deleted".
            var videoTagCount = await db.VideoTags.CountAsync(vt => vt.TagId == id, ct);
            var propValueCount = await db.TagPropertyValues.CountAsync(pv => pv.TagId == id, ct);

            db.Tags.Remove(t);  // cascades VideoTag + TagPropertyValue
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Deleted Tag {TagId} '{Name}' (group {TagGroupId}) — cascaded {VideoTagCount} VideoTag rows and {PropertyValueCount} TagPropertyValue rows",
                t.Id, t.Name, t.TagGroupId, videoTagCount, propValueCount);
            return Results.NoContent();
        }).WithName("DeleteTag");

        tagsGroup.MapPost("/merge", async (
            MergeTagsRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (req.SourceIds.Contains(req.TargetId))
            {
                logger.LogWarning(
                    "Tag merge rejected — target {TargetId} is also listed as a source",
                    req.TargetId);
                return Results.BadRequest(new { error = "Target must not be in sources." });
            }
            var target = await db.Tags.FirstOrDefaultAsync(t => t.Id == req.TargetId, ct);
            if (target is null) return Results.NotFound(new { error = "Target tag not found." });
            var sources = await db.Tags.Where(t => req.SourceIds.Contains(t.Id)).ToListAsync(ct);
            if (sources.Any(s => s.TagGroupId != target.TagGroupId))
            {
                logger.LogWarning(
                    "Tag merge rejected — sources {SourceIds} span TagGroups, but target {TargetId} is in TagGroup {TargetGroupId}",
                    sources.Select(s => s.Id).ToArray(), target.Id, target.TagGroupId);
                return Results.BadRequest(new { error = "All merged tags must belong to the same group." });
            }

            // Re-point video_tags from sources to target. VideoTag's PK is
            // (VideoId, TagId), so EF Core won't let us mutate TagId in place
            // — we delete the source rows and insert fresh target rows for
            // any video that didn't already have the target.
            var srcIds = sources.Select(s => s.Id).ToList();
            var affected = await db.VideoTags
                .Where(vt => srcIds.Contains(vt.TagId))
                .ToListAsync(ct);
            var alreadyHasTarget = await db.VideoTags
                .Where(vt => vt.TagId == target.Id)
                .Select(vt => vt.VideoId)
                .ToListAsync(ct);
            var alreadySet = new HashSet<Guid>(alreadyHasTarget);
            var newlyAttached = new HashSet<Guid>();

            foreach (var vt in affected)
            {
                db.VideoTags.Remove(vt);
                if (!alreadySet.Contains(vt.VideoId) && newlyAttached.Add(vt.VideoId))
                {
                    db.VideoTags.Add(new VideoTag { VideoId = vt.VideoId, TagId = target.Id });
                }
            }

            // Fold each source's name and aliases into the target's alias list
            // so the search/typeahead can still find the target by what users
            // typed for the merged-away tags. Case-insensitive dedup, and we
            // never add anything that collides with the target's own name.
            var existing = new HashSet<string>(target.Aliases, StringComparer.OrdinalIgnoreCase);
            foreach (var s in sources)
            {
                if (!string.Equals(s.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                    && existing.Add(s.Name))
                {
                    target.Aliases.Add(s.Name);
                }
                foreach (var a in s.Aliases)
                {
                    if (!string.Equals(a, target.Name, StringComparison.OrdinalIgnoreCase)
                        && existing.Add(a))
                    {
                        target.Aliases.Add(a);
                    }
                }
            }

            db.Tags.RemoveRange(sources);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Merged tags {SourceIds} into {TargetId} ('{TargetName}', group {TargetGroupId}) — re-pointed {RepointedRows} VideoTag rows ({NewlyAttached} new attachments, {SkippedDuplicates} skipped because target was already attached), folded source names/aliases into target",
                sources.Select(s => s.Id).ToArray(),
                target.Id, target.Name, target.TagGroupId,
                affected.Count, newlyAttached.Count, affected.Count - newlyAttached.Count);
            return Results.Ok(new { mergedVideos = affected.Count, removedSources = sources.Count });
        }).WithName("MergeTags");

        // Unified search across all tag groups for the filter chip picker.
        tagsGroup.MapGet("/search", async (
            string q, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<TagSearchHit>());
            var lower = q.Trim().ToLower();
            const int limit = 40;

            var matches = await db.Tags.AsNoTracking()
                .Include(t => t.TagGroup)
                .Where(t => t.Name.ToLower().Contains(lower))
                .OrderBy(t => t.TagGroup!.SortOrder)
                .ThenBy(t => t.Name)
                .Take(limit)
                .ToListAsync(ct);

            var hits = matches.Select(t => new TagSearchHit(
                t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty, t.Name, t.Aliases)).ToList();

            // Fall back to alias matching if name-only didn't fill the limit.
            if (hits.Count < limit)
            {
                var seen = hits.Select(h => h.TagId).ToHashSet();
                var aliasCandidates = await db.Tags.AsNoTracking()
                    .Include(t => t.TagGroup)
                    .Where(t => !seen.Contains(t.Id))
                    .ToListAsync(ct);
                foreach (var t in aliasCandidates)
                {
                    if (hits.Count >= limit) break;
                    if (t.Aliases.Any(a => a.ToLower().Contains(lower)))
                    {
                        hits.Add(new TagSearchHit(
                            t.Id, t.TagGroupId, t.TagGroup?.Name ?? string.Empty, t.Name, t.Aliases));
                    }
                }
            }

            return Results.Ok(hits);
        }).Produces<List<TagSearchHit>>(StatusCodes.Status200OK)
          .WithName("SearchTags");

        // Replace property values on a tag.
        tagsGroup.MapPut("/{id:guid}/properties", async (
            Guid id, SetPropertyValuesRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var tag = await db.Tags.Include(t => t.PropertyValues)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();
            await ReplaceTagPropertiesAsync(db, tag, req.Values, logger, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("SetTagProperties");
    }
}
