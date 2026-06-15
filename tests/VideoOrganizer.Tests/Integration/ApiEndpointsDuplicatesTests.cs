using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Tests.Fixtures;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Duplicate-candidate review workflow against real Postgres: confirm / reject
/// transition a pending pair's status.
/// </summary>
[Collection("PostgresApi")]
public sealed class ApiEndpointsDuplicatesTests
{
    private readonly PostgresApiFixture _api;

    public ApiEndpointsDuplicatesTests(PostgresApiFixture api) => _api = api;

    [SkippableFact]
    public async Task Confirm_moves_a_pending_candidate_to_confirmed()
        => await AssertTransition("confirm", DuplicateStatus.Confirmed);

    [SkippableFact]
    public async Task Reject_moves_a_pending_candidate_to_rejected()
        => await AssertTransition("reject", DuplicateStatus.Rejected);

    private async Task AssertTransition(string action, DuplicateStatus expected)
    {
        Skip.IfNot(_api.Available, _api.SkipReason);

        var token = Guid.NewGuid().ToString("N");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await _api.WithDbAsync(async db =>
        {
            db.Videos.Add(new Video { Id = a, FileName = "a.mp4", FilePath = $"/dup-{token}/a.mp4" });
            db.Videos.Add(new Video { Id = b, FileName = "b.mp4", FilePath = $"/dup-{token}/b.mp4" });
            db.DuplicateCandidates.Add(new DuplicateCandidate
            {
                Id = candidateId,
                VideoAId = a,
                VideoBId = b,
                Status = DuplicateStatus.Pending,
            });
            await db.SaveChangesAsync();
        });

        try
        {
            var res = await _api.Client.PostAsync($"/api/duplicates/{candidateId}/{action}", content: null);
            Assert.True(res.IsSuccessStatusCode, $"POST /duplicates/{{id}}/{action} -> {(int)res.StatusCode}");

            await _api.WithDbAsync(async db =>
            {
                var c = await db.DuplicateCandidates.AsNoTracking().SingleAsync(x => x.Id == candidateId);
                Assert.Equal(expected, c.Status);
            });
        }
        finally
        {
            await _api.WithDbAsync(async db =>
            {
                await db.DuplicateCandidates.Where(x => x.Id == candidateId).ExecuteDeleteAsync();
                await db.Videos.Where(v => v.Id == a || v.Id == b).ExecuteDeleteAsync();
            });
        }
    }
}
