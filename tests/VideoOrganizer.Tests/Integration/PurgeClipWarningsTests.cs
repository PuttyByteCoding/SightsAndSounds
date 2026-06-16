using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Purge-page clip warnings (#174): a parent queued for deletion that still has
/// embedded, un-exported clips is surfaced (with a count) so the user can export
/// them before the file is deleted. Exported/deleted clips and clip-free parents
/// don't warn. DB-only — no ffmpeg.
/// </summary>
[Collection("PostgresApi")]
public sealed class PurgeClipWarningsTests
{
    private readonly PostgresApiFixture _api;

    public PurgeClipWarningsTests(PostgresApiFixture api) => _api = api;

    private sealed record Warning(Guid videoId, string fileName, int embeddedClipCount);

    [SkippableFact]
    public async Task Only_parents_with_unexported_embedded_clips_are_warned()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N")[..8];
        var withClips = Guid.NewGuid();    // marked + has 1 live clip + 1 exported clip
        var noClips = Guid.NewGuid();      // marked, no clips
        var liveClip = Guid.NewGuid();
        var exportedClip = Guid.NewGuid();
        var root = $"/pcw-{token}";

        try
        {
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video { Id = withClips, FileName = $"parent-{token}.mp4", FilePath = $"{root}/p.mp4", MarkedForDeletion = true });
                db.Videos.Add(new Video { Id = noClips, FileName = $"bare-{token}.mp4", FilePath = $"{root}/b.mp4", MarkedForDeletion = true });
                db.Videos.Add(new Video
                {
                    Id = liveClip, FileName = "live", FilePath = $"{root}/p.mp4", ParentVideoId = withClips,
                    ClipStartSeconds = 1, ClipEndSeconds = 3,
                });
                db.Videos.Add(new Video
                {
                    Id = exportedClip, FileName = "exported", FilePath = $"{root}/p.mp4", ParentVideoId = withClips,
                    ClipStartSeconds = 4, ClipEndSeconds = 6, ClipExported = true,   // already exported → no warning
                });
                await db.SaveChangesAsync();
            });

            var warnings = await _api.Client.GetFromJsonAsync<List<Warning>>("/api/videos/purge-clip-warnings");
            Assert.NotNull(warnings);

            var w = Assert.Single(warnings!, x => x.videoId == withClips);
            Assert.Equal(1, w.embeddedClipCount);                       // only the live clip counts
            Assert.DoesNotContain(warnings!, x => x.videoId == noClips); // clip-free parent: no warning
        }
        finally
        {
            await _api.WithDbAsync(db =>
                db.Videos.Where(v => v.FilePath.StartsWith(root)).ExecuteDeleteAsync());
        }
    }
}
