using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xabe.FFmpeg;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Remove blocked sections (issue #70): a video's "Hide" blocks are cut out and
/// the kept segments concatenated (stream-copy) into a new "_trimmed" file,
/// ingested as a fresh video carrying the source's tags. Runs real ffmpeg
/// extract+concat; skips when Docker/ffmpeg are unavailable.
/// </summary>
[Collection("PostgresApi")]
public sealed class BlockRemovalTests
{
    private readonly PostgresApiFixture _api;

    public BlockRemovalTests(PostgresApiFixture api) => _api = api;

    private sealed record RunProgress(bool active, int total, int done, string current, string phase, List<string> errors);

    [SkippableFact]
    public async Task Removing_a_middle_hide_block_produces_a_shorter_tagged_trimmed_file()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-trim-" + token);
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.mp4");
        var videoId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(srcPath, seconds: 9);

            await _api.WithDbAsync(async db =>
            {
                db.TagGroups.Add(new TagGroup { Id = groupId, Name = "grp-" + token });
                db.Tags.Add(new Tag { Id = tagId, Name = "tag-" + token, TagGroupId = groupId });
                db.Videos.Add(new Video
                {
                    Id = videoId, FileName = "src.mp4", FilePath = srcPath,
                    Duration = TimeSpan.FromSeconds(9), FileSize = new FileInfo(srcPath).Length,
                    // Hide the middle 3s → keep [0,3] + [6,9] (two segments, concat path).
                    VideoBlocks = new List<VideoBlock>
                    {
                        new() { OffsetInSeconds = 3, LengthInSeconds = 3, VideoBlockType = VideoBlockTypes.Hide },
                    },
                });
                db.VideoTags.Add(new VideoTag { VideoId = videoId, TagId = tagId });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsJsonAsync("/api/remove-blocks", new { videoIds = new[] { videoId } });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            var prog = await PollAsync();
            Assert.False(prog.active);
            Assert.Equal("done", prog.phase);
            Assert.Empty(prog.errors);

            var trimmedPath = Path.Combine(dir, "src_trimmed.mp4");
            Assert.True(File.Exists(trimmedPath), "expected src_trimmed.mp4 on disk");

            await _api.WithDbAsync(async db =>
            {
                var trimmed = await db.Videos.AsNoTracking()
                    .Include(v => v.VideoTags).ThenInclude(vt => vt.Tag)
                    .FirstOrDefaultAsync(v => v.FilePath == trimmedPath);
                Assert.NotNull(trimmed);
                Assert.Null(trimmed!.ParentVideoId);
                // ~6s of content kept out of 9 — clearly shorter than the source.
                Assert.True(trimmed.Duration > TimeSpan.FromSeconds(1), "trimmed should have real content");
                Assert.True(trimmed.Duration < TimeSpan.FromSeconds(8),
                    $"trimmed duration {trimmed.Duration} should be well under the 9s source");
                Assert.Contains("tag-" + token, trimmed.VideoTags.Select(t => t.Tag!.Name));
            });
        }
        finally
        {
            await _api.WithDbAsync(async db =>
            {
                await db.Videos.Where(v => v.Id == videoId || v.FilePath.StartsWith(dir)).ExecuteDeleteAsync();
                await db.Tags.Where(t => t.Id == tagId).ExecuteDeleteAsync();
                await db.TagGroups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
            });
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task A_fully_hidden_video_reports_an_error_and_produces_no_file()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-trim-full-" + token);
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "all.mp4");
        var videoId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(srcPath, seconds: 5);
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video
                {
                    Id = videoId, FileName = "all.mp4", FilePath = srcPath,
                    Duration = TimeSpan.FromSeconds(5),
                    VideoBlocks = new List<VideoBlock>
                    {
                        new() { OffsetInSeconds = 0, LengthInSeconds = 5, VideoBlockType = VideoBlockTypes.Hide },
                    },
                });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsJsonAsync("/api/remove-blocks", new { videoIds = new[] { videoId } });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            var prog = await PollAsync();
            Assert.False(prog.active);
            Assert.Equal("error", prog.phase);
            Assert.NotEmpty(prog.errors);
            Assert.False(File.Exists(Path.Combine(dir, "all_trimmed.mp4")));
        }
        finally
        {
            await _api.WithDbAsync(async db =>
                await db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<RunProgress> PollAsync()
    {
        RunProgress? prog = null;
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(500);
            prog = await _api.Client.GetFromJsonAsync<RunProgress>("/api/remove-blocks");
            if (prog is { active: false }) break;
        }
        Assert.NotNull(prog);
        return prog!;
    }

    private static async Task MakeVideoAsync(string path, int seconds)
    {
        var psi = new ProcessStartInfo { FileName = ResolveFfmpeg(), RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=15:duration={seconds}",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "15", path,
                 })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg fixture-gen failed: {err}");
    }

    private static string ResolveFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dir = FFmpeg.ExecutablesPath;
        if (!string.IsNullOrEmpty(dir))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return exe;
    }
}
