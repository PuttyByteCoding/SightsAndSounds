using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Shared.Dto;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// #128 — concurrent re-import dedup. The partial unique index
/// (FilePath, FileName WHERE ParentVideoId IS NULL) makes dedup transactional:
/// a race can never create a duplicate, and the importer degrades gracefully
/// (re-saves the batch row-by-row, skipping only the genuine duplicate) instead
/// of failing the whole batch.
/// </summary>
[Collection("PostgresApi")]
public sealed class ImportConcurrencyTests
{
    private readonly PostgresApiFixture _api;

    public ImportConcurrencyTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Two_top_level_videos_with_the_same_path_violate_the_unique_index()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var path = $"/conc-{token}/dup.mp4";

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
                await _api.WithDbAsync(async db =>
                {
                    db.Videos.Add(new Video { Id = Guid.NewGuid(), FileName = "dup.mp4", FilePath = path });
                    db.Videos.Add(new Video { Id = Guid.NewGuid(), FileName = "dup.mp4", FilePath = path });
                    await db.SaveChangesAsync(); // partial unique index rejects the second
                }));
        }
        finally
        {
            await _api.WithDbAsync(db => db.Videos.Where(v => v.FilePath == path).ExecuteDeleteAsync());
        }
    }

    [SkippableFact]
    public async Task A_clip_may_reuse_its_parents_path()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var path = $"/conc-{token}/movie.mp4";
        var parentId = Guid.NewGuid();
        var clipId = Guid.NewGuid();

        try
        {
            // The unique index is filtered to ParentVideoId IS NULL, so a clip
            // sharing the parent's exact FilePath + FileName is allowed.
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video { Id = parentId, FileName = "movie.mp4", FilePath = path });
                await db.SaveChangesAsync();
                db.Videos.Add(new Video
                {
                    Id = clipId,
                    FileName = "movie.mp4",
                    FilePath = path,
                    ParentVideoId = parentId,
                    ClipStartSeconds = 1,
                    ClipEndSeconds = 2,
                });
                await db.SaveChangesAsync(); // must NOT throw
            });

            await _api.WithDbAsync(async db =>
                Assert.Equal(2, await db.Videos.CountAsync(v => v.FilePath == path)));
        }
        finally
        {
            await _api.WithDbAsync(db => db.Videos.Where(v => v.FilePath == path).ExecuteDeleteAsync());
        }
    }

    [SkippableFact]
    public async Task Concurrent_imports_of_the_same_directory_never_duplicate_or_spuriously_fail()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available (needed to make + probe clips)");

        var token = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "sas-conc-" + token);
        Directory.CreateDirectory(dir);
        var names = new[] { $"c1-{token}.mp4", $"c2-{token}.mp4", $"c3-{token}.mp4" };
        foreach (var n in names) await MakeClipAsync(Path.Combine(dir, n));

        var statuses = new ConcurrentBag<(string File, ImportFileStatus Status, string? Msg)>();
        void Report(string file, long _, ImportFileStatus status, long __, long ___, string? msg)
            => statuses.Add((Path.GetFileName(file), status, msg));

        try
        {
            // Two importers from independent DI scopes (independent DbContexts) —
            // this is the cross-process race the in-app queue can't serialize.
            using var scope1 = _api.Factory.Services.CreateScope();
            using var scope2 = _api.Factory.Services.CreateScope();
            var svc1 = scope1.ServiceProvider.GetRequiredService<DirectoryImportService>();
            var svc2 = scope2.ServiceProvider.GetRequiredService<DirectoryImportService>();

            var t1 = svc1.ImportFromDirectoryAsync(dir, includeSubdirectories: false, fileStatusReporter: Report);
            var t2 = svc2.ImportFromDirectoryAsync(dir, includeSubdirectories: false, fileStatusReporter: Report);
            await Task.WhenAll(t1, t2); // neither import should throw

            await _api.WithDbAsync(async db =>
            {
                foreach (var n in names)
                    Assert.Equal(1, await db.Videos.CountAsync(v => v.FileName == n)); // exactly one, no dupes
            });

            // The whole point of #128: a raced file is reported Skipped (or
            // Completed by the winner), never Failed.
            var failed = statuses.Where(s => s.Status == ImportFileStatus.Failed).ToList();
            Assert.True(failed.Count == 0,
                "no file should be reported Failed due to a concurrent re-import; got: " +
                string.Join(" | ", failed.Select(f => $"{f.File}: {f.Msg}")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            await _api.WithDbAsync(db => db.Videos.Where(v => names.Contains(v.FileName)).ExecuteDeleteAsync());
        }
    }

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
