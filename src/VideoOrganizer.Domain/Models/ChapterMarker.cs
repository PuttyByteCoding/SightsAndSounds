namespace VideoOrganizer.Domain.Models;

public class ChapterMarker
{
    // Offset in seconds from start of video
    public int Offset { get; set; }
    public string Comment { get; set; } = String.Empty;
}