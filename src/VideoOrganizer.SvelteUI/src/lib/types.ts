// Frontend DTO types — DERIVED from the OpenAPI document (#125).
//
// These used to be a hand-maintained mirror of VideoOrganizer.Shared.Dto, which
// silently drifted from the backend. They now alias the generated schemas in
// `api.generated.ts` (regenerated from `ci/openapi.json`; see ci/README.md), so
// the API contract is the single source of truth and a rename/retype on the
// server surfaces here as a compile error.
//
// A few types are intentionally kept local — they have no 1:1 server schema
// (UI-only shapes, the polymorphic search results) or need a tighter shape than
// the generated one. Each is called out below.

import type { components } from './api.generated';

type S = components['schemas'];

// --- Enums -----------------------------------------------------------------

export type CameraTypes = S['CameraTypes'];
export type VideoQuality = S['VideoQuality'];
export type VideoDimensionFormat = S['VideoDimensionFormat'];
export type VideoCodec = S['VideoCodec'];
export type VideoBlockTypes = S['VideoBlockTypes'];
export type ImportFileStatus = S['ImportFileStatus'];
export type PropertyDataType = S['PropertyDataTypeDto'];
export type PropertyScope = S['PropertyScopeDto'];

// --- Tag groups + tags + properties ----------------------------------------

export type TagGroup = S['TagGroupDto'];
export type Tag = S['TagDto'];
export type UpdateTagGroupRequest = S['UpdateTagGroupRequest'];

// Request-DTO optionality (see note below): these params have C# defaults, so
// the API accepts them omitted — but .NET's OpenAPI generator marks every
// non-nullable param `required` regardless of its default. We restore the true
// contract here (required keys explicit, the defaulted ones optional) while
// still deriving field names/types from the generated schema.
export type CreateTagGroupRequest = { name: string } & Partial<S['CreateTagGroupRequest']>;
export type CreateTagRequest =
  { tagGroupId: string; name: string } & Partial<S['CreateTagRequest']>;
export type UpdateTagRequest =
  Omit<S['UpdateTagRequest'], 'hiddenByDefault'> & { hiddenByDefault?: boolean };
export type MergeTagsRequest = S['MergeTagsRequest'];
export type BulkCreateTagsRequest = S['BulkCreateTagsRequest'];
export type BulkCreateTagsResponse = S['BulkCreateTagsResponse'];
export type TagSearchHit = S['TagSearchHit'];

// UI-derived (issue #10): tags suggested from a video's file name / folder.
// No server DTO — built client-side, so it stays hand-written.
export interface TagSuggestion {
  tagId: string;
  tagGroupId: string;
  tagGroupName: string;
  name: string;
  source: string;
  matchedText: string;
}

// --- Global search (Ctrl+K palette) ----------------------------------------
//
// Deliberately polymorphic UI shape (discriminated on `kind`). The generated
// schema models the same data but with an optional discriminator, which is
// awkward for exhaustive narrowing — so the client keeps its own tighter
// definitions here. v1 only emits VideoSearchResult.

export interface VideoSearchResult {
  kind: 'video';
  id: string;
  title: string;
  subtitle: string;
  fileSize: number;
  duration: string;
  isClip: boolean;
  tags: string[];
  matchedFields: string[];
}

export type SearchResult = VideoSearchResult;
//                       | TagSearchResult | SourceSearchResult …   ← v2

export interface SearchResponse {
  query: string;
  totalCount: number;
  truncated: boolean;
  results: SearchResult[];
}

// Client-side request options for the search call (maps to query-string
// params, not a request body) — no server schema.
export interface SearchRequestOpts {
  q: string;
  limit?: number;
  offset?: number;
  kinds?: string;
}

export type PropertyDefinition = S['PropertyDefinitionDto'];
export type CreatePropertyDefinitionRequest = S['CreatePropertyDefinitionRequest'];
export type UpdatePropertyDefinitionRequest = S['UpdatePropertyDefinitionRequest'];
export type PropertyValue = S['PropertyValueDto'];
export type PropertyValueWrite = S['PropertyValueWrite'];
export type SetPropertyValuesRequest = S['SetPropertyValuesRequest'];

export type BackupInfo = S['BackupInfo'];
export type BackupSettings = S['BackupSettings'];

// --- Video DTOs ------------------------------------------------------------

export type ChapterMarker = S['ChapterMarkerDto'];
export type VideoBlock = S['VideoBlockDto'];

// TimeSpan is serialized as "hh:mm:ss" / "d.hh:mm:ss" by System.Text.Json.
// The generated type is just `string`; this alias documents the intent.
export type TimeSpanString = string;

export type VideoTagSummary = S['VideoTagSummaryDto'];
export type Video = S['VideoDto'];

// chapterMarkers/videoBlocks/tagIds/properties are nullable on the server and
// omittable by callers (treated as "unchanged"); the spec marks them required
// keys. Make them optional to match how the client builds the request.
export type UpdateVideoRequest =
  Omit<S['UpdateVideoRequest'], 'chapterMarkers' | 'videoBlocks' | 'tagIds' | 'properties'>
  & Partial<Pick<S['UpdateVideoRequest'], 'chapterMarkers' | 'videoBlocks' | 'tagIds' | 'properties'>>;
export type SetVideoTagsRequest = S['SetVideoTagsRequest'];
export type CreateClipRequest = S['CreateClipRequest'];
export type ClipSummary = S['ClipSummaryDto'];

// Clip export (issue #69).
export type ClipExportQueueItem = S['ClipExportQueueItemDto'];
export type ClipExportProgress = S['ClipExportProgressDto'];
export type KeyframeCut = S['KeyframeCutDto'];

// Block removal (issue #70).
export type BlockRemovalQueueItem = S['BlockRemovalQueueItemDto'];
export type BlockRemovalProgress = S['BlockRemovalProgressDto'];

// --- Filtering -------------------------------------------------------------

export type FilterRefType = S['FilterRefType'];

// Both fields are always populated by the client; the generated schema marks
// them optional (the server class has property initializers).
export type FilterRef = Required<S['FilterRef']>;

// UI-side: carries a display label so chips render without re-resolving.
export interface FilterTag extends FilterRef {
  label: string;
  tagGroupName?: string;
}

export type PlaylistFilterRequest = S['PlaylistFilterRequest'];

// One keyset-paginated page of filtered videos (#127).
export type FilteredVideosPage = S['FilteredVideosPage'];

// Sort modes for the paginated browse query — match the server's `sort` param.
export type BrowseSort = 'shuffle' | 'fileName' | 'fileSize' | 'duration' | 'folderFile';

// --- On-screen text OCR (issue #5) -----------------------------------------

// Text read off a single video frame (the "Read text" button at the playhead).
export type OcrResult = S['OcrResultDto'];

// One stored OCR hit from a full-video scan: the text and where it appears.
export type OcrTextLine = S['OcrTextLineDto'];

// Live state of a background OCR scan (start → poll → stop / resume).
export type OcrScanProgress = S['OcrScanProgressDto'];

// --- Duplicates -------------------------------------------------------------

export type DuplicateStatus = S['DuplicateStatusDto'];
export type DuplicateCandidate = S['DuplicateCandidateDto'];
export type CreateDuplicateCandidateRequest = S['CreateDuplicateCandidateRequest'];

// --- Logs / workers / imports ---------------------------------------------

export type LogEvent = S['LogEvent'];
export type PlaylistDto = S['PlaylistDto'];
export type PlaylistNavigationDto = S['PlaylistNavigationDto'];
export type DirectoryImportRequest = S['DirectoryImportRequest'];
export type ImportBrowseDirectory = S['ImportBrowseDirectory'];
export type ImportFolderCount = S['ImportFolderCount'];
export type ImportBrowseResponse = S['ImportBrowseResponse'];
export type ImportedFolder = S['ImportedFolder'];
export type ImportScanProgress = S['ImportScanProgressDto'];
export type MoveVideoRequest = S['MoveVideoRequest'];
export type MoveProgress = S['MoveProgressDto'];
export type FileMoveLog = S['FileMoveLogDto'];
export type ImportFileListResponse = S['ImportFileListResponse'];
export type ImportFileProgressDto = S['ImportFileProgressDto'];
export type WorkerFailedRow = S['WorkerFailedRowDto'];
export type WorkerQueueRow = S['WorkerQueueRowDto'];
export type Md5DuplicateRow = S['Md5DuplicateRowDto'];
export type ImportFailedFileRow = S['ImportFailedFileDto'];
export type ImportQueueFileRow = S['ImportQueueFileDto'];
export type ImportProgressResponse = S['ImportProgressResponse'];
export type ImportTaskProgress = S['ImportTaskProgressDto'];
export type ImportJobSummary = S['ImportJobSummaryDto'];

// --- Runtime / Diagnostics -------------------------------------------------

// Generated `os` is a bare string; keep the tighter union for the UI's
// platform checks.
export type RuntimeInfo = Omit<S['RuntimeInfoDto'], 'os'> & {
  os: 'windows' | 'macos' | 'linux' | 'other';
};

export type FfprobeResult = S['FfprobeResultDto'];
export type FlagCounts = S['FlagCountsDto'];

// --- VideoSet --------------------------------------------------------------

// The server reuses the EF VideoSet entity for both request and response, so
// the generated schema has id/enabled/sortOrder optional and lacks the
// response-only computed `pathExists`. Responses always populate the former;
// tighten them here and add `pathExists`.
export type VideoSet = S['VideoSet'] & {
  id: string;
  enabled: boolean;
  sortOrder: number;
  pathExists?: boolean;
};

export type VideoSetInput = Omit<VideoSet, 'pathExists'>;

// --- Re-root ----------------------------------------------------------------

export type ReRootPreviewItem = S['ReRootPreviewItem'];
export type ReRootPreview = S['ReRootPreview'];

// --- Data validation -------------------------------------------------------

export type MissingVideoFile = S['MissingVideoFileDto'];
export type PurgeMissingFilesResult = S['PurgeMissingFilesResultDto'];

// Parent marked for deletion that still holds embedded (un-exported) clips (#174).
export type PurgeClipWarning = S['PurgeClipWarningDto'];

// Live state of an optimize-for-streaming (faststart) run (issue #166).
export type StreamingOptimizeProgress = S['StreamingOptimizeProgressDto'];

// Live state of a video-repair run (issue #165).
export type RepairProgress = S['RepairProgressDto'];

// Live state of a join (concatenate) run (issue #163).
export type JoinProgress = S['JoinProgressDto'];

// Live state of an encode/convert run (issue #164).
export type EncodeProgress = S['EncodeProgressDto'];
export type ExtraDiskFile = S['ExtraDiskFileDto'];
export type Md5Candidate = S['Md5CandidateDto'];
export type Md5CheckResult = S['Md5CheckResultDto'];
