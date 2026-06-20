using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.API.Services;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xabe.FFmpeg;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Optimize for streaming (issue #166): a non-faststart MP4 (moov after mdat,
/// which makes large files buffer before playback) is remuxed in place with
/// faststart (moov first), losslessly. Already-faststart files are skipped.
/// Runs real ffmpeg; skips without Docker/ffmpeg.
/// </summary>
[Collection("PostgresApi")]
public sealed class StreamingOptimizeTests
{
    private readonly PostgresApiFixture _api;

    public StreamingOptimizeTests(PostgresApiFixture api) => _api = api;

    private sealed record RunProgress(bool active, int total, int done, int optimized, int skipped, string current, string phase, List<string> errors);

    [SkippableFact]
    public async Task Optimize_moves_moov_to_the_front_and_rehashes()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-faststart-" + token);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "big.mp4");
        var videoId = Guid.NewGuid();

        try
        {
            // ffmpeg's mp4 muxer writes moov AFTER mdat by default (no faststart).
            await MakeVideoAsync(path, 3);
            Assert.False(await StreamingOptimizeService.IsFaststartAsync(path, default),
                "freshly-muxed mp4 should not be faststart");

            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video
                {
                    Id = videoId, FileName = "big.mp4", FilePath = path,
                    Duration = TimeSpan.FromSeconds(3), FileSize = new FileInfo(path).Length,
                    Md5 = "0123456789abcdef0123456789abcdef",   // pretend it was hashed
                });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsJsonAsync("/api/optimize-streaming", new { videoIds = new[] { videoId } });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            RunProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<RunProgress>("/api/optimize-streaming");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active);
            Assert.Equal("done", prog.phase);
            Assert.Empty(prog.errors);
            Assert.Equal(1, prog.optimized);

            // Same path, now faststart, still a valid video.
            Assert.True(await StreamingOptimizeService.IsFaststartAsync(path, default),
                "file should be faststart after optimize");

            await _api.WithDbAsync(async db =>
            {
                var v = await db.Videos.AsNoTracking().FirstAsync(x => x.Id == videoId);
                Assert.Null(v.Md5);                  // cleared so the worker re-hashes
                Assert.True(v.FileSize > 0);
            });

            // Running again is a no-op skip (already faststart).
            await _api.Client.PostAsJsonAsync("/api/optimize-streaming", new { videoIds = new[] { videoId } });
            RunProgress? again = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                again = await _api.Client.GetFromJsonAsync<RunProgress>("/api/optimize-streaming");
                if (again is { active: false }) break;
            }
            Assert.Equal(1, again!.skipped);
            Assert.Equal(0, again.optimized);
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
