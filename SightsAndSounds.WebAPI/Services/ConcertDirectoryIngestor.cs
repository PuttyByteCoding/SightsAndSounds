using SightsAndSounds.Shared.Models;
public class ConcertDirectoryIngestor
{
    private readonly SightsAndSoundsDbContext _db;

    public ConcertDirectoryIngestor(SightsAndSoundsDbContext db)
    {
        _db = db;
    }

    public void Ingest(string rootDirectory)
    {
        foreach (var dir in Directory.GetDirectories(rootDirectory))
        {
            var concert = new Concert
            {
                Id = Guid.NewGuid(),
                ConcertDirPath = dir,
                // Set other properties as needed
            };

            foreach (var file in Directory.GetFiles(dir, "*.*"))
            {
                var track = new Track
                {
                    Id = Guid.NewGuid(),
                    TrackFileLocation = file,
                    // Set other properties as needed
                };
                concert.Setlist.Add(track);
            }

            _db.Concerts.Add(concert);
        }
        _db.SaveChanges();
    }
}