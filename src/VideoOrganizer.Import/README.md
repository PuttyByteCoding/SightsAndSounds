# VideoOrganizer.Import

Shared import library extracted from the ImportTool console app.

## Usage
- Register `VideoOrganizer.Import.Services.DirectoryImportService` in a DI container.
- Provide `VideoStorageOptions` and `IVideoMetadataService`.

## Notes
- Directory import is shared in this library.
- JSON/tag-list import remains in `VideoOrganizer.ImportTool` for now.
