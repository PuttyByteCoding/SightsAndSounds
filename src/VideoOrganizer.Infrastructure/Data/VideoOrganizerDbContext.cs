using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data;

public sealed class VideoOrganizerDbContext : DbContext
{
    public VideoOrganizerDbContext(DbContextOptions<VideoOrganizerDbContext> options) : base(options)
    {
    }

    public DbSet<Video> Videos => Set<Video>();
    public DbSet<VideoSet> VideoSets => Set<VideoSet>();

    public DbSet<TagGroup> TagGroups => Set<TagGroup>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<VideoTag> VideoTags => Set<VideoTag>();

    public DbSet<PropertyDefinition> PropertyDefinitions => Set<PropertyDefinition>();
    public DbSet<TagPropertyValue> TagPropertyValues => Set<TagPropertyValue>();
    public DbSet<VideoPropertyValue> VideoPropertyValues => Set<VideoPropertyValue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VideoOrganizerDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
