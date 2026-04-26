namespace VideoOrganizer.Domain.Models;

// A property value attached to a single Tag.
public class TagPropertyValue
{
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }

    public Guid PropertyDefinitionId { get; set; }
    public PropertyDefinition? PropertyDefinition { get; set; }

    // Stored as string regardless of DataType. Parsed/formatted at the API
    // boundary using PropertyDefinition.DataType.
    public string Value { get; set; } = string.Empty;
}
