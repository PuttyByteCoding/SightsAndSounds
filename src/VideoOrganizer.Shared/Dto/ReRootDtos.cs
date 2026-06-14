namespace VideoOrganizer.Shared.Dto;

// Re-rooting a source (issue #32). Changing a VideoSet.Path leaves every
// Video.FilePath (an absolute path matched by prefix) pointing at the old
// location, so a plain path edit orphans the whole library. Re-rooting
// rewrites the source path AND the FilePath prefix on every video under it in
// one transaction — the operation that makes a source "move" (e.g. a Windows
// S:/Videos library migrating to /mnt/videos on Linux).

public sealed record ReRootRequest(string NewPath);

// One sampled file in a re-root preview: the stored path, the path it would
// become under the new base, and whether that new path exists on disk.
public sealed record ReRootPreviewItem(string OldPath, string NewPath, bool Exists);

// Dry-run spot check for a proposed re-root. Reports how many videos would be
// repointed and stats a small sample at the new location so the user can
// confirm the mapping before committing.
public sealed record ReRootPreview(
    int TotalAffected,
    int Sampled,
    int Found,
    int Missing,
    bool NewBaseExists,
    IReadOnlyList<ReRootPreviewItem> Examples);
