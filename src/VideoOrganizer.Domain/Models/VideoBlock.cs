namespace VideoOrganizer.Domain.Models;

public class VideoBlock
{
    public int OffsetInSeconds { get; set; }
    public int LengthInSeconds { get; set; }
    public VideoBlockTypes VideoBlockType { get; set; }
}

public enum VideoBlockTypes
{
    Clip,  // A noteworthy region — can be exported as its own Video.
    Hide,  // Skipped during playback; removed when "download with hides removed" is used.
    Other
}