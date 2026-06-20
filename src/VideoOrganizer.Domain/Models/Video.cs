namespace VideoOrganizer.Domain.Models;

public class Video
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // File Metadata
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty; // Absolute path the API reads from (always a child of some VideoSet.Path)
    // Null while the background Md5BackfillService hasn't computed it yet.
    // Postgres treats multiple NULLs as distinct in a unique index, so the
    // uniqueness constraint still catches real-Md5 duplicates once filled in.
    public string? Md5 { get; set; }
    // Set by Md5BackfillService when hashing fails or the user manually skips.
    // Stops the worker from re-trying the same broken file every scan.
    public bool Md5Failed { get; set; }
    // Captured at the time Md5Failed flips true — exception message, "timed
    // out", or "skipped by user". Surfaced in the "Show Failed" table on
    // the Background Tasks page. Cleared when the row is retried.
    public string? Md5FailedError { get; set; }
    // Set by ThumbnailWarmingService on generation failure / skip / timeout.
    // VideoCard renders a "Thumbnail failed" overlay so the user can
    // investigate or retry.
    public bool ThumbnailsFailed { get; set; }
    // Counterpart to Md5FailedError — exception message / "timed out" /
    // "skipped by user". Cleared on retry.
    public string? ThumbnailsFailedError { get; set; }
    // Set true by ThumbnailWarmingService once the sprite + VTT are
    // successfully written to disk. Lets the per-import panel show progress
    // counts without per-row disk checks. Disk is still the source of
    // truth for the worker's IsAlreadyWarmed check.
    public bool ThumbnailsGenerated { get; set; }
    // Stamped by DirectoryImportService on every Video it creates so the
    // Background Tasks page can group thumbnail/Md5 progress by import
    // (each import panel queries Videos with this JobId). Null on rows
    // imported before this column existed or via other means.
    public Guid? ImportJobId { get; set; }
    public long FileSize { get; set; }

    // Video Metadata
    public TimeSpan Duration { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }
    public VideoDimensionFormat VideoDimensionFormat { get; set; }
    public VideoCodec VideoCodec { get; set; }
    public long Bitrate { get; set; }
    public double FrameRate { get; set; }
    public string? PixelFormat { get; set; }
    public string? Ratio { get; set; }
    public DateTime? CreationTime { get; set; }
    public int VideoStreamCount { get; set; }
    public int AudioStreamCount { get; set; }

    public DateTime IngestDate { get; set; }
    public CameraTypes CameraType { get; set; }
    public VideoQuality VideoQuality { get; set; }
    public int WatchCount { get; set; }
    public string Notes { get; set; } = string.Empty;

    // Structural status flags (NOT user-editable tags). These are system-
    // managed:
    //   NeedsReview        — auto-set true on import; cleared by the user
    //                        once they've reviewed. Re-set by Md5BackfillService
    //                        when an Md5 duplicate is detected.
    //   PlaybackIssue      — set when the user marks a file as not playing
    //                        cleanly in the browser (could be codec, encoding,
    //                        container, or genuine corruption — name avoids
    //                        claiming a cause). Triggers a move into
    //                        `<set>/_PlaybackIssue/...`. Was previously named
    //                        WontPlay; renamed to read more honestly given
    //                        many such files do play in external players.
    //   MarkedForDeletion  — set when the user marks for deletion. Triggers
    //                        a move into `<set>/_ToDelete/...`. The purge
    //                        endpoints key off this field.
    //   IsFavorite         — user-set "starred" flag. Plain boolean (no file
    //                        move side effect); rendered as ★ everywhere.
    public bool NeedsReview { get; set; } = true;
    public bool PlaybackIssue { get; set; }
    public bool MarkedForDeletion { get; set; }
    public bool IsFavorite { get; set; }

    // Clip relationship. A "clip" is a Video whose ParentVideoId is non-null;
    // it represents a named range inside the parent's underlying file. The
    // playback layer uses ClipStartSeconds/ClipEndSeconds to auto-seek to the
    // in-point on load and loop back at the out-point. On non-clip rows, all
    // three fields are null.
    public Guid? ParentVideoId { get; set; }
    public double? ClipStartSeconds { get; set; }
    public double? ClipEndSeconds { get; set; }

    // Clip / edit classification flags (#167). Surfaced in the browse "Flags"
    // sidebar (Status filter), not tags.
    //   IsClip         — the umbrella "this is a clip" flag. User-settable (so
    //                    imported standalone clips can be marked), and auto-set
    //                    on embedded clips (CreateClip) and exported clips. The
    //                    Clip filter is the union ParentVideoId|IsClip|IsExportedClip,
    //                    so embedded/exported clips count even on old rows.
    //   IsExportedClip — this file was exported from a parent's clip range (#69).
    //   IsEdited       — this file is the output of removing "Hide" blocks (#70).
    public bool IsClip { get; set; }
    public bool IsExportedClip { get; set; }
    public bool IsEdited { get; set; }

    // Clip export (issue #69). When a clip (a child row with ParentVideoId) is
    // exported to its own standalone file, the source clip row is NOT deleted —
    // it's marked exported so it (a) disappears from the library/export queue
    // and (b) still lets the parent's scrubber draw a breadcrumb band showing
    // "a clip was exported from here". ExportedToVideoId points at the new
    // standalone Video created from this clip (a soft reference — no FK, so
    // deleting the export just leaves the breadcrumb pointing nowhere).
    public bool ClipExported { get; set; }
    public Guid? ExportedToVideoId { get; set; }

    // How far (in seconds) a full-video OCR text scan has reached (issue #5).
    // Null = never scanned. The scan is resumable: "Scan more" continues from
    // here rather than re-reading frames already covered. Tracked separately
    // from OcrTextLines because frames with no text leave no row, yet the scan
    // still advanced past them.
    public double? OcrScannedThroughSeconds { get; set; }

    public List<ChapterMarker> ChapterMarkers { get; set; } = new();
    public List<VideoBlock> VideoBlocks { get; set; } = new();
    public List<OcrTextLine> OcrTextLines { get; set; } = new();

    // Tagging + custom properties.
    public List<VideoTag> VideoTags { get; set; } = new();
    public List<VideoPropertyValue> PropertyValues { get; set; } = new();
}

public enum CameraTypes
{
    Unknown,
    CellPhone,
    HiddenCamera,
    Camcorder,
    ProfessionalCamera,
    NotChecked
}

// TODO: Figure this out.  What do I want to use this for?
public enum VideoQuality
{
    SingleCamera,
    MultipleCameras,
    LowQuality,
    NotChecked
}

public enum VideoDimensionFormat
{
    UHD8k,
    UHD4K,
    HD1080p,
    HD720p,
    SD576p4x3,
    SD576p16x9,
    SD480p4x3,
    SD480p16x9,
    VerticalUHD4k,
    Vertical1080p,
    Vertical720p,
    NonStandard
}

public enum VideoCodec
{
    H264,
    H265,
    HEVC,
    NotChecked,
    Other
}
