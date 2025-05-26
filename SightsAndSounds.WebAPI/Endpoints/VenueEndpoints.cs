using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;

public static class VenueEndpoints
{
    public static IEndpointRouteBuilder MapVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/venues", async (SightsAndSoundsDbContext db) =>
            await db.Venues.ToListAsync())
            .WithName("GetAllVenues")
            .WithTags("Venues")
            .WithSummary("Get all venues")
            .WithDescription("Retrieves a list of all venues in the database.");

        routes.MapGet("/venues/{id}", async (int id, SightsAndSoundsDbContext db) =>
            await db.Venues.FindAsync(id) is Venue venue ? Results.Ok(venue) : Results.NotFound())
            .WithName("GetVenueById")
            .WithTags("Venues")
            .WithSummary("Get a venue by ID")
            .WithDescription("Retrieves a single venue by its unique identifier.");

        routes.MapPost("/venues", async (Venue venue, SightsAndSoundsDbContext db) =>
        {
            db.Venues.Add(venue);
            await db.SaveChangesAsync();
            return Results.Created($"/venues/{venue.Id}", venue);
        })
            .WithName("CreateVenue")
            .WithTags("Venues")
            .WithSummary("Create a new venue")
            .WithDescription("Adds a new venue to the database.");

        routes.MapPut("/venues/{id}", async (int id, Venue input, SightsAndSoundsDbContext db) =>
        {
            var venue = await db.Venues.FindAsync(id);
            if (venue is null) return Results.NotFound();
            venue.Name = input.Name;
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("UpdateVenue")
            .WithTags("Venues")
            .WithSummary("Update a venue");

        routes.MapDelete("/venues/{id}", async (int id, SightsAndSoundsDbContext db) =>
        {
            var venue = await db.Venues.FindAsync(id);
            if (venue is null) return Results.NotFound();
            db.Venues.Remove(venue);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("DeleteVenue")
            .WithTags("Venues")
            .WithSummary("Delete a venue")
            .WithDescription("Removes a venue from the database by ID.");

        return routes;
    }
}