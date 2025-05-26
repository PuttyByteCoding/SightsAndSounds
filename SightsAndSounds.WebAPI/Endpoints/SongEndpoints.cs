using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;

public static class SongEndpoints
{
    public static IEndpointRouteBuilder MapSongEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/songs", async (SightsAndSoundsDbContext db) =>
            await db.Songs.ToListAsync());

        routes.MapGet("/songs/{id}", async (int id, SightsAndSoundsDbContext db) =>
            await db.Songs.FindAsync(id) is Song song ? Results.Ok(song) : Results.NotFound());

        routes.MapPost("/songs", async (Song song, SightsAndSoundsDbContext db) =>
        {
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            return Results.Created($"/songs/{song.Id}", song);
        });

        routes.MapPut("/songs/{id}", async (int id, Song input, SightsAndSoundsDbContext db) =>
        {
            var song = await db.Songs.FindAsync(id);
            if (song is null) return Results.NotFound();
            song.Name = input.Name;
            // update other properties as needed
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        routes.MapDelete("/songs/{id}", async (int id, SightsAndSoundsDbContext db) =>
        {
            var song = await db.Songs.FindAsync(id);
            if (song is null) return Results.NotFound();
            db.Songs.Remove(song);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return routes;
    }
}