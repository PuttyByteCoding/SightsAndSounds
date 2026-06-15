using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.IO;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Helpers;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

public static partial class ApiEndpoints
{
    // Resolves the set of videos matching a playlist filter, pushing the
    // translatable slots into SQL (#127) so we don't pull every video under the
    // enabled roots into memory. Folder filters (untranslatable) fall back to
    // the exact in-memory MatchesFilter pass over the SQL-narrowed set. Returns
    // (Id, WatchCount) pairs — the even-distribution playlist needs WatchCount;
    // the random playlist ignores it. Empty when no VideoSet is enabled.
    private static async Task<List<(Guid Id, int WatchCount)>> ResolveFilteredVideosAsync(
        VideoOrganizerDbContext db, PlaylistFilterRequest? filter, CancellationToken ct)
    {
        var enabledRoots = await db.VideoSets.Where(s => s.Enabled).Select(s => s.Path).ToListAsync(ct);
        if (enabledRoots.Count == 0) return new();

        var required = filter?.Required ?? new();
        var optional = filter?.Optional ?? new();
        var excluded = filter?.Excluded ?? new();

        var baseQuery = db.Videos
            .AsNoTracking()
            .Include(v => v.VideoTags) // only materialized on the in-memory fallback path
            .Where(v => enabledRoots.Any(r => v.FilePath.StartsWith(r)));

        var (narrowed, needsInMemory) =
            VideoFilterTranslator.Apply(baseQuery, required, optional, excluded, Array.Empty<Guid>());

        if (!needsInMemory)
        {
            var rows = await narrowed.Select(v => new { v.Id, v.WatchCount }).ToListAsync(ct);
            return rows.Select(r => (r.Id, r.WatchCount)).ToList();
        }

        var lookup = await LoadTagLookupAsync(db, ct);
        var loaded = await narrowed.ToListAsync(ct);
        return loaded.Where(v =>
        {
            if (required.Count > 0 && !required.All(t => MatchesFilter(t, v, lookup))) return false;
            if (optional.Count > 0 && !optional.Any(t => MatchesFilter(t, v, lookup))) return false;
            if (excluded.Count > 0 && excluded.Any(t => MatchesFilter(t, v, lookup))) return false;
            return true;
        }).Select(v => (v.Id, v.WatchCount)).ToList();
    }

    private static void MapPlaylistEndpoints(RouteGroupBuilder api)
    {
        var playlists = api.MapGroup("/playlists").WithTags("Playlists");

        playlists.MapPost("/random", async (
            PlaylistFilterRequest? filter,
            VideoOrganizerDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var resolved = await ResolveFilteredVideosAsync(db, filter, ct);
            var matched = resolved.Select(r => r.Id).ToList();

            if (matched.Count == 0)
                return Results.BadRequest("No videos found matching the filter criteria");

            var rng = Random.Shared;
            var shuffled = matched.OrderBy(_ => rng.Next()).ToList();
            var playlistId = Guid.NewGuid();
            var playlist = new PlaylistDto(playlistId, shuffled, DateTime.UtcNow);
            _playlists[playlistId] = playlist;
            logger.LogInformation("Created random playlist {PlaylistId} with {Count} videos", playlistId, shuffled.Count);
            return Results.Ok(playlist);
        }).Produces<PlaylistDto>(StatusCodes.Status200OK)
          .WithName("CreateRandomPlaylist");

        playlists.MapPost("/even", async (
            PlaylistFilterRequest? filter,
            VideoOrganizerDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var matched = await ResolveFilteredVideosAsync(db, filter, ct);

            if (matched.Count == 0)
                return Results.BadRequest("No videos found matching the filter criteria");

            var rng = Random.Shared;
            var ordered = matched
                .OrderBy(x => x.WatchCount)
                .ThenBy(_ => rng.Next())
                .Select(x => x.Id)
                .ToList();
            var playlistId = Guid.NewGuid();
            var playlist = new PlaylistDto(playlistId, ordered, DateTime.UtcNow);
            _playlists[playlistId] = playlist;
            logger.LogInformation("Created even-distribution playlist {PlaylistId} with {Count} videos",
                playlistId, ordered.Count);
            return Results.Ok(playlist);
        }).Produces<PlaylistDto>(StatusCodes.Status200OK)
          .WithName("CreateEvenDistributionPlaylist");

        playlists.MapGet("/{id:guid}", (Guid id, ILogger<Program> logger) =>
        {
            if (!_playlists.TryGetValue(id, out var playlist))
            {
                logger.LogWarning("Playlist {PlaylistId} not found", id);
                return Results.NotFound();
            }
            return Results.Ok(playlist);
        }).Produces<PlaylistDto>(StatusCodes.Status200OK)
          .WithName("GetPlaylist");

        playlists.MapGet("/{playlistId:guid}/navigation/{videoId:guid}",
            (Guid playlistId, Guid videoId, ILogger<Program> logger) =>
        {
            if (!_playlists.TryGetValue(playlistId, out var playlist))
                return Results.NotFound("Playlist not found");

            var currentIndex = playlist.VideoIds.IndexOf(videoId);
            if (currentIndex == -1) return Results.NotFound("Video not found in playlist");

            var previousVideoId = currentIndex > 0 ? playlist.VideoIds[currentIndex - 1] : (Guid?)null;
            var nextVideoId = currentIndex < playlist.VideoIds.Count - 1
                ? playlist.VideoIds[currentIndex + 1] : (Guid?)null;
            return Results.Ok(new PlaylistNavigationDto(
                videoId, nextVideoId, previousVideoId, currentIndex, playlist.VideoIds.Count));
        }).Produces<PlaylistNavigationDto>(StatusCodes.Status200OK)
          .WithName("GetPlaylistNavigation");
    }
}
