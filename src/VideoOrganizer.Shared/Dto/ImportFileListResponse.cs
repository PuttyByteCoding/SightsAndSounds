namespace VideoOrganizer.Shared.Dto;

public record ImportFileListResponse(
    string DirectoryPath,
    List<string> Files,
    List<string> NonImportableFiles,
    // Docker paths of files in `Files` that are already in the database.
    // Clients use this to render "already imported" markers or filter them out.
    List<string> ImportedFiles,
    // Hidden (dot-prefixed) files — shown on their own tab, excluded from the
    // importable/other buckets and from the folder counts. (issue #62)
    List<string> HiddenFiles
);
