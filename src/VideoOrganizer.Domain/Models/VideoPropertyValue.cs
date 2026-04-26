namespace VideoOrganizer.Domain.Models;

// A property value attached to a single Video.
public class VideoPropertyValue
{
    public Guid VideoId { get; set; }
    public Video? Video { get; set; }

    public Guid PropertyDefinitionId { get; set; }
    public PropertyDefinition? PropertyDefinition { get; set; }

    public string Value { get; set; } = string.Empty;
}
