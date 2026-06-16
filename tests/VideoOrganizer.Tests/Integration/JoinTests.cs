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
/// Join (issue #163): concatenates videos in order into one new file, ingested
/// as a fresh video whose duration is the sum of the inputs. Stream-copy path
/// (inputs share format). Runs real ffmpeg; skips without Docker/ffmpeg.
/// </summary>
[Collection("PostgresApi")]
public sealed class JoinTests
{
    private readonly PostgresApiFixture _api;

    public JoinTests(PostgresApiFixture api) => _api = api;

    private sealed record RunProgress(bool active, int total, int done, string current, string phase, List<string> errors);

    [SkippableFact]
    public async Task Join_concatenates_two_clips_into_one_longer_file()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-join-" + token);
        Directory.CreateDirectory(dir);
        var aPath = Path.Combine(dir, "a.mp4");
        var bPath = Path.Combine(dir, "b.mp4");
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(aPath, 2);
            await MakeVideoAsync(bPath, 3);
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video { Id = aId, FileName = "a.mp4", FilePath = aPath, Duration = TimeSpan.FromSeconds(2), Width = 320, Height = 240 });
                db.Videos.Add(new Video { Id = bId, FileName = "b.mp4", FilePath = bPath, Duration = TimeSpan.FromSeconds(3), Width = 320, Height = 240 });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsJsonAsync("/api/join",
                new { videoIds = new[] { aId, bId }, reencode = false, name = "combined-" + token });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            RunProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<RunProgress>("/api/join");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active);
            Assert.Equal("done", prog.phase);
            Assert.Empty(prog.errors);

            var joinedPath = Path.Combine(dir, "combined-" + token + ".mp4");
            Assert.True(File.Exists(joinedPath), "expected the joined file on disk");

            await _api.WithDbAsync(async db =>
            {
                var joined = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.FilePath == joinedPath);
                Assert.NotNull(joined);
                // ~5s total (2 + 3); allow slack for keyframe/container rounding.
                Assert.True(joined!.Duration > TimeSpan.FromSeconds(4), $"joined duration {joined.Duration} should be ~5s");
            });
        }
        finally
        {
            await _api.WithDbAsync(db => db.Videos.Where(v => v.FilePath.StartsWith(dir)).ExecuteDeleteAsync());
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
