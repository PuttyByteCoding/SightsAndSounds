namespace VideoOrganizer.Domain.Models;

// How tag names typed/pasted into this group are normalized on save (#207).
// Kept in sync (same order) with Shared.Dto.TextFormatOption — the API maps
// between the two by integer value, same as the other domain/wire enum twins.
public enum TextFormatOption
{
    NoFormatting,
    TitleCase,
    AllLowercase,
    AllUppercase
}

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

    // Case normalization applied to tag names as they're created/renamed in
    // this group (#207). NoFormatting keeps names exactly as typed/pasted.
    public TextFormatOption TextFormat { get; set; } = TextFormatOption.NoFormatting;

    public List<Tag> Tags { get; set; } = new();
    public List<PropertyDefinition> PropertyDefinitions { get; set; } = new();
}
