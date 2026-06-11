using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared.Dto;
using Xunit;

namespace VideoOrganizer.Tests;

// Locks in the state-machine contract that the import UI depends on:
// StartJob → MarkRunning → MarkCompleted/MarkFailed, plus the in-memory
// snapshots that feed /api/import/jobs and the failed/queue modals.
public class ImportProgressTrackerTests
{
    [Fact]
    public void StartJob_RegistersJobInQueuedState()
    {
        var tracker = Make(out _);
        var jobId = tracker.StartJob("/videos/concert", "Concert");

        var snapshots = tracker.GetAllJobSnapshots();
        var snap = Assert.Single(snapshots);
        Assert.Equal(jobId, snap.JobId);
        Assert.Equal("Concert", snap.Name);
        Assert.Equal("/videos/concert", snap.DirectoryPath);
        Assert.NotEqual(default, snap.EnqueuedAt);
        // StartedAt = null is the UI's signal to render the row as "queued".
        Assert.Null(snap.StartedAt);
        Assert.False(snap.IsCompleted);
    }

    [Fact]
    public void StartJob_FallsBackToDirectoryLeafForName()
    {
        // The UI uses Name as the row label; an empty/whitespace name has
        // to fall back to the directory's last segment so the user sees
        // something other than a blank cell.
        var tracker = Make(out _);
        var jobId = tracker.StartJob("/videos/2024-tour", name: null);

        var snap = tracker.GetAllJobSnapshots().Single();
        Assert.Equal("2024-tour", snap.Name);
    }

    [Fact]
    public void MarkRunning_SetsStartedAtAndLogsQueueDwell()
    {
        var tracker = Make(out var logger);
        var jobId = tracker.StartJob("/d", "x");

        tracker.MarkRunning(jobId);

        var snap = tracker.GetAllJobSnapshots().Single();
        Assert.NotNull(snap.StartedAt);
        // "queued for Nms" comes from the dwell calculation — assert the
        // shape rather than an exact value (timing is non-deterministic).
        Assert.Contains(logger.Collector.GetSnapshot(),
            r => r.Message.Contains("started") && r.Message.Contains("queued for"));
    }

    [Fact]
    public void MarkCompleted_AggregatesPerStatusCounts()
    {
        var tracker = Make(out var logger);
        var jobId = tracker.StartJob("/d");
        tracker.MarkRunning(jobId);

        tracker.UpdateFileProgress(jobId, "a.mp4", 100, ImportFileStatus.Completed, 100, 100);
        tracker.UpdateFileProgress(jobId, "b.mp4", 200, ImportFileStatus.Completed, 200, 200);
        tracker.UpdateFileProgress(jobId, "c.mp4", 300, ImportFileStatus.Failed, 300, 300, "boom");
        tracker.UpdateFileProgress(jobId, "d.mp4", 400, ImportFileStatus.Skipped, 400, 400);

        tracker.MarkCompleted(jobId);

        var snap = tracker.GetAllJobSnapshots().Single();
        Assert.True(snap.IsCompleted);
        Assert.Equal(2, snap.CompletedCount);
        Assert.Equal(1, snap.FailedCount);
        Assert.Equal(1, snap.SkippedCount);
        Assert.Equal(4, snap.TotalFiles);

        // Completion log carries the breakdown — the operator's main
        // post-import diagnostic.
        Assert.Contains(logger.Collector.GetSnapshot(),
            r => r.Message.Contains("completed") && r.Message.Contains("2 ok"));
    }

    [Fact]
    public void MarkFailed_LogsAtWarningWithError()
    {
        var tracker = Make(out var logger);
        var jobId = tracker.StartJob("/d");

        tracker.MarkFailed(jobId, "disk full");

        var snap = tracker.GetAllJobSnapshots().Single();
        Assert.True(snap.IsCompleted);
        Assert.Equal("disk full", snap.Error);

        var record = logger.Collector.GetSnapshot()
            .Single(r => r.Message.Contains("failed"));
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("disk full", record.Message);
    }

    [Fact]
    public void GetStatus_DequeuesMessagesOnce()
    {
        // Messages are a poll-and-drain channel so the UI doesn't replay
        // the same notification on every refresh. After GetStatus, the
        // backlog is empty.
        var tracker = Make(out _);
        var jobId = tracker.StartJob("/d");
        tracker.AddMessage(jobId, "step 1");
        tracker.AddMessage(jobId, "step 2");

        var first = tracker.GetStatus(jobId);
        Assert.Equal(new[] { "step 1", "step 2" }, first.Messages);

        var second = tracker.GetStatus(jobId);
        Assert.Empty(second.Messages);
    }

    [Fact]
    public void GetStatus_UnknownJob_ReturnsTerminalNotFound()
    {
        // Stale UI polls (job already evicted) must not blow up — returning
        // (Completed=true, Error="Job not found.") lets the UI close the
        // row gracefully.
        var tracker = Make(out _);
        var status = tracker.GetStatus(Guid.NewGuid());
        Assert.True(status.IsCompleted);
        Assert.Equal("Job not found.", status.Error);
        Assert.Empty(status.FileStatuses);
    }

    [Fact]
    public void GetFailedFiles_ReturnsOnlyFailed_AcrossJobs()
    {
        // The "Show Failed" modal aggregates across all live jobs so a user
        // can retry everything in one pass. Mixed-status jobs and
        // non-failed entries must be filtered out.
        var tracker = Make(out _);
        var j1 = tracker.StartJob("/a");
        var j2 = tracker.StartJob("/b");
        tracker.UpdateFileProgress(j1, "a1.mp4", 1, ImportFileStatus.Completed, 1, 1);
        tracker.UpdateFileProgress(j1, "a2.mp4", 1, ImportFileStatus.Failed, 1, 1, "x");
        tracker.UpdateFileProgress(j2, "b1.mp4", 1, ImportFileStatus.Failed, 1, 1, "y");
        tracker.UpdateFileProgress(j2, "b2.mp4", 1, ImportFileStatus.Skipped, 1, 1);

        var failed = tracker.GetFailedFiles();

        Assert.Equal(2, failed.Count);
        Assert.All(failed, f => Assert.NotNull(f.Error));
        Assert.Contains(failed, f => f.FileName == "a2.mp4");
        Assert.Contains(failed, f => f.FileName == "b1.mp4");
    }

    [Fact]
    public void GetQueueFiles_OnlyImportingAndPending_FromActiveJobs()
    {
        // Backs the "Show Queue" modal: completed jobs and terminal-status
        // files are excluded so the user sees only what's still to do.
        var tracker = Make(out _);
        var active = tracker.StartJob("/active");
        var done = tracker.StartJob("/done");

        tracker.UpdateFileProgress(active, "p.mp4", 1, ImportFileStatus.Pending, 0, 1);
        tracker.UpdateFileProgress(active, "i.mp4", 1, ImportFileStatus.Importing, 0, 1);
        tracker.UpdateFileProgress(active, "c.mp4", 1, ImportFileStatus.Completed, 1, 1);

        tracker.UpdateFileProgress(done, "x.mp4", 1, ImportFileStatus.Pending, 0, 1);
        tracker.MarkCompleted(done);

        var queue = tracker.GetQueueFiles();
        Assert.Equal(2, queue.Count);
        Assert.Contains(queue, q => q.FileName == "p.mp4");
        Assert.Contains(queue, q => q.FileName == "i.mp4");

        // Importing rows are sorted ahead of Pending so the in-flight row
        // shows at the top of the modal.
        Assert.Equal(ImportFileStatus.Importing, queue[0].Status);
    }

    [Fact]
    public void ClearCompleted_RemovesOnlyTerminalJobs_AndLogsCount()
    {
        var tracker = Make(out var logger);
        var alive = tracker.StartJob("/alive");
        var doneOk = tracker.StartJob("/ok");
        var doneFail = tracker.StartJob("/fail");

        tracker.MarkCompleted(doneOk);
        tracker.MarkFailed(doneFail, "x");

        var removed = tracker.ClearCompleted();

        Assert.Equal(2, removed);
        var remaining = tracker.GetAllJobSnapshots();
        Assert.Single(remaining);
        Assert.Equal(alive, remaining[0].JobId);
        Assert.Contains(logger.Collector.GetSnapshot(),
            r => r.Message.Contains("Cleared") && r.Message.Contains("2"));
    }

    [Fact]
    public void ClearCompleted_NoTerminalJobs_DoesNotLog()
    {
        // Suppress no-op logs the same way WorkerPauseStatus does — keeps
        // periodic UI refreshes from polluting the feed.
        var tracker = Make(out var logger);
        tracker.StartJob("/active");

        tracker.ClearCompleted();

        Assert.DoesNotContain(logger.Collector.GetSnapshot(),
            r => r.Message.Contains("Cleared"));
    }

    [Fact]
    public void GetAllJobSnapshots_OrderedByEnqueuedAtDescending()
    {
        // Most-recent-first is what users expect on Background Tasks.
        var tracker = Make(out _);
        var first = tracker.StartJob("/a", "first");
        Thread.Sleep(5);
        var second = tracker.StartJob("/b", "second");
        Thread.Sleep(5);
        var third = tracker.StartJob("/c", "third");

        var snaps = tracker.GetAllJobSnapshots();
        Assert.Equal(third, snaps[0].JobId);
        Assert.Equal(second, snaps[1].JobId);
        Assert.Equal(first, snaps[2].JobId);
    }

    private static ImportProgressTracker Make(out FakeLogger<ImportProgressTracker> logger)
    {
        logger = new FakeLogger<ImportProgressTracker>();
        return new ImportProgressTracker(logger);
    }
}
