using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;

public class SightsAndSoundsDbContext : DbContext
{
    public SightsAndSoundsDbContext(DbContextOptions<SightsAndSoundsDbContext> options)
        : base(options) { }

    public DbSet<Song> Songs { get; set; }
    public DbSet<Track> Tracks { get; set; }
    public DbSet<Concert> Concerts { get; set; }
    public DbSet<Venue> Venues { get; set; }
}