using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;

public static class VenueEndpoints
{
    public static IEndpointRouteBuilder MapVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/venues", async (SightsAndSoundsDbContext db) =>
            await db.Venues.ToListAsync());

        routes.MapGet("/venues/{id}", async (int id, SightsAndSoundsDbContext db) =>
            await db.Venues.FindAsync(id) is Venue venue ? Results.Ok(venue) : Results.NotFound());

        routes.MapPost("/venues", async (Venue venue, SightsAndSoundsDbContext db) =>
        {
            db.Venues.Add(venue);
            await db.SaveChangesAsync();
            return Results.Created($"/venues/{venue.Id}", venue);
        });

        routes.MapPut("/venues/{id}", async (int id, Venue input, SightsAndSoundsDbContext db) =>
        {
            var venue = await db.Venues.FindAsync(id);
            if (venue is null) return Results.NotFound();
            venue.Name = input.Name;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        routes.MapDelete("/venues/{id}", async (int id, SightsAndSoundsDbContext db) =>
        {
            var venue = await db.Venues.FindAsync(id);
            if (venue is null) return Results.NotFound();
            db.Venues.Remove(venue);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return routes;
    }
}