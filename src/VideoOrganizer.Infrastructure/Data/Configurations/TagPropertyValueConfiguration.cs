using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class TagPropertyValueConfiguration : IEntityTypeConfiguration<TagPropertyValue>
{
    public void Configure(EntityTypeBuilder<TagPropertyValue> builder)
    {
        builder.ToTable("tag_property_values");
        builder.HasKey(v => new { v.TagId, v.PropertyDefinitionId });

        builder.HasOne(v => v.Tag)
            .WithMany(t => t.PropertyValues)
            .HasForeignKey(v => v.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(v => v.PropertyDefinition)
            .WithMany()
            .HasForeignKey(v => v.PropertyDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(v => v.Value).HasMaxLength(8192);

        builder.HasIndex(v => v.PropertyDefinitionId);
    }
}
