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
    private static void MapPropertyEndpoints(RouteGroupBuilder api)
    {
        var props = api.MapGroup("/properties").WithTags("Properties");

        props.MapGet("/", async (Guid? tagGroupId, VideoOrganizerDbContext db, CancellationToken ct) =>
        {
            IQueryable<PropertyDefinition> q = db.PropertyDefinitions.AsNoTracking();
            if (tagGroupId.HasValue) q = q.Where(p => p.TagGroupId == tagGroupId.Value);
            var rows = await q
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
                .Select(p => new PropertyDefinitionDto(
                    p.Id, p.Name, (PropertyDataTypeDto)p.DataType, (PropertyScopeDto)p.Scope,
                    p.TagGroupId, p.Required, p.SortOrder, p.Notes))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).Produces<List<PropertyDefinitionDto>>(StatusCodes.Status200OK)
          .WithName("ListProperties");

        props.MapPost("/", async (
            CreatePropertyDefinitionRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (req.Scope == PropertyScopeDto.Tag && !req.TagGroupId.HasValue)
                return Results.BadRequest(new { error = "Tag-scoped properties must specify a TagGroupId." });
            if (req.Scope == PropertyScopeDto.Video && req.TagGroupId.HasValue)
                return Results.BadRequest(new { error = "Video-scoped properties must not specify a TagGroupId." });
            if (req.TagGroupId.HasValue && !await db.TagGroups.AnyAsync(g => g.Id == req.TagGroupId, ct))
                return Results.BadRequest(new { error = "TagGroup not found." });

            var def = new PropertyDefinition
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                DataType = (PropertyDataType)req.DataType,
                Scope = (PropertyScope)req.Scope,
                TagGroupId = req.TagGroupId,
                Required = req.Required,
                SortOrder = req.SortOrder,
                Notes = req.Notes
            };
            db.PropertyDefinitions.Add(def);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created PropertyDefinition {PropertyId} '{Name}' (scope={Scope}, dataType={DataType}, tagGroup={TagGroupId}, required={Required})",
                def.Id, def.Name, def.Scope, def.DataType, def.TagGroupId, def.Required);
            return Results.Created($"/api/properties/{def.Id}",
                new PropertyDefinitionDto(def.Id, def.Name,
                    (PropertyDataTypeDto)def.DataType, (PropertyScopeDto)def.Scope,
                    def.TagGroupId, def.Required, def.SortOrder, def.Notes));
        }).Produces<PropertyDefinitionDto>(StatusCodes.Status201Created)
          .WithName("CreateProperty");

        props.MapPut("/{id:guid}", async (
            Guid id, UpdatePropertyDefinitionRequest req, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var def = await db.PropertyDefinitions.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (def is null) return Results.NotFound();
            var oldName = def.Name;
            var oldDataType = def.DataType;
            def.Name = req.Name;
            def.DataType = (PropertyDataType)req.DataType;
            def.Required = req.Required;
            def.SortOrder = req.SortOrder;
            def.Notes = req.Notes;
            await db.SaveChangesAsync(ct);

            // DataType changes are especially worth flagging — existing
            // string values stay in the DB and may no longer parse under
            // the new type. Operator should know.
            if (oldDataType != def.DataType)
            {
                logger.LogWarning(
                    "PropertyDefinition {PropertyId} '{Name}' DataType changed {OldDataType}→{NewDataType} — existing values are NOT re-validated",
                    def.Id, def.Name, oldDataType, def.DataType);
            }
            else if (!string.Equals(oldName, def.Name, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Renamed PropertyDefinition {PropertyId} '{OldName}'→'{NewName}'",
                    def.Id, oldName, def.Name);
            }
            return Results.NoContent();
        }).WithName("UpdateProperty");

        props.MapDelete("/{id:guid}", async (
            Guid id, VideoOrganizerDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var def = await db.PropertyDefinitions.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (def is null) return Results.NotFound();

            // Count cascaded value rows before removing.
            var videoValueCount = await db.VideoPropertyValues.CountAsync(v => v.PropertyDefinitionId == id, ct);
            var tagValueCount = await db.TagPropertyValues.CountAsync(t => t.PropertyDefinitionId == id, ct);

            db.PropertyDefinitions.Remove(def);  // cascades to value rows
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Deleted PropertyDefinition {PropertyId} '{Name}' (scope={Scope}) — cascaded {VideoValueCount} VideoPropertyValue rows and {TagValueCount} TagPropertyValue rows",
                def.Id, def.Name, def.Scope, videoValueCount, tagValueCount);
            return Results.NoContent();
        }).WithName("DeleteProperty");
    }
}
