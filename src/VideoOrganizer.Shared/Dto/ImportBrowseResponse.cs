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
