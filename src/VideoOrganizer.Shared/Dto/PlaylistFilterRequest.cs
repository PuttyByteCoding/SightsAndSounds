namespace VideoOrganizer.Shared.Dto;

// What kind of filter slot this entry refers to. The bulk of filtering is by
// Tag — the rest are derived from non-tag Video columns/paths.
//
// Wire format is camelCase via LenientEnumConverterFactory.ToWireCamelCase.
public enum FilterRefType
{
    Tag,           // Value = Tag.Id (Guid string)
    Folder,        // Value = absolute folder path
    Missing,       // Value = "tagGroup:<groupId>"
    // System-managed structural bools. Value is one of:
    //   "needsReview" | "wontPlay" | "markedForDeletion"
    // These three live as direct columns on Video and aren't tags.
    Status
}

// Opaque filter reference. Interpretation of Value depends on Type.
public sealed class FilterRef
{
    public FilterRefType Type { get; set; }
    public string Value { get; set; } = string.Empty;
}

// Three-way tag filter applied when generating a playlist or filtering the
// browse view. Slot semantics:
//   Required -> video must match ALL of these (AND)
//   Optional -> video must match AT LEAST ONE (OR), grouped per slot type
//   Excluded -> video must match NONE (NOT)
// An empty/missing list disables that slot.
public sealed class PlaylistFilterRequest
{
    public List<FilterRef> Required { get; set; } = new();
    public List<FilterRef> Optional { get; set; } = new();
    public List<FilterRef> Excluded { get; set; } = new();
}
