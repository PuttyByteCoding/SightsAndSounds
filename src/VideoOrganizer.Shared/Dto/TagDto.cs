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
    int VideoCount = 0,
    bool HiddenByDefault = false);

public record CreateTagRequest(
    Guid TagGroupId,
    string Name,
    IReadOnlyList<string>? Aliases = null,
    bool IsFavorite = false,
    int SortOrder = 0,
    string Notes = "",
    bool HiddenByDefault = false);

// Lightweight toggle for a tag's "hidden by default" flag (issue #84) — used
// by the filter-tree tag modal and the Hidden-by-default management page.
public record SetHiddenByDefaultRequest(bool Hidden);

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
    Guid? TagGroupId = null,
    bool HiddenByDefault = false);

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

// A tag suggested for a video because its name or one of its aliases was found
// in the video's file name or folder path (issue #10). Source tells the user
// where it matched ("File name" / "Folder"); MatchedText is the tag text that
// hit, for display. Tags already on the video are not suggested.
public record TagSuggestion(
    Guid TagId,
    Guid TagGroupId,
    string TagGroupName,
    string Name,
    string Source,
    string MatchedText);
