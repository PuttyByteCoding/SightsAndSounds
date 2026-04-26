namespace VideoOrganizer.Shared.Dto;

public record VideoBlockDto(
    int OffsetInSeconds,
    int LengthInSeconds,
    VideoBlockTypes VideoBlockType);