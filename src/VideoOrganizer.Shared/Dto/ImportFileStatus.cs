namespace VideoOrganizer.Shared.Dto;

public enum ImportFileStatus
{
    // Registered for the job but not yet picked up by the import loop.
    // Import service writes one of these per discovered file at start so
    // the "Show Queue" modal can list everything still to do, not just
    // the file currently in flight.
    Pending,
    Importing,
    Completed,
    Failed,
    Skipped
}
