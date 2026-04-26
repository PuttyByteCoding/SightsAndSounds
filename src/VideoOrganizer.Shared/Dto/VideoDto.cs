namespace VideoOrganizer.Shared.Dto;

// Wire shape for a Video. Tags are flattened from the VideoTag join so the
// frontend doesn't have to traverse it. PropertyValues are typed via the
// embedded PropertyDataType from the definition. WontPlay and
// MarkedForDeletion remain structural (file-system behavior); IsClip is
// derived from ParentVideoId.
public record VideoDto(
    Guid Id,
    string FileName,
    string FilePath,
    string? Md5,
    bool Md5Failed,
    string? Md5FailedError,
    bool ThumbnailsFailed,
    string? ThumbnailsFailedError,
    bool ThumbnailsGenerated,
    Guid? ImportJobId,
    long FileSize,
    TimeSpan Duration,
    int Height,
    int Width,
    VideoDimensionFormat VideoDimensionFormat,
    VideoCodec VideoCodec,
    long Bitrate,
    double FrameRate,
    string? PixelFormat,
    string? Ratio,
    DateTime? CreationTime,
    int VideoStreamCount,
    int AudioStreamCount,
    DateTime IngestDate,
    CameraTypes CameraType,
    VideoQuality VideoQuality,
    int WatchCount,
    string Notes,
    bool NeedsReview,
    bool WontPlay,
    bool MarkedForDeletion,
    Guid? ParentVideoId,
    double? ClipStartSeconds,
    double? ClipEndSeconds,
    bool IsClip,
    IReadOnlyList<ChapterMarkerDto> ChapterMarkers,
    IReadOnlyList<VideoBlockDto> VideoBlocks,
    IReadOnlyList<VideoTagSummaryDto> Tags,
    IReadOnlyList<PropertyValueDto> Properties);

// Slim per-tag projection embedded in VideoDto.
public record VideoTagSummaryDto(
    Guid Id,
    Guid TagGroupId,
    string TagGroupName,
    string Name);

// PUT /api/videos/{id} body — full update. Drops file/codec/structural
// metadata that the client shouldn't be rewriting from this endpoint
// (those are managed by the import + worker pipeline). NeedsReview and
// IsFavorite are included so the user can flip them inline; WontPlay /
// MarkedForDeletion are not — those have file-move side effects and live
// on dedicated endpoints.
public record UpdateVideoRequest(
    string FileName,
    DateTime IngestDate,
    CameraTypes CameraType,
    VideoQuality VideoQuality,
    int WatchCount,
    string Notes,
    bool NeedsReview,
    bool IsFavorite,
    double? ClipStartSeconds,
    double? ClipEndSeconds,
    IReadOnlyList<ChapterMarkerDto>? ChapterMarkers,
    IReadOnlyList<VideoBlockDto>? VideoBlocks,
    IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<PropertyValueWrite>? Properties);

// PUT /api/videos/{id}/tags body. Full-replace semantics.
public record SetVideoTagsRequest(IReadOnlyList<Guid> TagIds);
