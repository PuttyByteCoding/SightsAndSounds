namespace VideoOrganizer.Domain.Models;

// User-defined custom field. Either attaches to every tag in a TagGroup
// (Scope=Tag, TagGroupId set) or to every Video (Scope=Video, TagGroupId null).
// Field values live in TagPropertyValue / VideoPropertyValue.
public class PropertyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public PropertyDataType DataType { get; set; } = PropertyDataType.Text;
    public PropertyScope Scope { get; set; } = PropertyScope.Tag;
    public Guid? TagGroupId { get; set; }
    public TagGroup? TagGroup { get; set; }
    public bool Required { get; set; }
    public int SortOrder { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public enum PropertyDataType
{
    Text,
    LongText,
    Number,
    Date,
    Boolean,
    Url
}

public enum PropertyScope
{
    Tag,
    Video
}
