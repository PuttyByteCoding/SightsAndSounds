using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Rename a video's file in place (#172): the file moves to the new name in the
/// same folder, FileName/FilePath update, child clips sharing the file follow,
/// a move-log row is written, and a name collision is rejected. DB + filesystem;
/// no ffmpeg.
/// </summary>
[Collection("PostgresApi")]
public sealed class RenameVideoTests
{
    private readonly PostgresApiFixture _api;

    public RenameVideoTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Rename_moves_the_file_and_updates_the_row_and_its_clips()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "sas-rename-" + token);
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "old.mp4");
        await File.WriteAllBytesAsync(srcPath, new byte[] { 1, 2, 3 });
        var parentId = Guid.NewGuid();
        var clipId = Guid.NewGuid();

        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video { Id = parentId, FileName = "old.mp4", FilePath = srcPath, Duration = TimeSpan.FromSeconds(5) });
                db.Videos.Add(new Video
                {
                    Id = clipId, FileName = "a clip", FilePath = srcPath, ParentVideoId = parentId,
                    ClipStartSeconds = 1, ClipEndSeconds = 3,
                });
                await db.SaveChangesAsync();
            });

            var res = await _api.Client.PostAsJsonAsync($"/api/videos/{parentId}/rename", new { newName = "fresh name" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var newPath = Path.Combine(dir, "fresh name.mp4");
            Assert.True(File.Exists(newPath), "renamed file should exist");
            Assert.False(File.Exists(srcPath), "old file should be gone");

            await _api.WithDbAsync(async db =>
            {
                var parent = await db.Videos.AsNoTracking().FirstAsync(v => v.Id == parentId);
                Assert.Equal("fresh name.mp4", parent.FileName);
                Assert.EndsWith("fresh name.mp4", parent.FilePath);

                // Child clip's FilePath followed the rename (it shares the file).
                var clip = await db.Videos.AsNoTracking().FirstAsync(v => v.Id == clipId);
                Assert.Equal(parent.FilePath, clip.FilePath);
                Assert.Equal("a clip", clip.FileName); // clip keeps its own display name

                // A move-log row records the rename.
                Assert.True(await db.FileMoveLogs.AnyAsync(m => m.VideoId == parentId && m.ToPath == parent.FilePath));
            });

            // Renaming onto an existing name is rejected.
            await File.WriteAllBytesAsync(Path.Combine(dir, "taken.mp4"), new byte[] { 9 });
            var clash = await _api.Client.PostAsJsonAsync($"/api/videos/{parentId}/rename", new { newName = "taken" });
            Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        }
        finally
        {
            await _api.WithDbAsync(db =>
                db.Videos.Where(v => v.Id == parentId || v.ParentVideoId == parentId).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
