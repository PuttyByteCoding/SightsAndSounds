namespace VideoOrganizer.Shared.Dto;

// A Video row whose FilePath no longer exists on disk. Returned by
// GET /api/validation/missing-files and rendered on the Data
// Validation page so the user can see DB rows that have lost their
// underlying file (likely candidates for purging).
public record MissingVideoFileDto(
    System.Guid VideoId,
    string FileName,
    string FilePath,
    long FileSize,
    string IngestDate,
    System.Guid? SourceId,
    string? SourceName,
    bool SourceEnabled);

// Request for POST /api/validation/missing-files/purge — the rows
// (by Video id) the user wants removed from the database. IDs come
// from a prior GET /missing-files scan.
public record PurgeMissingFilesRequest(
    System.Collections.Generic.IReadOnlyList<System.Guid> VideoIds);

// Result of POST /api/validation/missing-files/purge. DB-only
// deletion: the file is already gone — that's the premise — so
// nothing touches disk. Every row is re-probed with File.Exists
// before deletion; SkippedPresentIds lists rows whose file
// reappeared between the scan and the purge (remounted drive,
// restored backup) and were therefore NOT deleted. NotFound counts
// ids with no matching row (deleted elsewhere since the scan).
public record PurgeMissingFilesResultDto(
    int Deleted,
    int SkippedPresent,
    int NotFound,
    System.Collections.Generic.IReadOnlyList<System.Guid> SkippedPresentIds);

// A video file found on disk under a configured source that does
// NOT have a matching Video row in the DB. Returned by GET
// /api/validation/extra-files. These are unimported leftovers — the
// user can re-run the import tool on the source to pick them up.
public record ExtraDiskFileDto(
    string FilePath,
    string FileName,
    long FileSize,
    System.Guid SourceId,
    string SourceName);

// A Video row eligible for MD5 re-validation: has a stored Md5,
// the file is reachable on disk, and it's not a clip (clips share
// their parent's file). Returned by GET /api/validation/md5-candidates;
// the client iterates this list one-by-one and POSTs each id to
// /api/validation/md5-check/{id} so progress can be tracked + cancelled
// in the browser without a long-running server connection.
public record Md5CandidateDto(
    System.Guid VideoId,
    string FileName,
    string FilePath,
    long FileSize,
    System.Guid? SourceId,
    string? SourceName,
    bool SourceEnabled,
    string StoredMd5);

// Per-file result for /api/validation/md5-check/{id}. Match=false
// means the file's content has drifted from what was hashed at
// import (corruption, truncation, an unrelated edit). FileExists=false
// is reported as an error rather than a mismatch since there's
// nothing to hash; the missing-files tool covers that case
// separately.
public record Md5CheckResultDto(
    System.Guid VideoId,
    string ComputedMd5,
    string StoredMd5,
    bool Match,
    long FileSize,
    bool FileExists,
    string? Error);
