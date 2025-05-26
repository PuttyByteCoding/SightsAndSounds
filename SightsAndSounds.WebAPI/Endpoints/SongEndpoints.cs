using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;

public static class SongEndpoints
{
    public static IEndpointRouteBuilder MapSongEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/songs", async (SightsAndSoundsDbContext db) =>
            await db.Songs.ToListAsync())
            .WithName("GetAllSongs")
            .WithTags("Songs")
            .WithSummary("Get all songs")
            .WithDescription("Retrieves a list of all songs in the database.");

        routes.MapGet("/songs/{id}", async (int id, SightsAndSoundsDbContext db) =>
            await db.Songs.FindAsync(id) is Song song ? Results.Ok(song) : Results.NotFound())
            .WithName("GetSongById")
            .WithTags("Songs")
            .WithSummary("Get a song by ID")
            .WithDescription("Retrieves a single song by its unique identifier.");

        routes.MapPost("/songs", async (Song song, SightsAndSoundsDbContext db) =>
        {
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            return Results.Created($"/songs/{song.Id}", song);
        })
            .WithName("CreateSong")
            .WithTags("Songs")
            .WithSummary("Create a new song")
            .WithDescription("Adds a new song to the database.");

        routes.MapPut("/songs/{id}", async (int id, Song input, SightsAndSoundsDbContext db) =>
        {
            var song = await db.Songs.FindAsync(id);
            if (song is null) return Results.NotFound();
            song.Name = input.Name;
            // update other properties as needed
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("UpdateSong")
            .WithTags("Songs")
            .WithSummary("Update a song");

        routes.MapDelete("/songs/{id}", async (int id, SightsAndSoundsDbContext db) =>
        {
            var song = await db.Songs.FindAsync(id);
            if (song is null) return Results.NotFound();
            db.Songs.Remove(song);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("DeleteSong")
            .WithTags("Songs")
            .WithSummary("Delete a song")
            .WithDescription("Removes a song from the database by ID.");

        return routes;
    }
}