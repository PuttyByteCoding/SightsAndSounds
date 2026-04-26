namespace VideoOrganizer.Domain.Models;

// Join entity: a video has a tag.
public class VideoTag
{
    public Guid VideoId { get; set; }
    public Video? Video { get; set; }

    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
}
