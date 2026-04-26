namespace VideoOrganizer.Shared.Dto;

public record ImportProgressResponse(
    List<string> Messages,
    bool IsCompleted,
    string? Error,
    List<ImportFileProgressDto> FileStatuses
);
