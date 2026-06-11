using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Infrastructure.Data.Configurations;

public sealed class VideoConfiguration : IEntityTypeConfiguration<Video>
{
    public void Configure(EntityTypeBuilder<Video> builder)
    {
        builder.ToTable("videos");
        builder.HasKey(v => v.Id);

        // File Metadata
        builder.Property(v => v.FileName).IsRequired().HasMaxLength(255);
        builder.Property(v => v.FilePath).IsRequired().HasMaxLength(4096);
        builder.Property(v => v.Md5).HasMaxLength(32);
        builder.Property(v => v.FileSize).IsRequired().HasColumnType("bigint");
        builder.Property(v => v.Md5Failed).IsRequired().HasDefaultValue(false);
        builder.Property(v => v.ThumbnailsFailed).IsRequired().HasDefaultValue(false);
        builder.Property(v => v.ThumbnailsGenerated).IsRequired().HasDefaultValue(false);
        builder.Property(v => v.ImportJobId);

        // Video Metadata
        builder.Property(v => v.Duration).IsRequired().HasColumnType("interval");
        builder.Property(v => v.Width).IsRequired();
        builder.Property(v => v.Height).IsRequired();
        builder.Property(v => v.VideoDimensionFormat).HasConversion<string>().HasMaxLength(64);
        builder.Property(v => v.VideoCodec).HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.Bitrate).IsRequired().HasColumnType("bigint");
        builder.Property(v => v.FrameRate).IsRequired().HasColumnType("double precision");
        builder.Property(v => v.PixelFormat).HasMaxLength(64);
        builder.Property(v => v.Ratio).HasMaxLength(16);
        builder.Property(v => v.CreationTime);
        builder.Property(v => v.VideoStreamCount).IsRequired();
        builder.Property(v => v.AudioStreamCount).IsRequired();

        builder.Property(v => v.IngestDate).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(v => v.WatchCount).HasDefaultValue(0);
        builder.Property(v => v.Notes).HasMaxLength(4096);

        // Structural status flags (not user tags) — see Video.cs.
        // NeedsReview defaults to true so any insert path (UI, importer, raw
        // SQL) gets it automatically.
        builder.Property(v => v.NeedsReview).IsRequired().HasDefaultValue(true);
        builder.Property(v => v.PlaybackIssue).IsRequired().HasDefaultValue(false);
        builder.Property(v => v.MarkedForDeletion).IsRequired().HasDefaultValue(false);
        builder.Property(v => v.IsFavorite).IsRequired().HasDefaultValue(false);

        builder.Property(v => v.CameraType).HasConversion<string>().HasMaxLength(64);
        builder.Property(v => v.VideoQuality).HasConversion<string>().HasMaxLength(64);

        // JSONB list columns (chapter markers + video blocks)
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());

        var chapterConverter = new ValueConverter<List<ChapterMarker>, string>(
            v => JsonSerializer.Serialize(v, json),
            v => JsonSerializer.Deserialize<List<ChapterMarker>>(v, json) ?? new());

        var blocksConverter = new ValueConverter<List<VideoBlock>, string>(
            v => JsonSerializer.Serialize(v, json),
            v => JsonSerializer.Deserialize<List<VideoBlock>>(v, json) ?? new());

        var chapterComparer = new ValueComparer<List<ChapterMarker>>(
            (a, b) => a != null && b != null
                      && a.Count == b.Count
                      && a.Zip(b).All(p => p.First.Offset == p.Second.Offset &&
                                           p.First.Comment == p.Second.Comment),
            v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.Offset, x.Comment ?? string.Empty)),
            v => v.Select(x => new ChapterMarker { Offset = x.Offset, Comment = x.Comment }).ToList());

        var blocksComparer = new ValueComparer<List<VideoBlock>>(
            (a, b) => a != null && b != null
                      && a.Count == b.Count
                      && a.Zip(b).All(p => p.First.OffsetInSeconds == p.Second.OffsetInSeconds &&
                                           p.First.LengthInSeconds == p.Second.LengthInSeconds &&
                                           p.First.VideoBlockType == p.Second.VideoBlockType),
            v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.OffsetInSeconds, x.LengthInSeconds, x.VideoBlockType)),
            v => v.Select(x => new VideoBlock
            {
                OffsetInSeconds = x.OffsetInSeconds,
                LengthInSeconds = x.LengthInSeconds,
                VideoBlockType = x.VideoBlockType
            }).ToList());

        builder.Property(v => v.ChapterMarkers)
            .HasConversion(chapterConverter)
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(chapterComparer);

        builder.Property(v => v.VideoBlocks)
            .HasConversion(blocksConverter)
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(blocksComparer);

        // Clip self-reference. Cascade-delete clips when the parent goes.
        builder.Property(v => v.ParentVideoId);
        builder.Property(v => v.ClipStartSeconds).HasColumnType("double precision");
        builder.Property(v => v.ClipEndSeconds).HasColumnType("double precision");
        builder.HasOne<Video>()
            .WithMany()
            .HasForeignKey(v => v.ParentVideoId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        builder.HasIndex(v => v.Md5);
        // Uniqueness on (FilePath, FileName) covers parent videos only — clips
        // intentionally reuse the parent's path. ParentVideoId IS NULL is the
        // structural successor of the old "IsClip = false" filter.
        builder.HasIndex(v => new { v.FilePath, v.FileName })
            .IsUnique()
            .HasFilter("\"ParentVideoId\" IS NULL");
        builder.HasIndex(v => v.ParentVideoId);
        builder.HasIndex(v => v.IngestDate);
        builder.HasIndex(v => v.ImportJobId);
    }
}
