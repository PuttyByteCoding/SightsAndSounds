namespace VideoOrganizer.Shared.Dto;

public record ImportBrowseDirectory(
    string Name,
    string FullPath,
    bool HasSubdirectories,
    int VideoCount = 0,      // Recursive count of video files under this folder
    int ImportedCount = 0    // Subset of VideoCount already present in the DB
);

public record ImportBrowseResponse(
    string CurrentPath,
    string? ParentPath,
    List<ImportBrowseDirectory> Directories
);

// Live progress of an in-flight /import/browse directory scan, polled by
// the Import page to show a climbing "Discovered N video files…" count.
// Scanning is false once the walk finishes; Discovered holds the final
// total until the next scan starts. (issue #27)
public record ImportScanProgressDto(
    bool Scanning,
    int Discovered
);
