namespace VideoOrganizer.Shared.Dto;

// Body of POST /api/videos/{parentId}/clips. StartSeconds / EndSeconds define
// the in/out inside the parent video's file; Name is optional — when omitted
// the API auto-generates "{parent filename} [start-end]".
public sealed class CreateClipRequest
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string? Name { get; set; }
}
