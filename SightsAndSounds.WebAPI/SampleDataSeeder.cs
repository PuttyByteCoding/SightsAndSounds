using SightsAndSounds.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public static class SampleDataSeeder
{
    public static void Seed(SightsAndSoundsDbContext db)
    {        
        if (!db.Songs.Any())
        {
            db.Songs.Add(new Song
            {
                Id = Guid.NewGuid(),
                Name = "Sample Song",
                Album = "Sample Album",
                Playcount = 1,
                AlternateNames = new List<string> { "Sample Alt" },
                Notes = "Sample notes"
            });
            // Add more sample data as needed
            db.SaveChanges();
        }

        if (!db.Venues.Any())
        {
            db.Venues.Add(new Venue
            {
                Id = Guid.NewGuid(),
                Name = "Sample Venue",
                City = "Sample City",
                State = "Sample State",
                Country = "Sample Country",
                Type = VenueType.Arena,
                AlternateNames = new List<string> { "Sample Alt Venue" },
                Notes = "Sample venue notes"
            });
            // Add more sample data as needed
            db.SaveChanges();
        }

        if (!db.Concerts.Any())
        {
            db.Concerts.Add(new Concert
            {
                Id = Guid.NewGuid(),
                Date = DateTime.UtcNow,
                VenueId = db.Venues.First().Id, // Assuming at least one venue exists
                RecordingType = RecordingType.Mic,
                Notes = "Sample concert notes"
            });
            // Add more sample data as needed
            db.SaveChanges();
        }
    }
}