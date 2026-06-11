using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class DuplicateCandidateConfiguration : IEntityTypeConfiguration<DuplicateCandidate>
{
    public void Configure(EntityTypeBuilder<DuplicateCandidate> builder)
    {
        builder.ToTable("duplicate_candidates");
        builder.HasKey(d => d.Id);

        // One row per pair — the API normalizes (A, B) ordering before
        // insert so the index can't be defeated by swapping the ids.
        builder.HasIndex(d => new { d.VideoAId, d.VideoBId }).IsUnique();

        // Review pages filter by status; cheap index keeps that snappy
        // at large pair counts.
        builder.HasIndex(d => d.Status);

        builder.HasOne(d => d.VideoA)
            .WithMany()
            .HasForeignKey(d => d.VideoAId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.VideoB)
            .WithMany()
            .HasForeignKey(d => d.VideoBId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
