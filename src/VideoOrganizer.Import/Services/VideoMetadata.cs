namespace VideoOrganizer.Import.Services;

public class VideoMetadata
{
    public TimeSpan? Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
    public long? VideoBitrate { get; set; }
    public double? FrameRate { get; set; }
    public string? PixelFormat { get; set; }
    public string? Ratio { get; set; }
    public DateTime? CreationTime { get; set; }
    public int? VideoStreamCount { get; set; }
    public int? AudioStreamCount { get; set; }
}
