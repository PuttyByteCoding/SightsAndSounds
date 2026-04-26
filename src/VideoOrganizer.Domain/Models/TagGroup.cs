namespace VideoOrganizer.Domain.Models;

public class TagGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool AllowMultiple { get; set; } = true;
    // When true, the EditTagsPanel renders this group as a checkbox list
    // (every tag in the group, with a checkbox per tag) instead of the
    // default pill + autocomplete UI. Also makes the group's tags eligible
    // for the Alt+1..9 keyboard toggle in the player. Only the first
    // checkbox-mode group (by SortOrder) is bound to keyboard shortcuts.
    public bool DisplayAsCheckboxes { get; set; }
    public int SortOrder { get; set; }
    public string Notes { get; set; } = string.Empty;

    public List<Tag> Tags { get; set; } = new();
    public List<PropertyDefinition> PropertyDefinitions { get; set; } = new();
}
