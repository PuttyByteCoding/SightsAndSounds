using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class OcrTextLineConfiguration : IEntityTypeConfiguration<OcrTextLine>
{
    public void Configure(EntityTypeBuilder<OcrTextLine> builder)
    {
        builder.ToTable("ocr_text_lines");
        builder.HasKey(o => o.Id);

        // Hits for one video are listed in playhead order (the results panel);
        // index VideoId+TimeSeconds for that and for resume-position lookups.
        builder.HasIndex(o => new { o.VideoId, o.TimeSeconds });

        // Cascade with the video — a deleted video's OCR text goes with it.
        builder.HasOne(o => o.Video)
            .WithMany(v => v.OcrTextLines)
            .HasForeignKey(o => o.VideoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
