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
/// Endpoint tests for the riskier stateful mutations — tag merge (data-loss if
/// it mis-repoints), file move + undo (touches the filesystem and the undo log),
/// and the three-way tag filter (core browse correctness). Run against a real
/// Postgres container via <see cref="PostgresApiFixture"/>.
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsMutationTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsMutationTests(PostgresApiFixture api) => _api = api;

    private sealed record VideoRow(Guid id);

    [SkippableFact]
    public async Task Merge_repoints_video_tags_to_target_and_deletes_sources()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var groupId = Guid.NewGuid();
        var target = Guid.NewGuid();
        var source = Guid.NewGuid();
        var videoId = Guid.NewGuid();

        await _api.WithDbAsync(async db =>
        {
            db.TagGroups.Add(new TagGroup { Id = groupId, Name = "merge-" + token });
            db.Tags.Add(new Tag { Id = target, Name = "target-" + token, TagGroupId = groupId });
            db.Tags.Add(new Tag { Id = source, Name = "source-" + token, TagGroupId = groupId });
            db.Videos.Add(new Video { Id = videoId, FileName = "m.mp4", FilePath = $"/merge-{token}/m.mp4" });
            db.VideoTags.Add(new VideoTag { VideoId = videoId, TagId = source });
            await db.SaveChangesAsync();
        });

        var res = await _api.Client.PostAsJsonAsync(
            "/api/tags/merge", new { sourceIds = new[] { source }, targetId = target });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _api.WithDbAsync(async db =>
        {
            Assert.True(await db.VideoTags.AnyAsync(vt => vt.VideoId == videoId && vt.TagId == target),
                "the video should now be tagged with the merge target");
            Assert.False(await db.VideoTags.AnyAsync(vt => vt.TagId == source),
                "no taggings should remain pointing at the merged-away source");
            Assert.False(await db.Tags.AnyAsync(t => t.Id == source), "the source tag should be deleted");
            // The source's name folds into the target's aliases (search still finds it).
            var t = await db.Tags.AsNoTracking().SingleAsync(x => x.Id == target);
            Assert.Contains("source-" + token, t.Aliases);
        });
    }

    [SkippableFact]
    public async Task Filter_applies_required_and_excluded_tag_slots()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var root = $"/filter-{token}";
        var groupId = Guid.NewGuid();
        var tagA = Guid.NewGuid();
        var tagB = Guid.NewGuid();
        Guid v1 = Guid.NewGuid(), v2 = Guid.NewGuid(), v3 = Guid.NewGuid();
        var mine = new[] { v1, v2, v3 };

        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = Guid.NewGuid(), Name = "filter-" + token, Path = root, Enabled = true });
            db.TagGroups.Add(new TagGroup { Id = groupId, Name = "filter-" + token });
            db.Tags.Add(new Tag { Id = tagA, Name = "A-" + token, TagGroupId = groupId });
            db.Tags.Add(new Tag { Id = tagB, Name = "B-" + token, TagGroupId = groupId });
            db.Videos.AddRange(
                new Video { Id = v1, FileName = "1.mp4", FilePath = $"{root}/1.mp4" },   // A
                new Video { Id = v2, FileName = "2.mp4", FilePath = $"{root}/2.mp4" },   // B
                new Video { Id = v3, FileName = "3.mp4", FilePath = $"{root}/3.mp4" });  // A + B
            db.VideoTags.AddRange(
                new VideoTag { VideoId = v1, TagId = tagA },
                new VideoTag { VideoId = v2, TagId = tagB },
                new VideoTag { VideoId = v3, TagId = tagA },
                new VideoTag { VideoId = v3, TagId = tagB });
            await db.SaveChangesAsync();
        });

        // Required = tagA  -> A-carrying videos: v1, v3.
        var required = await PostFilterAsync(new
        {
            required = new[] { new { type = "tag", value = tagA.ToString() } }
        });
        Assert.Equal(new[] { v1, v3 }.OrderBy(x => x), required.Intersect(mine).OrderBy(x => x));

        // Required = tagA AND Excluded = tagB -> has A but not B: v1 only (v3 has B).
        var requiredNotExcluded = await PostFilterAsync(new
        {
            required = new[] { new { type = "tag", value = tagA.ToString() } },
            excluded = new[] { new { type = "tag", value = tagB.ToString() } }
        });
        Assert.Equal(new[] { v1 }, requiredNotExcluded.Intersect(mine).ToArray());
    }

    private async Task<HashSet<Guid>> PostFilterAsync(object body)
    {
        var res = await _api.Client.PostAsJsonAsync("/api/videos/filter", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<List<VideoRow>>();
        return rows!.Select(r => r.id).ToHashSet();
    }

    [SkippableFact]
    public async Task Move_then_revert_relocates_the_file_and_restores_it()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "sas-move-" + token);
        var srcDir = Path.Combine(root, "A");
        var destDir = Path.Combine(root, "B");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);
        var srcFile = Path.Combine(srcDir, "clip.mp4");
        var destFile = Path.Combine(destDir, "clip.mp4");
        await File.WriteAllBytesAsync(srcFile, new byte[] { 1, 2, 3, 4 });

        var setId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.VideoSets.Add(new VideoSet { Id = setId, Name = "move-" + token, Path = PathNormalizer.Normalize(root), Enabled = true });
                db.Videos.Add(new Video { Id = videoId, FileName = "clip.mp4", FilePath = PathNormalizer.Normalize(srcFile) });
                await db.SaveChangesAsync();
            });

            // Move A -> B.
            var moveRes = await _api.Client.PostAsJsonAsync(
                $"/api/videos/{videoId}/move", new { targetDirectory = destDir });
            Assert.Equal(HttpStatusCode.OK, moveRes.StatusCode);
            Assert.True(File.Exists(destFile), "file should now be in the destination folder");
            Assert.False(File.Exists(srcFile), "file should be gone from the source folder");

            Guid moveId = Guid.Empty;
            await _api.WithDbAsync(async db =>
            {
                var v = await db.Videos.AsNoTracking().SingleAsync(x => x.Id == videoId);
                Assert.Equal(PathNormalizer.Normalize(destFile), v.FilePath);
                moveId = (await db.FileMoveLogs.AsNoTracking().SingleAsync(m => m.VideoId == videoId)).Id;
            });

            // Undo.
            var revertRes = await _api.Client.PostAsync($"/api/file-moves/{moveId}/revert", content: null);
            Assert.Equal(HttpStatusCode.OK, revertRes.StatusCode);
            Assert.True(File.Exists(srcFile), "file should be back in the source folder after undo");
            Assert.False(File.Exists(destFile), "file should be gone from the destination after undo");

            await _api.WithDbAsync(async db =>
            {
                var v = await db.Videos.AsNoTracking().SingleAsync(x => x.Id == videoId);
                Assert.Equal(PathNormalizer.Normalize(srcFile), v.FilePath);
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            await _api.WithDbAsync(async db =>
            {
                await db.FileMoveLogs.Where(m => m.VideoId == videoId).ExecuteDeleteAsync();
                await db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync();
                await db.VideoSets.Where(s => s.Id == setId).ExecuteDeleteAsync();
            });
        }
    }
}
