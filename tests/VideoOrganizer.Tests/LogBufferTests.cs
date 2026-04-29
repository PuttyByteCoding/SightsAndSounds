using VideoOrganizer.API.Services;
using Xunit;

namespace VideoOrganizer.Tests;

public class LogBufferTests
{
    [Fact]
    public void Add_ThenSnapshot_ReturnsAllEvents()
    {
        var buf = new LogBuffer();
        buf.Add(Make(0, "first"));
        buf.Add(Make(1, "second"));
        buf.Add(Make(2, "third"));

        var snap = buf.Snapshot();

        Assert.Equal(3, snap.Count);
        Assert.Equal("first", snap[0].Message);
        Assert.Equal("second", snap[1].Message);
        Assert.Equal("third", snap[2].Message);
    }

    [Fact]
    public void Add_EvictsEntriesOlderThanRetention()
    {
        // Retention is 1 second so we don't have to sleep — we backdate
        // events past the cutoff and then add a fresh one to trigger
        // eviction. Without an explicit add, eviction never runs.
        var buf = new LogBuffer { Retention = TimeSpan.FromSeconds(1) };
        var now = DateTimeOffset.UtcNow;

        buf.Add(new LogEvent(now.AddSeconds(-10), "Information", "cat", "ancient", null));
        buf.Add(new LogEvent(now.AddSeconds(-5), "Information", "cat", "old", null));
        buf.Add(new LogEvent(now, "Information", "cat", "fresh", null));

        var snap = buf.Snapshot();
        Assert.Single(snap);
        Assert.Equal("fresh", snap[0].Message);
    }

    [Fact]
    public void Snapshot_ReturnsIndependentCopy()
    {
        // The Logs page is fed from this method; a leaking reference would
        // let a slow client see partially-evicted state. The buffer must
        // hand out a stable snapshot.
        var buf = new LogBuffer();
        buf.Add(Make(0, "a"));
        var first = buf.Snapshot();

        buf.Add(Make(1, "b"));
        var second = buf.Snapshot();

        Assert.Single(first);            // first snapshot unaffected by later add
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task Add_IsThreadSafe()
    {
        // Sanity check: LogBuffer is hit by every ILogger callsite in the
        // process. Concurrent Add/Snapshot must not throw or lose events.
        var buf = new LogBuffer();
        const int writers = 8;
        const int perWriter = 200;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                buf.Add(Make(i, $"w{w}-{i}"));
                if (i % 10 == 0) _ = buf.Snapshot();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(writers * perWriter, buf.Snapshot().Count);
    }

    private static LogEvent Make(int seqMs, string msg) =>
        new(DateTimeOffset.UtcNow.AddMilliseconds(seqMs), "Information", "cat", msg, null);
}
