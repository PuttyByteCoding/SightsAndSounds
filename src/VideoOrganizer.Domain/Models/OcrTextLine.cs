namespace VideoOrganizer.Domain.Models;

// One line of on-screen text recognized from a video frame during a full-video
// OCR scan (issue #5). The scanner samples frames on an interval, OCRs each,
// and stores a row per frame that produced non-empty text. TimeSeconds is the
// playhead position the text was read at, so search results can jump straight
// to it. Frames with no recognized text produce no row — the scan's reach is
// tracked separately on Video.OcrScannedThroughSeconds so a resume knows where
// to continue even across stretches that found nothing.
public class OcrTextLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VideoId { get; set; }
    public Video? Video { get; set; }

    // Playhead position (seconds) the frame was sampled at.
    public double TimeSeconds { get; set; }

    // The recognized text for that frame (already trimmed; never empty — the
    // scanner skips blank reads).
    public string Text { get; set; } = string.Empty;
}
