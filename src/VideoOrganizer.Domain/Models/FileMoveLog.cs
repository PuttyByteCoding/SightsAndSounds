namespace VideoOrganizer.Domain.Models;

// An audit record of a file-move operation (issue #4): the video's file
// was moved FromPath -> ToPath at MovedAt. Kept after the move so the
// operation is both logged and reversible — RevertedAt is null while the
// move can still be undone, and set once the file has been moved back.
public class FileMoveLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VideoId { get; set; }
    public Video? Video { get; set; }

    // Snapshot of the file name at move time, so the Moves list can label
    // the row even if the video row is later deleted.
    public string FileName { get; set; } = string.Empty;
    public string FromPath { get; set; } = string.Empty;
    public string ToPath { get; set; } = string.Empty;

    public DateTime MovedAt { get; set; } = DateTime.UtcNow;

    // Null = the move is still revertable; set when it has been undone.
    public DateTime? RevertedAt { get; set; }
}
