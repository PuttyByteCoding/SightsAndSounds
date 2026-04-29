using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API.Services;

public sealed class ImportProgressTracker
{
    private readonly ILogger<ImportProgressTracker> _logger;

    public ImportProgressTracker(ILogger<ImportProgressTracker> logger)
    {
        _logger = logger;
    }

    private sealed record ImportFileProgressEntry(
        long FileSizeBytes,
        ImportFileStatus Status,
        long Md5BytesProcessed,
        long Md5TotalBytes,
        string? Error);

    private sealed class ImportProgressState
    {
        public string Name { get; init; } = string.Empty;
        public string DirectoryPath { get; init; } = string.Empty;
        // EnqueuedAt: when StartJob() was called (job posted to the queue).
        // StartedAt: when ImportQueueService actually pulled the job — null
        // while the job is queued waiting. UI uses null to show "queued".
        public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentDictionary<string, ImportFileProgressEntry> FileStatuses { get; } = new();
        public bool IsCompleted { get; set; }
        public string? Error { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, ImportProgressState> _jobs = new();

    public Guid StartJob(string directoryPath, string? name = null)
    {
        var jobId = Guid.NewGuid();
        // Fall back to the directory leaf name when the client doesn't give
        // us a label — better than rendering an empty string in the UI.
        var displayName = !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : Path.GetFileName(directoryPath.TrimEnd('/', '\\'));
        if (string.IsNullOrWhiteSpace(displayName)) displayName = directoryPath;
        _jobs[jobId] = new ImportProgressState
        {
            Name = displayName,
            DirectoryPath = directoryPath,
            EnqueuedAt = DateTime.UtcNow,
            StartedAt = null,
        };
        _logger.LogInformation(
            "Import job {JobId} enqueued ({Name}) for {DirectoryPath}",
            jobId, displayName, directoryPath);
        return jobId;
    }

    // Called when the ImportQueueService pulls this job off the queue and
    // begins the Add-to-database phase. Until this fires, the job is
    // surfaced as "queued" in the UI.
    public void MarkRunning(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.StartedAt = DateTime.UtcNow;
            var queuedMs = (state.StartedAt!.Value - state.EnqueuedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Import job {JobId} started ({Name}) — queued for {QueuedMs}ms",
                jobId, state.Name, (long)queuedMs);
        }
    }

    public void AddMessage(Guid jobId, string message)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.Messages.Enqueue(message);
        }
    }

    public void UpdateFileProgress(
        Guid jobId,
        string filePath,
        long fileSizeBytes,
        ImportFileStatus status,
        long md5BytesProcessed,
        long md5TotalBytes,
        string? error = null)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.FileStatuses[filePath] = new ImportFileProgressEntry(
                fileSizeBytes,
                status,
                md5BytesProcessed,
                md5TotalBytes,
                error);
        }
    }

    public void MarkCompleted(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.IsCompleted = true;
            state.CompletedAt = DateTime.UtcNow;
            var counts = CountByStatus(state.FileStatuses);
            var elapsedMs = state.StartedAt is { } started
                ? (long)(state.CompletedAt!.Value - started).TotalMilliseconds
                : 0L;
            _logger.LogInformation(
                "Import job {JobId} completed ({Name}) — {Completed} ok, {Failed} failed, {Skipped} skipped of {Total} files in {ElapsedMs}ms",
                jobId, state.Name, counts.Completed, counts.Failed, counts.Skipped,
                state.FileStatuses.Count, elapsedMs);
        }
    }

    public void MarkFailed(Guid jobId, string error)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.IsCompleted = true;
            state.CompletedAt = DateTime.UtcNow;
            state.Error = error;
            _logger.LogWarning(
                "Import job {JobId} failed ({Name}): {Error}",
                jobId, state.Name, error);
        }
    }

    public (List<string> Messages, bool IsCompleted, string? Error, List<ImportFileProgressDto> FileStatuses) GetStatus(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return (new List<string> { "Job not found." }, true, "Job not found.", new List<ImportFileProgressDto>());
        }

        var messages = new List<string>();
        while (state.Messages.TryDequeue(out var msg))
        {
            messages.Add(msg);
        }

        var fileStatuses = state.FileStatuses
            .Select(kvp => new ImportFileProgressDto(
                kvp.Key,
                kvp.Value.FileSizeBytes,
                kvp.Value.Md5BytesProcessed,
                kvp.Value.Md5TotalBytes,
                kvp.Value.Status,
                kvp.Value.Error))
            .OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (messages, state.IsCompleted, state.Error, fileStatuses);
    }

    // Aggregates failed files across all jobs the tracker still has in
    // memory. Surfaced via /api/import/failed-files for the Show Failed
    // modal on Background Tasks.
    public List<ImportFailedFileDto> GetFailedFiles()
    {
        var rows = new List<ImportFailedFileDto>();
        foreach (var kvp in _jobs)
        {
            var jobId = kvp.Key;
            var dir = kvp.Value.DirectoryPath;
            foreach (var f in kvp.Value.FileStatuses)
            {
                if (f.Value.Status != ImportFileStatus.Failed) continue;
                rows.Add(new ImportFailedFileDto(
                    jobId,
                    dir,
                    f.Key,
                    Path.GetFileName(f.Key),
                    f.Value.FileSizeBytes,
                    f.Value.Error));
            }
        }
        // Group failed-most-recently rows together at the top so the modal
        // shows the latest run first. We don't store per-file timestamps
        // yet; sorting by job order is the closest proxy.
        return rows
            .OrderByDescending(r => r.JobId)
            .ThenBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // All in-flight files across active jobs. "In-flight" = not yet
    // Completed/Skipped/Failed — Importing or Pending. Drives the Show
    // Queue modal for imports.
    public List<ImportQueueFileDto> GetQueueFiles()
    {
        var rows = new List<ImportQueueFileDto>();
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.IsCompleted) continue;
            var jobId = kvp.Key;
            var dir = kvp.Value.DirectoryPath;
            foreach (var f in kvp.Value.FileStatuses)
            {
                if (f.Value.Status != ImportFileStatus.Importing
                    && f.Value.Status != ImportFileStatus.Pending) continue;
                rows.Add(new ImportQueueFileDto(
                    jobId,
                    dir,
                    f.Key,
                    Path.GetFileName(f.Key),
                    f.Value.FileSizeBytes,
                    f.Value.Status));
            }
        }
        return rows
            .OrderBy(r => r.Status == ImportFileStatus.Importing ? 0 : 1)
            .ThenBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Read-only snapshot of in-memory state for one job. The endpoint
    // augments this with DB-backed per-job thumbnail/Md5 counts before
    // returning to the client (the tracker is intentionally pure
    // in-memory; querying Videos belongs in the endpoint).
    public sealed record JobSnapshot(
        Guid JobId,
        string Name,
        string DirectoryPath,
        DateTime EnqueuedAt,
        DateTime? StartedAt,
        DateTime? CompletedAt,
        bool IsCompleted,
        string? Error,
        int TotalFiles,
        int CompletedCount,
        int FailedCount,
        int SkippedCount,
        int ImportingCount,
        string? CurrentFilePath);

    public List<JobSnapshot> GetAllJobSnapshots()
    {
        return _jobs
            .Select(kvp =>
            {
                var s = kvp.Value;
                var counts = CountByStatus(s.FileStatuses);
                var importingFile = s.FileStatuses
                    .Where(f => f.Value.Status == ImportFileStatus.Importing)
                    .Select(f => f.Key)
                    .FirstOrDefault();
                return new JobSnapshot(
                    kvp.Key,
                    s.Name,
                    s.DirectoryPath,
                    s.EnqueuedAt,
                    s.StartedAt,
                    s.CompletedAt,
                    s.IsCompleted,
                    s.Error,
                    s.FileStatuses.Count,
                    counts.Completed,
                    counts.Failed,
                    counts.Skipped,
                    counts.Importing,
                    importingFile);
            })
            // Sort newest-submitted first — most recently enqueued at the
            // top regardless of state. Users want to see the latest job
            // they kicked off without scrolling, even if it's queued
            // behind older still-running jobs.
            .OrderByDescending(j => j.EnqueuedAt)
            .ToList();
    }

    // Removes terminal jobs (completed or failed) from the in-memory map.
    // Active jobs are left alone.
    public int ClearCompleted()
    {
        var removed = 0;
        foreach (var kvp in _jobs.ToArray())
        {
            if (kvp.Value.IsCompleted && _jobs.TryRemove(kvp.Key, out _))
                removed++;
        }
        if (removed > 0)
        {
            _logger.LogInformation("Cleared {Count} completed import jobs from history", removed);
        }
        return removed;
    }

    private static (int Completed, int Failed, int Skipped, int Importing) CountByStatus(
        ConcurrentDictionary<string, ImportFileProgressEntry> files)
    {
        int c = 0, f = 0, s = 0, i = 0;
        foreach (var kvp in files)
        {
            switch (kvp.Value.Status)
            {
                case ImportFileStatus.Completed: c++; break;
                case ImportFileStatus.Failed: f++; break;
                case ImportFileStatus.Skipped: s++; break;
                case ImportFileStatus.Importing: i++; break;
            }
        }
        return (c, f, s, i);
    }
}
