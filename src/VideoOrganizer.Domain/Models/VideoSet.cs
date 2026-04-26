namespace VideoOrganizer.Domain.Models;

// A named video root. Videos are associated with a set by matching
// Video.FilePath's prefix against Path.
public class VideoSet
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}
