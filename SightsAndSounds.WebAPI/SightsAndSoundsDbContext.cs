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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<Venue>()
            .ToTable(t => t.HasCheckConstraint("CK_Venue_Type", "Type IN (0, 1, 2, 3, 4, 5, 6, 7, 8)"));
    }
}