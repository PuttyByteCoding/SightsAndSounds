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
    //   "needsReview" | "playbackIssue" | "markedForDeletion"
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
//
// SearchQuery is a free-text substring (case-insensitive). When non-empty,
// it ANDs with the rest of the filter — only videos whose FileName,
// FilePath, Notes, Md5, OR any tag name contains the substring are kept.
// Pushed down to SQL via ILIKE; reuses the pg_trgm GIN indexes added in
// migration 20260520010000_AddSearchTrigramIndexes for sub-second matches
// at 100k+ rows. Drives the "Play all results" path from the Ctrl+K
// search palette: navigate to /browse?searchQuery=foo, /browse forwards
// it to this endpoint, the returned VideoDto[] becomes the current
// playlist.
public sealed class PlaylistFilterRequest
{
    public List<FilterRef> Required { get; set; } = new();
    public List<FilterRef> Optional { get; set; } = new();
    public List<FilterRef> Excluded { get; set; } = new();
    public string? SearchQuery { get; set; }
}
