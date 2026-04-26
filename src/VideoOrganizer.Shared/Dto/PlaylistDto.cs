namespace VideoOrganizer.Shared.Dto;

public record PlaylistDto(
    Guid Id,
    List<Guid> VideoIds,
    DateTime CreatedAt
);

public record PlaylistNavigationDto(
    Guid CurrentVideoId,
    Guid? NextVideoId,
    Guid? PreviousVideoId,
    int CurrentIndex,
    int TotalCount
);
