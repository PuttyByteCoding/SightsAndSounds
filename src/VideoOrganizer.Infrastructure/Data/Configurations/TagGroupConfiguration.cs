using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class TagGroupConfiguration : IEntityTypeConfiguration<TagGroup>
{
    public void Configure(EntityTypeBuilder<TagGroup> builder)
    {
        builder.ToTable("tag_groups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).IsRequired().HasMaxLength(200);
        builder.HasIndex(g => g.Name).IsUnique();

        builder.Property(g => g.AllowMultiple).IsRequired().HasDefaultValue(true);
        builder.Property(g => g.DisplayAsCheckboxes).IsRequired().HasDefaultValue(false);
        builder.Property(g => g.SortOrder).HasDefaultValue(0);
        builder.Property(g => g.Notes).HasMaxLength(4096);

        builder.HasMany(g => g.Tags)
            .WithOne(t => t.TagGroup)
            .HasForeignKey(t => t.TagGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(g => g.PropertyDefinitions)
            .WithOne(p => p.TagGroup)
            .HasForeignKey(p => p.TagGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
