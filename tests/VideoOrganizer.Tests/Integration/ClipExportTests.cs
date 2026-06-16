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
/// Clip export (issue #69): a clip (child row over a parent file) is
/// stream-copied into its own standalone file, ingested as a fresh top-level
/// video (tagged "Clip" + the clip's inherited tags), and the source clip is
/// marked exported — gone from the queue/library but still a breadcrumb on the
/// parent. Runs the real ffmpeg extraction against a synthetic clip; skips when
/// Docker/ffmpeg are unavailable.
/// </summary>
[Collection("PostgresApi")]
public sealed class ClipExportTests
{
    private readonly PostgresApiFixture _api;

    public ClipExportTests(PostgresApiFixture api) => _api = api;

    private sealed record ExportProgress(bool active, int total, int done, string current, string phase, List<string> errors);
    private sealed record ClipRow(Guid id, string fileName, double clipStartSeconds, double clipEndSeconds, bool exported);
    private sealed record QueueItem(Guid parentId, string parentFileName, double parentDurationSeconds, List<ClipRow> clips);
    private sealed record KeyframeCut(double requestedStartSeconds, double snappedStartSeconds, double endSeconds);

    [SkippableFact]
    public async Task Export_creates_a_standalone_file_tagged_Clip_and_marks_the_source_exported()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-clipexport-" + token);
        Directory.CreateDirectory(dir);
        var parentPath = Path.Combine(dir, "source.mp4");
        var parentId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(parentPath, seconds: 6);

            await _api.WithDbAsync(async db =>
            {
                db.TagGroups.Add(new TagGroup { Id = groupId, Name = "grp-" + token });
                db.Tags.Add(new Tag { Id = tagId, Name = "tag-" + token, TagGroupId = groupId });
                db.Videos.Add(new Video
                {
                    Id = parentId, FileName = "source.mp4", FilePath = parentPath,
                    Duration = TimeSpan.FromSeconds(6), FileSize = new FileInfo(parentPath).Length,
                });
                // A clip 1.0–4.0s, sharing the parent file, carrying the tag (as
                // CreateClip would have inherited it).
                db.Videos.Add(new Video
                {
                    Id = clipId, FileName = "the clip", FilePath = parentPath,
                    ParentVideoId = parentId, ClipStartSeconds = 1.0, ClipEndSeconds = 4.0,
                    Duration = TimeSpan.FromSeconds(3),
                });
                db.VideoTags.Add(new VideoTag { VideoId = parentId, TagId = tagId });
                db.VideoTags.Add(new VideoTag { VideoId = clipId, TagId = tagId });
                await db.SaveChangesAsync();
            });

            // Export it.
            var start = await _api.Client.PostAsJsonAsync("/api/clips-export",
                new { clipIds = new[] { clipId } });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            ExportProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<ExportProgress>("/api/clips-export");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active);
            Assert.Equal("done", prog.phase);
            Assert.Empty(prog.errors);
            Assert.Equal(1, prog.done);

            // A standalone file landed next to the parent.
            var exportedPath = Path.Combine(dir, "source_clip.mp4");
            Assert.True(File.Exists(exportedPath), "expected source_clip.mp4 on disk");

            // …and a fresh top-level Video row for it, tagged "Clip" + inherited tag.
            await _api.WithDbAsync(async db =>
            {
                var exported = await db.Videos.AsNoTracking()
                    .Include(v => v.VideoTags).ThenInclude(vt => vt.Tag)
                    .FirstOrDefaultAsync(v => v.FilePath == exportedPath);
                Assert.NotNull(exported);
                Assert.Null(exported!.ParentVideoId);               // top-level, not a clip
                Assert.True(exported.Duration > TimeSpan.Zero);     // ffprobe ran
                var names = exported.VideoTags.Select(t => t.Tag!.Name).ToList();
                Assert.Contains("Clip", names);
                Assert.Contains("tag-" + token, names);

                // Source clip is marked exported and points at the new video.
                var src = await db.Videos.AsNoTracking().FirstAsync(v => v.Id == clipId);
                Assert.True(src.ClipExported);
                Assert.Equal(exported.Id, src.ExportedToVideoId);
            });

            // The exported clip is gone from the export queue…
            var queue = await _api.Client.GetFromJsonAsync<List<QueueItem>>("/api/clips-export/queue");
            Assert.DoesNotContain(queue!, q => q.clips.Any(c => c.id == clipId));

            // …but still a breadcrumb on the parent's clip list, flagged exported.
            var bands = await _api.Client.GetFromJsonAsync<List<ClipRow>>($"/api/videos/{parentId}/clips");
            var band = Assert.Single(bands!, c => c.id == clipId);
            Assert.True(band.exported);
        }
        finally
        {
            await _api.WithDbAsync(async db =>
            {
                await db.Videos.Where(v => v.Id == parentId || v.ParentVideoId == parentId
                    || v.FilePath.StartsWith(dir)).ExecuteDeleteAsync();
                await db.Tags.Where(t => t.Id == tagId).ExecuteDeleteAsync();
                await db.TagGroups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
            });
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task Keyframe_cut_snaps_the_start_to_at_or_before_the_requested_in_point()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-clipkf-" + token);
        Directory.CreateDirectory(dir);
        var parentPath = Path.Combine(dir, "kf.mp4");
        var parentId = Guid.NewGuid();
        var clipId = Guid.NewGuid();

        try
        {
            await MakeVideoAsync(parentPath, seconds: 8);
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video { Id = parentId, FileName = "kf.mp4", FilePath = parentPath, Duration = TimeSpan.FromSeconds(8) });
                db.Videos.Add(new Video
                {
                    Id = clipId, FileName = "c", FilePath = parentPath, ParentVideoId = parentId,
                    ClipStartSeconds = 5.0, ClipEndSeconds = 7.0, Duration = TimeSpan.FromSeconds(2),
                });
                await db.SaveChangesAsync();
            });

            var cut = await _api.Client.GetFromJsonAsync<KeyframeCut>($"/api/videos/{clipId}/keyframe-cut");
            Assert.NotNull(cut);
            Assert.Equal(5.0, cut!.requestedStartSeconds, 3);
            Assert.Equal(7.0, cut.endSeconds, 3);
            // The real cut starts at/before the requested in-point (keyframe lead-in).
            Assert.True(cut.snappedStartSeconds <= 5.0 + 0.001,
                $"snapped start {cut.snappedStartSeconds} should be <= requested 5.0");
            Assert.True(cut.snappedStartSeconds >= 0);
        }
        finally
        {
            await _api.WithDbAsync(async db =>
                await db.Videos.Where(v => v.Id == parentId || v.ParentVideoId == parentId).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // A short clip with a 2s keyframe interval so cut-point snapping is meaningful.
    private static async Task MakeVideoAsync(string path, int seconds)
    {
        var psi = new ProcessStartInfo { FileName = ResolveFfmpeg(), RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=15:duration={seconds}",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "30", path,
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
