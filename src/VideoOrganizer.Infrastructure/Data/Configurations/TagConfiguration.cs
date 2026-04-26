using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("tags");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(500);
        builder.Property(t => t.IsFavorite).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.SortOrder).HasDefaultValue(0);
        builder.Property(t => t.Notes).HasMaxLength(4096);

        // (TagGroupId, Name) unique — same tag name can repeat across groups.
        builder.HasIndex(t => new { t.TagGroupId, t.Name }).IsUnique();

        var stringListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<List<string>>(v, JsonSerializerOptions.Default) ?? new());

        var stringListComparer = new ValueComparer<List<string>>(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            c => c.Aggregate(0, (h, v) => HashCode.Combine(h, v ?? string.Empty)),
            c => c.ToList());

        builder.Property(t => t.Aliases)
            .HasConversion(stringListConverter)
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(stringListComparer);
    }
}
