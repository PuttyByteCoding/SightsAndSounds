using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class VideoPropertyValueConfiguration : IEntityTypeConfiguration<VideoPropertyValue>
{
    public void Configure(EntityTypeBuilder<VideoPropertyValue> builder)
    {
        builder.ToTable("video_property_values");
        builder.HasKey(v => new { v.VideoId, v.PropertyDefinitionId });

        builder.HasOne(v => v.Video)
            .WithMany(vid => vid.PropertyValues)
            .HasForeignKey(v => v.VideoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(v => v.PropertyDefinition)
            .WithMany()
            .HasForeignKey(v => v.PropertyDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(v => v.Value).HasMaxLength(8192);

        builder.HasIndex(v => v.PropertyDefinitionId);
    }
}
