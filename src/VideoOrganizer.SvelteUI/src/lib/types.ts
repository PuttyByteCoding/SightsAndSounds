// Mirrors VideoOrganizer.Shared.Dto over the wire.
// The API's LenientEnumConverterFactory.ToWireCamelCase produces these string values
// from the PascalCase enum names. See src/VideoOrganizer.API/LenientEnumConverter.cs.

// --- Enums -----------------------------------------------------------------

export type CameraTypes =
  | 'unknown'
  | 'cellPhone'
  | 'hiddenCamera'
  | 'camcorder'
  | 'professionalCamera'
  | 'notChecked';

export type VideoQuality =
  | 'singleCamera'
  | 'multipleCameras'
  | 'lowQuality'
  | 'notChecked';

export type VideoDimensionFormat =
  | 'uhd8k'
  | 'uhd4K'
  | 'hd1080p'
  | 'hd720p'
  | 'sd576p4x3'
  | 'sd576p16x9'
  | 'sd480p4x3'
  | 'sd480p16x9'
  | 'verticalUHD4k'
  | 'vertical1080p'
  | 'vertical720p'
  | 'nonStandard';

export type VideoCodec = 'h264' | 'h265' | 'hevc' | 'notChecked' | 'other';

export type VideoBlockTypes = 'clip' | 'hide' | 'other';

export type ImportFileStatus = 'pending' | 'importing' | 'completed' | 'failed' | 'skipped';

export type PropertyDataType = 'text' | 'longText' | 'number' | 'date' | 'boolean' | 'url';
export type PropertyScope = 'tag' | 'video';

// --- Tag groups + tags + properties ----------------------------------------

export interface TagGroup {
  id: string;
  name: string;
  allowMultiple: boolean;
  displayAsCheckboxes: boolean;
  sortOrder: number;
  notes: string;
  tagCount: number;
  // Number of videos with no tag from this group. Used for the
  // "Missing / None" badge under each group in the browse sidebar.
  videosMissingCount: number;
}

export interface Tag {
  id: string;
  tagGroupId: string;
  tagGroupName: string;
  name: string;
  aliases: string[];
  isFavorite: boolean;
  sortOrder: number;
  notes: string;
  videoCount: number;
}

export interface CreateTagGroupRequest {
  name: string;
  allowMultiple?: boolean;
  displayAsCheckboxes?: boolean;
  sortOrder?: number;
  notes?: string;
}

export interface UpdateTagGroupRequest {
  name: string;
  allowMultiple: boolean;
  displayAsCheckboxes: boolean;
  sortOrder: number;
  notes: string;
}

export interface CreateTagRequest {
  tagGroupId: string;
  name: string;
  aliases?: string[];
  isFavorite?: boolean;
  sortOrder?: number;
  notes?: string;
}

export interface UpdateTagRequest {
  name: string;
  aliases: string[];
  isFavorite: boolean;
  sortOrder: number;
  notes: string;
}

export interface MergeTagsRequest {
  sourceIds: string[];
  targetId: string;
}

export interface TagSearchHit {
  tagId: string;
  tagGroupId: string;
  tagGroupName: string;
  name: string;
  aliases: string[];
}

export interface PropertyDefinition {
  id: string;
  name: string;
  dataType: PropertyDataType;
  scope: PropertyScope;
  tagGroupId: string | null;
  required: boolean;
  sortOrder: number;
  notes: string;
}

export interface CreatePropertyDefinitionRequest {
  name: string;
  dataType: PropertyDataType;
  scope: PropertyScope;
  tagGroupId: string | null;
  required?: boolean;
  sortOrder?: number;
  notes?: string;
}

export interface UpdatePropertyDefinitionRequest {
  name: string;
  dataType: PropertyDataType;
  required: boolean;
  sortOrder: number;
  notes: string;
}

export interface PropertyValue {
  propertyDefinitionId: string;
  propertyName: string;
  dataType: PropertyDataType;
  value: string;
}

export interface PropertyValueWrite {
  propertyDefinitionId: string;
  value: string;
}

export interface SetPropertyValuesRequest {
  values: PropertyValueWrite[];
}

// --- Video DTOs ------------------------------------------------------------

export interface ChapterMarker {
  offset: number;
  comment: string | null;
}

export interface VideoBlock {
  offsetInSeconds: number;
  lengthInSeconds: number;
  videoBlockType: VideoBlockTypes;
}

// TimeSpan is serialized as "hh:mm:ss" / "d.hh:mm:ss" by System.Text.Json.
export type TimeSpanString = string;

// Slim per-tag projection embedded in Video.tags.
export interface VideoTagSummary {
  id: string;
  tagGroupId: string;
  tagGroupName: string;
  name: string;
}

export interface Video {
  id: string;
  fileName: string;
  filePath: string;
  md5: string | null;
  md5Failed: boolean;
  md5FailedError: string | null;
  thumbnailsFailed: boolean;
  thumbnailsFailedError: string | null;
  thumbnailsGenerated: boolean;
  importJobId: string | null;
  fileSize: number;
  duration: TimeSpanString;
  height: number;
  width: number;
  videoDimensionFormat: VideoDimensionFormat;
  videoCodec: VideoCodec;
  bitrate: number;
  frameRate: number;
  pixelFormat: string | null;
  ratio: string | null;
  creationTime: string | null;
  videoStreamCount: number;
  audioStreamCount: number;
  ingestDate: string;
  cameraType: CameraTypes;
  videoQuality: VideoQuality;
  watchCount: number;
  notes: string;
  needsReview: boolean;
  wontPlay: boolean;
  markedForDeletion: boolean;
  isFavorite: boolean;
  parentVideoId: string | null;
  clipStartSeconds: number | null;
  clipEndSeconds: number | null;
  isClip: boolean;
  chapterMarkers: ChapterMarker[];
  videoBlocks: VideoBlock[];
  tags: VideoTagSummary[];
  properties: PropertyValue[];
}

// PUT body for /api/videos/{id}.
export interface UpdateVideoRequest {
  fileName: string;
  ingestDate: string;
  cameraType: CameraTypes;
  videoQuality: VideoQuality;
  watchCount: number;
  notes: string;
  needsReview: boolean;
  isFavorite: boolean;
  clipStartSeconds: number | null;
  clipEndSeconds: number | null;
  chapterMarkers?: ChapterMarker[];
  videoBlocks?: VideoBlock[];
  tagIds?: string[];
  properties?: PropertyValueWrite[];
}

export interface SetVideoTagsRequest {
  tagIds: string[];
}

// POST body for /api/videos/{parentId}/clips. Name is optional — the API
// auto-generates "{parent filename} [start-end]" when omitted.
export interface CreateClipRequest {
  startSeconds: number;
  endSeconds: number;
  name?: string;
}

// --- Filtering -------------------------------------------------------------

// Wire enum from VideoOrganizer.Shared.Dto.FilterRefType.
export type FilterRefType = 'tag' | 'folder' | 'missing' | 'status';

// Opaque filter reference. `value` is:
//   tag       -> Tag.id
//   folder    -> absolute folder path
//   missing   -> "tagGroup:<groupId>"
//   status    -> "needsReview" | "wontPlay" | "markedForDeletion"
export interface FilterRef {
  type: FilterRefType;
  value: string;
}

// UI-side: carries a display label so chips can render without re-resolving.
export interface FilterTag extends FilterRef {
  label: string;
  // Optional display hint — what kind of filter slot this came from. Only
  // populated for tag-type refs that came from a TagSearchHit.
  tagGroupName?: string;
}

// POST body for /api/videos/filter and /api/playlists/random.
export interface PlaylistFilterRequest {
  required: FilterRef[];
  optional: FilterRef[];
  excluded: FilterRef[];
}

// --- Logs / workers / imports ---------------------------------------------

export interface LogEvent {
  timestamp: string;
  level: string;
  category: string;
  message: string;
  exception: string | null;
}

export interface PlaylistDto {
  id: string;
  videoIds: string[];
  createdAt: string;
}

export interface PlaylistNavigationDto {
  currentVideoId: string;
  nextVideoId: string | null;
  previousVideoId: string | null;
  currentIndex: number;
  totalCount: number;
}

export interface DirectoryImportRequest {
  directoryPath: string;
  includeSubdirectories: boolean;
  name?: string | null;
  notes?: string | null;
  initialTagIds?: string[] | null;
}

export interface ImportBrowseDirectory {
  name: string;
  fullPath: string;
  hasSubdirectories: boolean;
  videoCount: number;
  importedCount: number;
}

export interface ImportBrowseResponse {
  currentPath: string;
  parentPath: string | null;
  directories: ImportBrowseDirectory[];
}

export interface ImportFileListResponse {
  directoryPath: string;
  files: string[];
  nonImportableFiles: string[];
  importedFiles: string[];
}

export interface ImportFileProgressDto {
  filePath: string;
  fileSizeBytes: number;
  md5BytesProcessed: number;
  md5TotalBytes: number;
  status: ImportFileStatus;
  error: string | null;
}

export interface WorkerFailedRow {
  videoId: string;
  fileName: string;
  filePath: string;
  fileSizeBytes: number;
  error: string | null;
}

export interface WorkerQueueRow {
  videoId: string | null;
  fileName: string;
  filePath: string;
  fileSizeBytes: number;
}

export interface Md5DuplicateRow {
  videoId: string;
  fileName: string;
  filePath: string;
  fileSizeBytes: number;
  md5: string;
  groupSize: number;
}

export interface ImportFailedFileRow {
  jobId: string;
  jobDirectoryPath: string;
  filePath: string;
  fileName: string;
  fileSizeBytes: number;
  error: string | null;
}

export interface ImportQueueFileRow {
  jobId: string;
  jobDirectoryPath: string;
  filePath: string;
  fileName: string;
  fileSizeBytes: number;
  status: ImportFileStatus;
}

export interface ImportProgressResponse {
  messages: string[];
  isCompleted: boolean;
  error: string | null;
  fileStatuses: ImportFileProgressDto[];
}

export interface ImportTaskProgress {
  total: number;
  done: number;
  pending: number;
  failed: number;
}

export interface ImportJobSummary {
  jobId: string;
  name: string;
  directoryPath: string;
  enqueuedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  isCompleted: boolean;
  error: string | null;
  totalFiles: number;
  completedCount: number;
  failedCount: number;
  skippedCount: number;
  importingCount: number;
  currentFilePath: string | null;
  thumbnails: ImportTaskProgress;
  md5: ImportTaskProgress;
}

// --- VideoSet --------------------------------------------------------------

export interface VideoSet {
  id: string;
  name: string;
  path: string;
  enabled: boolean;
  sortOrder: number;
  pathExists?: boolean;
}

export type VideoSetInput = Omit<VideoSet, 'pathExists'>;
