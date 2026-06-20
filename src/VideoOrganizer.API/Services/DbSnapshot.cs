using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.API.Services;

// The whole database as plain lists, the shape of a JSON snapshot backup
// (issue #32). Ordered parents-before-children so a restore can insert in
// array order without tripping foreign keys. Restore (a later PR) deserializes
// the same type.
public sealed class DbSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }

    public List<VideoSet> VideoSets { get; set; } = new();
    public List<TagGroup> TagGroups { get; set; } = new();
    public List<Tag> Tags { get; set; } = new();
    public List<PropertyDefinition> PropertyDefinitions { get; set; } = new();
    public List<Video> Videos { get; set; } = new();
    public List<VideoTag> VideoTags { get; set; } = new();
    public List<TagPropertyValue> TagPropertyValues { get; set; } = new();
    public List<VideoPropertyValue> VideoPropertyValues { get; set; } = new();
    public List<DuplicateCandidate> DuplicateCandidates { get; set; } = new();
    public List<FileMoveLog> FileMoveLogs { get; set; } = new();
}
