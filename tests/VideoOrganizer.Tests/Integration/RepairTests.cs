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
/// Repair (issue #165): re-encodes a video to a browser-friendly H.264 MP4
/// "&lt;stem&gt;_repaired.mp4" and ingests it as a fresh video carrying the source's
/// tags; the original is left untouched. Runs real ffmpeg; skips without
/// Docker/ffmpeg.
/// </summary>
[Collection("PostgresApi")]
public sealed class RepairTests
{
    private readonly PostgresApiFixture _api;

    public RepairTests(PostgresApiFixture api) => _api = api;

    private sealed record RunProgress(bool active, int total, int done, string current, string phase, List<string> errors);

    [SkippableFact]
    public async Task Repair_re_encodes_to_a_new_h264_file_with_the_source_tags()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-repair-" + token);
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "broken.mkv");   // non-mp4 source → repaired output is .mp4
        var videoId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(srcPath, seconds: 3);
            await _api.WithDbAsync(async db =>
            {
                db.TagGroups.Add(new TagGroup { Id = groupId, Name = "grp-" + token });
                db.Tags.Add(new Tag { Id = tagId, Name = "tag-" + token, TagGroupId = groupId });
                db.Videos.Add(new Video
                {
                    Id = videoId, FileName = "broken.mkv", FilePath = srcPath,
                    Duration = TimeSpan.FromSeconds(3), PlaybackIssue = true,
                });
                db.VideoTags.Add(new VideoTag { VideoId = videoId, TagId = tagId });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsJsonAsync("/api/repair", new { videoIds = new[] { videoId } });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            RunProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<RunProgress>("/api/repair");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active);
            Assert.Equal("done", prog.phase);
            Assert.Empty(prog.errors);

            var repairedPath = Path.Combine(dir, "broken_repaired.mp4");
            Assert.True(File.Exists(repairedPath), "expected broken_repaired.mp4 on disk");

            await _api.WithDbAsync(async db =>
            {
                var repaired = await db.Videos.AsNoTracking()
                    .Include(v => v.VideoTags).ThenInclude(vt => vt.Tag)
                    .FirstOrDefaultAsync(v => v.FilePath == repairedPath);
                Assert.NotNull(repaired);
                Assert.Null(repaired!.ParentVideoId);
                Assert.True(repaired.Duration > TimeSpan.Zero);
                Assert.Equal(VideoOrganizer.Domain.Models.VideoCodec.H264, repaired.VideoCodec);  // re-encoded to H.264
                Assert.Contains("tag-" + token, repaired.VideoTags.Select(t => t.Tag!.Name));

                // The original is untouched (still flagged) — user decides to delete it.
                var original = await db.Videos.AsNoTracking().FirstAsync(v => v.Id == videoId);
                Assert.True(original.PlaybackIssue);
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

    private static async Task MakeVideoAsync(string path, int seconds)
    {
        var psi = new ProcessStartInfo { FileName = ResolveFfmpeg(), RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=15:duration={seconds}",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", path,
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
