using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Data-safety and security endpoint tests against a real Postgres container:
/// purge (deletes the file + row) and its "not flagged" guard, the stream
/// path-traversal guard (403 for files outside an enabled source), and the
/// tag-group cascade delete.
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsSafetyTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsSafetyTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Purge_deletes_a_flagged_video_and_its_file()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var (dir, file) = NewTempFile("purge");
        var videoId = Guid.NewGuid();
        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video
                {
                    Id = videoId,
                    FileName = "clip.mp4",
                    FilePath = PathNormalizer.Normalize(file),
                    MarkedForDeletion = true,
                });
                await db.SaveChangesAsync();
            });

            var res = await _api.Client.PostAsync($"/api/videos/{videoId}/purge", content: null);
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

            Assert.False(File.Exists(file), "the file should be deleted from disk");
            await _api.WithDbAsync(async db =>
                Assert.False(await db.Videos.AnyAsync(v => v.Id == videoId), "the row should be removed"));
        }
        finally { TryDeleteDir(dir); }
    }

    [SkippableFact]
    public async Task Purge_rejects_a_video_that_is_not_flagged()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var (dir, file) = NewTempFile("purge-guard");
        var videoId = Guid.NewGuid();
        try
        {
            await _api.WithDbAsync(async db =>
            {
                // Neither MarkedForDeletion nor PlaybackIssue — must not be purgeable.
                db.Videos.Add(new Video { Id = videoId, FileName = "clip.mp4", FilePath = PathNormalizer.Normalize(file) });
                await db.SaveChangesAsync();
            });

            var res = await _api.Client.PostAsync($"/api/videos/{videoId}/purge", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

            Assert.True(File.Exists(file), "the file must NOT be deleted");
            await _api.WithDbAsync(async db =>
                Assert.True(await db.Videos.AnyAsync(v => v.Id == videoId), "the row must survive"));
        }
        finally
        {
            TryDeleteDir(dir);
            await _api.WithDbAsync(db => db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync());
        }
    }

    [SkippableFact]
    public async Task Stream_forbids_a_file_outside_any_enabled_source()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        // Real file, but no enabled VideoSet covers its path → path-traversal guard.
        var (dir, file) = NewTempFile("stream-outside");
        var videoId = Guid.NewGuid();
        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video { Id = videoId, FileName = "clip.mp4", FilePath = PathNormalizer.Normalize(file) });
                await db.SaveChangesAsync();
            });

            var res = await _api.Client.GetAsync($"/api/videos/{videoId}/stream");
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        finally
        {
            TryDeleteDir(dir);
            await _api.WithDbAsync(db => db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync());
        }
    }

    [SkippableFact]
    public async Task Stream_serves_a_file_under_an_enabled_source()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var (dir, file) = NewTempFile("stream-in");
        var setId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.VideoSets.Add(new VideoSet { Id = setId, Name = "stream-in", Path = PathNormalizer.Normalize(dir), Enabled = true });
                db.Videos.Add(new Video { Id = videoId, FileName = "clip.mp4", FilePath = PathNormalizer.Normalize(file) });
                await db.SaveChangesAsync();
            });

            var res = await _api.Client.GetAsync($"/api/videos/{videoId}/stream");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("video/mp4", res.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            TryDeleteDir(dir);
            await _api.WithDbAsync(async db =>
            {
                await db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync();
                await db.VideoSets.Where(s => s.Id == setId).ExecuteDeleteAsync();
            });
        }
    }

    [SkippableFact]
    public async Task Deleting_a_tag_group_cascades_to_its_tags_and_taggings()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var groupId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var videoId = Guid.NewGuid();

        await _api.WithDbAsync(async db =>
        {
            db.TagGroups.Add(new TagGroup { Id = groupId, Name = "cascade-" + token });
            db.Tags.Add(new Tag { Id = tagId, Name = "t-" + token, TagGroupId = groupId });
            db.Videos.Add(new Video { Id = videoId, FileName = "c.mp4", FilePath = $"/cascade-{token}/c.mp4" });
            db.VideoTags.Add(new VideoTag { VideoId = videoId, TagId = tagId });
            await db.SaveChangesAsync();
        });

        var res = await _api.Client.DeleteAsync($"/api/tag-groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        await _api.WithDbAsync(async db =>
        {
            Assert.False(await db.TagGroups.AnyAsync(g => g.Id == groupId), "group gone");
            Assert.False(await db.Tags.AnyAsync(t => t.Id == tagId), "tag cascade-deleted");
            Assert.False(await db.VideoTags.AnyAsync(vt => vt.TagId == tagId), "taggings cascade-deleted");
            Assert.True(await db.Videos.AnyAsync(v => v.Id == videoId), "the video itself survives");
        });

        // The video carried no other group; clean it up.
        await _api.WithDbAsync(db => db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync());
    }

    private static (string dir, string file) NewTempFile(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sas-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(file, new byte[] { 0, 0, 0, 0 });
        return (dir, file);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
