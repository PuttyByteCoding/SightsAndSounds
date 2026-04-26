using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class VideoSetConfiguration : IEntityTypeConfiguration<VideoSet>
{
    public void Configure(EntityTypeBuilder<VideoSet> builder)
    {
        builder.ToTable("video_sets");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Name).IsRequired().HasMaxLength(100);
        builder.HasIndex(v => v.Name).IsUnique();

        builder.Property(v => v.Path).IsRequired().HasMaxLength(500);

        builder.Property(v => v.Enabled).HasDefaultValue(true);
        builder.Property(v => v.SortOrder).HasDefaultValue(0);
    }
}
