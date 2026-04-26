using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class PropertyDefinitionConfiguration : IEntityTypeConfiguration<PropertyDefinition>
{
    public void Configure(EntityTypeBuilder<PropertyDefinition> builder)
    {
        builder.ToTable("property_definitions");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.DataType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.TagGroupId);
        builder.Property(p => p.Required).IsRequired().HasDefaultValue(false);
        builder.Property(p => p.SortOrder).HasDefaultValue(0);
        builder.Property(p => p.Notes).HasMaxLength(4096);

        // A property name must be unique within its scope+group:
        //   - Tag-scoped: unique per (TagGroupId, Name)
        //   - Video-scoped: unique per (Name) — TagGroupId is null
        // Postgres treats multiple NULLs as distinct, which is fine here.
        builder.HasIndex(p => new { p.Scope, p.TagGroupId, p.Name }).IsUnique();
    }
}
