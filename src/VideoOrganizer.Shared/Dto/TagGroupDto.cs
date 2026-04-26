namespace VideoOrganizer.Shared.Dto;

// Wire shape for a TagGroup. TagCount is annotated when the endpoint loads
// counts (e.g. /tag-groups list); zero by default for callers that don't
// care.
public record TagGroupDto(
    Guid Id,
    string Name,
    bool AllowMultiple,
    bool DisplayAsCheckboxes,
    int SortOrder,
    string Notes,
    int TagCount = 0);

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
