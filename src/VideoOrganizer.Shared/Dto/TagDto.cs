namespace VideoOrganizer.Shared.Dto;

// Wire shape for a Tag. VideoCount populated when listing for the Tag
// Management page (/tags?withCounts=true), 0 elsewhere.
public record TagDto(
    Guid Id,
    Guid TagGroupId,
    string TagGroupName,
    string Name,
    IReadOnlyList<string> Aliases,
    bool IsFavorite,
    int SortOrder,
    string Notes,
    int VideoCount = 0);

public record CreateTagRequest(
    Guid TagGroupId,
    string Name,
    IReadOnlyList<string>? Aliases = null,
    bool IsFavorite = false,
    int SortOrder = 0,
    string Notes = "");

// Create many tags in one request (issue #49). Names are trimmed; blanks are
// ignored; names that collide with an existing tag in the group (or repeat
// earlier in the list), case-insensitively, are skipped rather than erroring.
public record BulkCreateTagsRequest(
    Guid TagGroupId,
    IReadOnlyList<string> Names,
    bool IsFavorite = false);

public record BulkCreateTagsResponse(
    int Created,
    int Skipped);

// TagGroupId moves the tag to another group when it differs from the
// tag's current group. Existing VideoTag rows reference the tag by id,
// so every video keeps its tagging across the move. Null / omitted
// (the pre-move wire shape) leaves the group unchanged.
public record UpdateTagRequest(
    string Name,
    IReadOnlyList<string> Aliases,
    bool IsFavorite,
    int SortOrder,
    string Notes,
    Guid? TagGroupId = null);

// Merge sources into target. All Videos referencing any source tag are
// re-pointed at target; sources are then deleted. Target must not appear
// in sources, and all tags must belong to the same TagGroup.
public record MergeTagsRequest(Guid[] SourceIds, Guid TargetId);

// One row in /tags/search results.
public record TagSearchHit(
    Guid TagId,
    Guid TagGroupId,
    string TagGroupName,
    string Name,
    IReadOnlyList<string> Aliases);
