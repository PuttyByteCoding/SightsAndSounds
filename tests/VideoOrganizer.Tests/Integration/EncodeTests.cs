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
/// Encode/convert to the configured profile (issue #164). With the default
/// ffmpeg backend, a source is re-encoded to "&lt;stem&gt;_encoded.mp4" (H.264) and
/// ingested as a fresh video carrying the source's tags. Runs real ffmpeg; skips
/// without Docker/ffmpeg.
/// </summary>
[Collection("PostgresApi")]
public sealed class EncodeTests
{
    private readonly PostgresApiFixture _api;

    public EncodeTests(PostgresApiFixture api) => _api = api;

    private sealed record RunProgress(bool active, int total, int done, string current, string phase, List<string> errors);

    [SkippableFact]
    public async Task Encode_produces_a_new_file_with_the_configured_ffmpeg_profile()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-encode-" + token);
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.mkv");
        var videoId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(srcPath, 3);
            await _api.WithDbAsync(async db =>
            {
                db.TagGroups.Add(new TagGroup { Id = groupId, Name = "grp-" + token });
                db.Tags.Add(new Tag { Id = tagId, Name = "tag-" + token, TagGroupId = groupId });
                db.Videos.Add(new Video { Id = videoId, FileName = "src.mkv", FilePath = srcPath, Duration = TimeSpan.FromSeconds(3) });
                db.VideoTags.Add(new VideoTag { VideoId = videoId, TagId = tagId });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsJsonAsync("/api/encode", new { videoIds = new[] { videoId } });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            RunProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<RunProgress>("/api/encode");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active);
            Assert.Equal("done", prog.phase);
            Assert.Empty(prog.errors);

            var outPath = Path.Combine(dir, "src_encoded.mp4");
            Assert.True(File.Exists(outPath), "expected src_encoded.mp4 on disk");

            await _api.WithDbAsync(async db =>
            {
                var encoded = await db.Videos.AsNoTracking()
                    .Include(v => v.VideoTags).ThenInclude(vt => vt.Tag)
                    .FirstOrDefaultAsync(v => v.FilePath == outPath);
                Assert.NotNull(encoded);
                Assert.True(encoded!.Duration > TimeSpan.Zero);
                Assert.Equal(VideoOrganizer.Domain.Models.VideoCodec.H264, encoded.VideoCodec);
                Assert.Contains("tag-" + token, encoded.VideoTags.Select(t => t.Tag!.Name));
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
