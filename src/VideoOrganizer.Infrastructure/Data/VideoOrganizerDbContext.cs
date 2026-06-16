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

    public DbSet<DuplicateCandidate> DuplicateCandidates => Set<DuplicateCandidate>();

    public DbSet<FileMoveLog> FileMoveLogs => Set<FileMoveLog>();

    // Postgres md5(text). Used to derive a stable, seedable shuffle order for
    // keyset pagination (#127): ORDER BY md5(id || seed) is deterministic per
    // seed, so pages don't overlap or repeat. DB-only — never called in memory.
    [DbFunction("md5", IsBuiltIn = true)]
    public static string Md5(string input) => throw new NotSupportedException("md5 is evaluated by Postgres");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VideoOrganizerDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
