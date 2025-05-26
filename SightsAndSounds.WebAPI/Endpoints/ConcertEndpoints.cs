using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;
using System;

public static class ConcertEndpoints
{
    public static IEndpointRouteBuilder MapConcertEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/concerts", async (SightsAndSoundsDbContext db) =>
            await db.Concerts.ToListAsync())
            .WithName("GetAllConcerts")
            .WithTags("Concerts")
            .WithSummary("Get all concerts")
            .WithDescription("Retrieves a list of all concerts in the database.");

        routes.MapGet("/concerts/{id}", async (int id, SightsAndSoundsDbContext db) =>
            await db.Concerts.FindAsync(id) is Concert concert ? Results.Ok(concert) : Results.NotFound())
            .WithName("GetConcertById")
            .WithTags("Concerts")
            .WithSummary("Get a concert by ID")
            .WithDescription("Retrieves a single concert by its unique identifier.");

        routes.MapPost("/concerts", async (Concert concert, SightsAndSoundsDbContext db) =>
        {
            db.Concerts.Add(concert);
            await db.SaveChangesAsync();
            return Results.Created($"/concerts/{concert.Id}", concert);
        })
            .WithName("CreateConcert")
            .WithTags("Concerts")
            .WithSummary("Create a new concert")
            .WithDescription("Adds a new concert to the database.");

        routes.MapPut("/concerts/{id}", async (int id, Concert input, SightsAndSoundsDbContext db) =>
        {
            var concert = await db.Concerts.FindAsync(id);
            if (concert is null) return Results.NotFound();
            concert.Id = input.Id;
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("UpdateConcert")
            .WithTags("Concerts")
            .WithSummary("Update a concerts");

        routes.MapDelete("/concerts/{id}", async (int id, SightsAndSoundsDbContext db) =>
        {
            var concert = await db.Concerts.FindAsync(id);
            if (concert is null) return Results.NotFound();
            db.Concerts.Remove(concert);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("DeleteConcert")
            .WithTags("Concerts")
            .WithSummary("Delete a concerts")
            .WithDescription("Removes a concerts from the database by ID.");

        return routes;
    }
}