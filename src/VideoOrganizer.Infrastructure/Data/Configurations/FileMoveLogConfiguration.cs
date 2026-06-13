using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class FileMoveLogConfiguration : IEntityTypeConfiguration<FileMoveLog>
{
    public void Configure(EntityTypeBuilder<FileMoveLog> builder)
    {
        builder.ToTable("file_move_logs");
        builder.HasKey(m => m.Id);

        // The Moves list is ordered newest-first; index MovedAt for it.
        builder.HasIndex(m => m.MovedAt);

        // Cascade with the video — a deleted video's move history goes too.
        builder.HasOne(m => m.Video)
            .WithMany()
            .HasForeignKey(m => m.VideoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
