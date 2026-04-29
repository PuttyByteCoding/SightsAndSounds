using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using VideoOrganizer.API.Services;
using Xunit;

namespace VideoOrganizer.Tests;

// Locks in the contract added in the third logging pass: every Pause/Resume
// must be auditable (one log per transition), but a no-op write — the same
// state being assigned again — must NOT log. Without that filter, a polled
// client hitting /pause repeatedly would flood Seq.
public class WorkerPauseStatusTests
{
    [Fact]
    public void TogglingFlag_LogsOnce()
    {
        var (status, logger) = Make();

        status.ImportPaused = true;

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("Import", record.Message);
        Assert.Contains("paused", record.Message);
    }

    [Fact]
    public void PauseThenResume_EmitsTwoLogsInOrder()
    {
        var (status, logger) = Make();

        status.Md5Paused = true;
        status.Md5Paused = false;

        var snap = logger.Collector.GetSnapshot();
        Assert.Equal(2, snap.Count);
        Assert.Contains("paused", snap[0].Message);
        Assert.Contains("resumed", snap[1].Message);
        Assert.All(snap, r => Assert.Contains("Md5", r.Message));
    }

    [Fact]
    public void IdempotentWrite_DoesNotLog()
    {
        // Setting Paused=true on an already-paused worker is the polled-client
        // pathology this filter is designed to defeat. Exactly one transition
        // log even after three writes.
        var (status, logger) = Make();

        status.ThumbnailsPaused = true;
        status.ThumbnailsPaused = true;
        status.ThumbnailsPaused = true;

        Assert.Single(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void EachWorkerLogsItsOwnLabel()
    {
        // The shared SetFlag helper takes a worker-name string; a refactor
        // could wire the wrong label to the wrong setter. Pin each one.
        var (status, logger) = Make();

        status.ImportPaused = true;
        status.ThumbnailsPaused = true;
        status.Md5Paused = true;

        var messages = logger.Collector.GetSnapshot().Select(r => r.Message).ToList();
        Assert.Contains(messages, m => m.Contains("Import") && !m.Contains("Md5") && !m.Contains("Thumbnails"));
        Assert.Contains(messages, m => m.Contains("Thumbnails"));
        Assert.Contains(messages, m => m.Contains("Md5"));
    }

    [Fact]
    public void Getter_ReadsCurrentState()
    {
        var (status, _) = Make();
        Assert.False(status.ImportPaused);
        status.ImportPaused = true;
        Assert.True(status.ImportPaused);
    }

    private static (WorkerPauseStatus Status, FakeLogger<WorkerPauseStatus> Logger) Make()
    {
        var logger = new FakeLogger<WorkerPauseStatus>();
        return (new WorkerPauseStatus(logger), logger);
    }
}
