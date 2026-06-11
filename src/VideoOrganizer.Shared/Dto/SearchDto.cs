using System.Text.Json.Serialization;

namespace VideoOrganizer.Shared.Dto;

// Wire shape for /api/search.
//
// Discriminated union: every result is a SearchResult with a string
// `kind` discriminator that tells the client what shape the rest of
// the object has. v1 only emits "video" results — but every field on
// SearchResponse (apart from `results`) plus the discriminator pattern
// itself is designed so v2 can add `kind: "tag"`, `kind: "source"`,
// etc. as additional [JsonDerivedType] entries below, with no API
// shape change required.
//
// PropertyNaming is camelCase via the API's ConfigureHttpJsonOptions,
// so `Kind`, `MatchedFields` etc. land on the wire as `kind`,
// `matchedFields`. The JsonPolymorphic attribute's discriminator
// property name is independent of that and is set explicitly to
// "kind" for clarity.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(VideoSearchResult), typeDiscriminator: "video")]
public abstract record SearchResult;

// One matching video. Compact — enough to render a thumbnail row in
// the command palette and route to /browse?id={Id} on click. If a
// caller needs the full VideoDto shape, they hit /api/videos/{id}
// after the user picks a result.
public sealed record VideoSearchResult(
    Guid Id,
    string Title,            // typically the file name (raw, no ellipsis)
    string Subtitle,         // typically the file path
    long FileSize,
    TimeSpan Duration,
    bool IsClip,             // true if this is a clip (ParentVideoId set)
    IReadOnlyList<string> Tags,          // flat list of tag names (for chip rendering)
    IReadOnlyList<string> MatchedFields  // which fields the query hit, e.g.
                                         // ["fileName"], ["filePath", "tag:Performer/Bob Marley"].
                                         // Lets the UI badge or highlight per-result.
) : SearchResult;

// Top-level envelope for /api/search responses. `truncated` is true
// when more results matched than `limit` returned — UI can show a
// "+N more" hint or paginate.
public sealed record SearchResponse(
    string Query,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<SearchResult> Results);
