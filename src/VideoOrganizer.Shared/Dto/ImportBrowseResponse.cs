namespace VideoOrganizer.Shared.Dto;

public record ImportBrowseDirectory(
    string Name,
    string FullPath,
    bool HasSubdirectories,
    // Recursive count of video files under this folder. NULL when not computed
    // yet: /import/browse returns the tree immediately without walking the disk,
    // and the client fills these in lazily via /import/folder-count (#197).
    int? VideoCount = null,
    int ImportedCount = 0    // Subset of VideoCount already present in the DB
);

public record ImportBrowseResponse(
    string CurrentPath,
    string? ParentPath,
    List<ImportBrowseDirectory> Directories
);

// Lazily-computed recursive video count for a single folder, fetched per
// folder after the browse tree renders so the disk walk never blocks the
// initial listing (issue #197).
public record ImportFolderCount(string Path, int VideoCount);

// A folder that already contains imported videos — the destination choices
// for the move dialog (issue #4). Derived from the library (distinct parent
// folders of imported videos), so listing it is a fast DB read rather than a
// filesystem walk. Label is a source-relative display path ("Source / sub /
// folder"); FullPath is the absolute path the move targets.
public record ImportedFolder(
    string FullPath,
    string Label,
    int VideoCount
);

// Remove every imported video under a folder from the library (issue #53).
// Files on disk are untouched; only the DB rows (and their cascaded tags,
// properties, duplicate pairs, move logs, and clips) go away.
public record RemoveFolderRequest(string Path);

public record RemoveFolderResponse(int Removed);

// Live progress of an in-flight /import/browse directory scan, polled by
// the Import page to show a climbing "Discovered N video files…" count.
// Scanning is false once the walk finishes; Discovered holds the final
// total until the next scan starts. (issue #27)
public record ImportScanProgressDto(
    bool Scanning,
    int Discovered
);
