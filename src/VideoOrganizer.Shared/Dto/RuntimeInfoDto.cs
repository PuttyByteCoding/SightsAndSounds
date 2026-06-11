namespace VideoOrganizer.Shared.Dto;

// Returned by GET /api/runtime-info. Lets the frontend tell whether
// the browser is on the same machine as the API (so it can hide the
// "must be on host" banner and surface the local-only diagnostic
// buttons), and what OS the server runs on (so the file-manager
// affordance can use the right wording — "Show in Explorer" vs
// "Reveal in Finder" vs generic "Open Folder").
public record RuntimeInfoDto(
    bool IsLocal,
    string Os);
