namespace VideoOrganizer.Domain.Models;

public enum DuplicateStatus
{
    // Marked by the user during a duplicate hunt; awaiting review.
    Pending = 0,
    // Reviewed and confirmed to be the same content.
    Confirmed = 1,
    // Reviewed and judged NOT to be duplicates. Kept (not deleted) so
    // the pair can't be accidentally re-flagged and re-reviewed later.
    Rejected = 2
}

// A user-flagged "these two videos might be the same content" pair.
// VideoAId/VideoBId are stored in normalized order (A's Guid sorts
// before B's) so the same pair can't be recorded twice in opposite
// orders — the unique index on (VideoAId, VideoBId) then enforces
// one row per pair.
public class DuplicateCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VideoAId { get; set; }
    public Video? VideoA { get; set; }

    public Guid VideoBId { get; set; }
    public Video? VideoB { get; set; }

    public DuplicateStatus Status { get; set; } = DuplicateStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
