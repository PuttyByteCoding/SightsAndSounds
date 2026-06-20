namespace VideoOrganizer.Shared.Dto;

// One stored OCR hit from a full-video scan (issue #5): the recognized text and
// the playhead position it was read at, so the UI can seek straight to it.
public record OcrTextLineDto(
    double TimeSeconds,
    string Text
);

// Live state of the background OCR scan (issue #5), polled by the player's
// "Scan for text" panel. ScannedThroughSeconds / DurationSeconds drive the
// "% scanned (up to MM:SS)" progress; Hits is how many text rows this scan has
// found so far. Phase: "idle" | "scanning" | "stopping" | "done" | "error".
public record OcrScanProgressDto(
    bool Active,
    double ScannedThroughSeconds,
    double DurationSeconds,
    int Hits,
    string Phase,
    string? Error
);
