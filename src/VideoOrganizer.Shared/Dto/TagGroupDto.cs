namespace VideoOrganizer.Shared.Dto;

// Wire shape for a TagGroup. TagCount and VideosMissingCount are
// annotated when the endpoint loads counts (e.g. /tag-groups list);
// zero by default for callers that don't care. VideosMissingCount is
// the number of videos that have no tag from this group — used to
// drive the "Missing / None" leaf badge in the browse sidebar.
public record TagGroupDto(
    Guid Id,
    string Name,
    bool AllowMultiple,
    bool DisplayAsCheckboxes,
    int SortOrder,
    string Notes,
    int TagCount = 0,
    int VideosMissingCount = 0);

public record CreateTagGroupRequest(
    string Name,
    bool AllowMultiple = true,
    bool DisplayAsCheckboxes = false,
    int SortOrder = 0,
    string Notes = "");

public record UpdateTagGroupRequest(
    string Name,
    bool AllowMultiple,
    bool DisplayAsCheckboxes,
    int SortOrder,
    string Notes);
