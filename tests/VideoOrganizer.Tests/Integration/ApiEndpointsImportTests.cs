using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// End-to-end import: drives POST /import/directory through the real queue +
/// ffprobe pipeline and asserts a Video row is created with extracted metadata.
/// Relies on the fixture keeping the ImportQueueService hosted service running.
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsImportTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsImportTests(PostgresApiFixture api) => _api = api;

    private sealed record JobAccepted(Guid jobId);
    private sealed record ImportProgress(bool isCompleted);

    [SkippableFact]
    public async Task Import_directory_creates_a_video_row_with_extracted_metadata()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available (needed to make + probe a clip)");

        var dir = Path.Combine(Path.GetTempPath(), "sas-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var clip = Path.Combine(dir, "import-clip.mp4");
        await MakeClipAsync(clip);

        try
        {
            // The endpoint enqueues and returns 202; ImportQueueService consumes.
            var post = await _api.Client.PostAsJsonAsync("/api/import/directory",
                new { directoryPath = dir, includeSubdirectories = false, name = "e2e-import" });
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
            var jobId = (await post.Content.ReadFromJsonAsync<JobAccepted>())!.jobId;

            // Poll the import phase to completion (bounded ~30s).
            var completed = false;
            for (var i = 0; i < 60 && !completed; i++)
            {
                var prog = await _api.Client.GetFromJsonAsync<ImportProgress>($"/api/import/progress/{jobId}");
                completed = prog!.isCompleted;
                if (!completed) await Task.Delay(500);
            }
            Assert.True(completed, "import job did not complete in time");

            await _api.WithDbAsync(async db =>
            {
                var v = await db.Videos.AsNoTracking().SingleOrDefaultAsync(x => x.FileName == "import-clip.mp4");
                Assert.NotNull(v);
                Assert.True(v!.Width > 0 && v.Height > 0, "ffprobe should populate dimensions");
                Assert.True(v.Duration > TimeSpan.Zero, "ffprobe should populate duration");
                Assert.True(v.NeedsReview, "freshly-imported videos are flagged NeedsReview");
                Assert.Contains(dir.Replace('\\', '/'), v.FilePath);
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            await _api.WithDbAsync(db => db.Videos.Where(x => x.FileName == "import-clip.mp4").ExecuteDeleteAsync());
        }
    }

    // Small synthetic clip via the system ffmpeg (the same binary the fixture
    // pointed Xabe at), so the import's ffprobe can read real metadata.
    private static async Task MakeClipAsync(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestFfmpeg.FfmpegExe,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=10",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast", path,
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed making clip (exit {proc.ExitCode}): {stderr}");
    }
}
