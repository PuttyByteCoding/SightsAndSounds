namespace VideoOrganizer.Shared.Dto;

// Returned by GET /api/videos/{id}/ffprobe. Stdout is the canonical
// `-of json` ffprobe document (so the frontend can parse + render it
// as a structured table). Stderr typically holds container-level
// errors / warnings. ExitCode = 0 on a clean read; non-zero usually
// means ffprobe couldn't decode the file at all (corruption,
// truncated container, unsupported demuxer). FilePath echoes the
// resolved on-disk path so the frontend can show the exact file
// the diagnostics ran against.
public record FfprobeResultDto(
    string Stdout,
    string Stderr,
    int ExitCode,
    string FilePath);
