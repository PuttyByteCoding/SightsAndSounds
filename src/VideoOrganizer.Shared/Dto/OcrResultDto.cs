namespace VideoOrganizer.Shared.Dto;

// Text read off the video frame at TimeSeconds via OCR (issue #5).
public record OcrResultDto(
    double TimeSeconds,
    string Text
);
