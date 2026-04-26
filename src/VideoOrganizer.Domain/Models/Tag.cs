namespace VideoOrganizer.Domain.Models;

public class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TagGroupId { get; set; }
    public TagGroup? TagGroup { get; set; }

    public required string Name { get; set; }
    public List<string> Aliases { get; set; } = new();
    public bool IsFavorite { get; set; }
    public int SortOrder { get; set; }
    public string Notes { get; set; } = string.Empty;

    public List<VideoTag> VideoTags { get; set; } = new();
    public List<TagPropertyValue> PropertyValues { get; set; } = new();
}
