using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Endpoint + EF behaviour against a real Postgres container. Focused on the
/// previously-untested, highest-risk paths: the re-root prefix rewrite and the
/// backup→restore round-trip (both destructive, both manually-tested-only before).
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsIntegrationTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsIntegrationTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task App_boots_migrates_and_serves_a_db_endpoint()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        // Proves the host built, migrations applied against the container, and a
        // DB-backed endpoint executes real SQL without error.
        var res = await _api.Client.GetAsync("/api/videos/count");
        Assert.True(res.IsSuccessStatusCode, $"GET /api/videos/count -> {(int)res.StatusCode}");
    }

    [SkippableFact]
    public async Task ReRoot_rewrites_videoset_path_and_child_filepath_prefixes()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var setId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        var oldBase = $"/reroot-old-{token}";
        var newBase = $"/reroot-new-{token}";
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();

        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = setId, Name = "ReRoot", Path = oldBase, Enabled = true });
            db.Videos.Add(new Video { Id = v1, FileName = "a.mp4", FilePath = $"{oldBase}/a.mp4" });
            db.Videos.Add(new Video { Id = v2, FileName = "b.mp4", FilePath = $"{oldBase}/sub/b.mp4" });
            await db.SaveChangesAsync();
        });

        var res = await _api.Client.PostAsJsonAsync(
            $"/api/video-sets/{setId}/re-root", new { newPath = newBase });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The endpoint reports how many child rows it repointed.
        var body = await res.Content.ReadFromJsonAsync<ReRootResult>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.reRooted);
        Assert.Equal(newBase, body.newPath);

        // Verify the actual DB state: set path moved, and each child FilePath had
        // exactly its prefix swapped (the Substring(oldBase.Length) math).
        await _api.WithDbAsync(async db =>
        {
            var set = await db.VideoSets.AsNoTracking().SingleAsync(s => s.Id == setId);
            Assert.Equal(newBase, set.Path);

            var p1 = (await db.Videos.AsNoTracking().SingleAsync(v => v.Id == v1)).FilePath;
            var p2 = (await db.Videos.AsNoTracking().SingleAsync(v => v.Id == v2)).FilePath;
            Assert.Equal($"{newBase}/a.mp4", p1);
            Assert.Equal($"{newBase}/sub/b.mp4", p2);
        });
    }

    [SkippableFact]
    public async Task Backup_snapshot_then_restore_round_trips_data()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var setId = Guid.NewGuid();
        var setName = "Backup-" + Guid.NewGuid().ToString("N");

        // Seed a distinctive row, snapshot, delete it, then restore.
        await _api.WithDbAsync(async db =>
        {
            db.VideoSets.Add(new VideoSet { Id = setId, Name = setName, Path = $"/backup-{setId:N}", Enabled = true });
            await db.SaveChangesAsync();
        });

        var snapshotRes = await _api.Client.PostAsync("/api/backup/snapshot", content: null);
        Assert.Equal(HttpStatusCode.OK, snapshotRes.StatusCode);
        var snapshot = await snapshotRes.Content.ReadFromJsonAsync<SnapshotResult>();
        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot!.fileName));

        // Delete the row so we can prove the restore actually brings it back.
        await _api.WithDbAsync(async db =>
        {
            await db.VideoSets.Where(s => s.Id == setId).ExecuteDeleteAsync();
        });
        await _api.WithDbAsync(async db =>
            Assert.False(await db.VideoSets.AnyAsync(s => s.Id == setId), "row should be gone pre-restore"));

        var restoreRes = await _api.Client.PostAsync(
            $"/api/backup/{Uri.EscapeDataString(snapshot.fileName)}/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);

        await _api.WithDbAsync(async db =>
        {
            var restored = await db.VideoSets.AsNoTracking().SingleOrDefaultAsync(s => s.Id == setId);
            Assert.NotNull(restored);
            Assert.Equal(setName, restored!.Name);
        });
    }

    private sealed record ReRootResult(int reRooted, string newPath);
    private sealed record SnapshotResult(string fileName);
}
