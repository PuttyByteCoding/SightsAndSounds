namespace VideoOrganizer.Shared.Dto;

// Wire mirror of Domain.Models.DuplicateStatus.
public enum DuplicateStatusDto
{
    Pending = 0,
    Confirmed = 1,
    Rejected = 2
}

// One flagged pair with both sides fully projected — the review page
// renders a property-by-property comparison, so it needs the complete
// VideoDto for each side rather than a slim summary.
public record DuplicateCandidateDto(
    Guid Id,
    DuplicateStatusDto Status,
    DateTime CreatedAt,
    VideoDto VideoA,
    VideoDto VideoB);

// POST body for /api/duplicates. Order doesn't matter — the server
// normalizes the pair before storing.
public record CreateDuplicateCandidateRequest(Guid VideoAId, Guid VideoBId);
