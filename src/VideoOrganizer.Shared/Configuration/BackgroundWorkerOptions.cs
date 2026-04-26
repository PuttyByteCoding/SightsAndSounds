namespace VideoOrganizer.Shared.Configuration;

// Tunables for the background workers (Thumbnail warming + MD5 backfill).
// Defaults match the original hard-coded constants so behavior is unchanged
// when the BackgroundWorkers section is missing from appsettings.json.
public sealed class BackgroundWorkerOptions
{
    public ThumbnailWarmingOptions ThumbnailWarming { get; set; } = new();
    public Md5BackfillOptions Md5Backfill { get; set; } = new();
}

public sealed class ThumbnailWarmingOptions
{
    // How long a single sprite-generation can run before the worker auto-
    // skips it, marks the row ThumbnailsFailed, and moves on. Tune up if
    // you have very large videos on slow disks; tune down if a hung ffmpeg
    // is a real concern.
    public int PerVideoTimeoutSeconds { get; set; } = 300; // 5 minutes

    // Cooldown between successful sprite jobs so the worker doesn't
    // monopolize CPU/disk while the user is browsing.
    public int PerVideoCooldownSeconds { get; set; } = 2;

    // Grace window between a signal (finished import / "Scan now" /
    // "Retry failed") and the next batch — lets bursty events collapse into
    // a single scan pass. The worker no longer auto-rescans on a timer, so
    // this is the only time the user sees a wait between trigger and work.
    public int ImportGraceSeconds { get; set; } = 5;
}

public sealed class Md5BackfillOptions
{
    // Same idea as ThumbnailWarming.PerVideoTimeoutSeconds — auto-skip a
    // single hash that runs longer than this. Default is generous because
    // very large files on slow storage can legitimately take a while.
    public int PerFileTimeoutSeconds { get; set; } = 1800; // 30 minutes

    public int PerFileCooldownMilliseconds { get; set; } = 500;

    public int ImportGraceSeconds { get; set; } = 5;
}
