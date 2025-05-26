using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;
using System;

public static class ConcertEndpoints
{
    public static IEndpointRouteBuilder MapConcertEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/concerts", async (SightsAndSoundsDbContext db) =>
            await db.Concerts.ToListAsync());

        routes.MapGet("/concerts/{id}", async (int id, SightsAndSoundsDbContext db) =>
            await db.Concerts.FindAsync(id) is Concert concert ? Results.Ok(concert) : Results.NotFound());

        routes.MapPost("/concerts", async (Concert concert, SightsAndSoundsDbContext db) =>
        {
            db.Concerts.Add(concert);
            await db.SaveChangesAsync();
            return Results.Created($"/concerts/{concert.Id}", concert);
        });

        routes.MapPut("/concerts/{id}", async (int id, Concert input, SightsAndSoundsDbContext db) =>
        {
            var concert = await db.Concerts.FindAsync(id);
            if (concert is null) return Results.NotFound();
            concert.Id = input.Id;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        routes.MapDelete("/concerts/{id}", async (int id, SightsAndSoundsDbContext db) =>
        {
            var concert = await db.Concerts.FindAsync(id);
            if (concert is null) return Results.NotFound();
            db.Concerts.Remove(concert);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return routes;
    }
}