import type {
  BackupInfo,
  BackupSettings,
  BulkCreateTagsRequest,
  BulkCreateTagsResponse,
  CreateClipRequest,
  CreatePropertyDefinitionRequest,
  CreateTagGroupRequest,
  CreateTagRequest,
  DirectoryImportRequest,
  ImportBrowseResponse,
  ImportedFolder,
  ImportScanProgress,
  MoveProgress,
  OcrResult,
  OcrTextLine,
  OcrScanProgress,
  FileMoveLog,
  ImportFailedFileRow,
  ImportFileListResponse,
  ImportJobSummary,
  ImportProgressResponse,
  ImportQueueFileRow,
  Md5DuplicateRow,
  MergeTagsRequest,
  WorkerFailedRow,
  WorkerQueueRow,
  LogEvent,
  PlaylistDto,
  PlaylistFilterRequest,
  FilteredVideosPage,
  BrowseSort,
  PlaylistNavigationDto,
  PropertyDefinition,
  SetPropertyValuesRequest,
  SetVideoTagsRequest,
  Tag,
  TagGroup,
  TagSearchHit,
  TagSuggestion,
  UpdatePropertyDefinitionRequest,
  UpdateTagGroupRequest,
  UpdateTagRequest,
  UpdateVideoRequest,
  Video,
  VideoSet,
  VideoSetInput,
  ReRootPreview,
  RuntimeInfo,
  FfprobeResult,
  FlagCounts,
  ClipSummary,
  ClipExportQueueItem,
  ClipExportProgress,
  KeyframeCut,
  BlockRemovalQueueItem,
  BlockRemovalProgress,
  MissingVideoFile,
  PurgeMissingFilesResult,
  ExtraDiskFile,
  Md5Candidate,
  SearchRequestOpts,
  SearchResponse,
  Md5CheckResult,
  DuplicateCandidate,
  DuplicateStatus
} from './types';

const BASE = '';

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly method: string, public readonly url: string, body?: string) {
    super(`${method} ${url} failed: ${status}${body ? ` — ${body}` : ''}`);
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const url = `${BASE}${path}`;
  const method = init.method ?? 'GET';
  const headers: Record<string, string> = {
    Accept: 'application/json',
    ...(init.headers as Record<string, string> | undefined)
  };
  if (init.body !== undefined && headers['Content-Type'] === undefined) {
    headers['Content-Type'] = 'application/json';
  }
  const res = await fetch(url, { ...init, headers });
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new ApiError(res.status, method, url, body);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (text.length === 0) return undefined as T;
  return JSON.parse(text) as T;
}

const enc = encodeURIComponent;

export const api = {
  // --- VideoSets -----------------------------------------------------------

  listVideoSets: () => request<VideoSet[]>('/api/video-sets'),
  createVideoSet: (input: VideoSetInput) =>
    request<VideoSet>('/api/video-sets', { method: 'POST', body: JSON.stringify(input) }),
  updateVideoSet: (id: string, input: VideoSetInput) =>
    request<VideoSet>(`/api/video-sets/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteVideoSet: (id: string, force = false) =>
    request<void>(`/api/video-sets/${id}${force ? '?force=true' : ''}`, { method: 'DELETE' }),
  getVideoSetOrphanCount: (id: string) =>
    request<{ count: number }>(`/api/video-sets/${id}/orphan-count`),
  // Re-root: spot-check a proposed new base path (no writes), then commit
  // the move (rewrites the source path + every child video's FilePath). (#32)
  reRootVideoSetPreview: (id: string, newPath: string) =>
    request<ReRootPreview>(`/api/video-sets/${id}/re-root/preview`, {
      method: 'POST',
      body: JSON.stringify({ newPath })
    }),
  reRootVideoSet: (id: string, newPath: string) =>
    request<{ reRooted: number; newPath: string }>(`/api/video-sets/${id}/re-root`, {
      method: 'POST',
      body: JSON.stringify({ newPath })
    }),

  // --- Workers -------------------------------------------------------------

  getThumbnailStatus: () =>
    request<{
      total: number; warmed: number; failed: number; pending: number;
      currentVideoId: string | null; currentFilePath: string | null;
      startedAt: string | null; nextScanAt: string | null; importDetectedAt: string | null;
    }>('/api/thumbnails/status'),
  getThumbnailQueue: () => request<WorkerQueueRow[]>('/api/thumbnails/queue'),
  getFailedThumbnails: () => request<WorkerFailedRow[]>('/api/thumbnails/failed'),
  skipCurrentThumbnail: () => request<void>('/api/thumbnails/skip', { method: 'POST' }),

  skipCurrentMd5: () => request<void>('/api/md5-backfill/skip', { method: 'POST' }),
  getMd5BackfillStatus: () =>
    request<{
      total: number; hashed: number; pending: number; failed: number;
      currentFileName: string | null; currentFilePath: string | null;
      bytesProcessed: number; totalBytes: number;
      nextScanAt: string | null; importDetectedAt: string | null;
    }>('/api/md5-backfill/status'),
  getMd5BackfillQueue: () => request<WorkerQueueRow[]>('/api/md5-backfill/queue'),
  getFailedMd5: () => request<WorkerFailedRow[]>('/api/md5-backfill/failed'),
  getMd5Duplicates: () => request<Md5DuplicateRow[]>('/api/md5-backfill/duplicates'),

  triggerThumbnailScan: () => request<void>('/api/thumbnails/scan-now', { method: 'POST' }),
  triggerMd5BackfillScan: () => request<void>('/api/md5-backfill/scan-now', { method: 'POST' }),

  pauseImports: () => request<void>('/api/import/pause', { method: 'POST' }),
  resumeImports: () => request<void>('/api/import/resume', { method: 'POST' }),
  pauseThumbnails: () => request<void>('/api/thumbnails/pause', { method: 'POST' }),
  resumeThumbnails: () => request<void>('/api/thumbnails/resume', { method: 'POST' }),
  pauseMd5: () => request<void>('/api/md5-backfill/pause', { method: 'POST' }),
  resumeMd5: () => request<void>('/api/md5-backfill/resume', { method: 'POST' }),

  getWorkerPauseStatus: () =>
    request<{ importPaused: boolean; thumbnailsPaused: boolean; md5Paused: boolean }>(
      '/api/worker-pause-status'
    ),

  clearFailedThumbnails: () =>
    request<{ cleared: number }>('/api/thumbnails/clear-failed', { method: 'POST' }),
  clearFailedMd5: () =>
    request<{ cleared: number }>('/api/md5-backfill/clear-failed', { method: 'POST' }),

  // --- Videos --------------------------------------------------------------

  getVideoCount: () => request<number>('/api/videos/count'),
  // Per-flag aggregate counts — drives the count badges on the
  // browse-page Flags tree.
  getFlagCounts: () => request<FlagCounts>('/api/videos/flag-counts'),
  getVideo: (id: string) => request<Video | null>(`/api/videos/${id}`),

  // POST the three-way filter (Required / Optional / Excluded). Empty filter
  // returns every enabled-set video. Returns the visible videos plus how many
  // matches the auto-hide (#84) suppressed (from the X-Hidden-Count header), so
  // the browse bar can show an "N hidden" status.
  filterVideos: async (
    filter: PlaylistFilterRequest,
    signal?: AbortSignal
  ): Promise<{ videos: Video[]; hiddenCount: number }> => {
    const url = `${BASE}/api/videos/filter`;
    const res = await fetch(url, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify(filter),
      signal
    });
    if (!res.ok) {
      const body = await res.text().catch(() => '');
      throw new ApiError(res.status, 'POST', url, body);
    }
    const videos = res.status === 204 ? [] : ((await res.json()) as Video[]);
    const hiddenCount = Number(res.headers.get('X-Hidden-Count') ?? '0') || 0;
    return { videos, hiddenCount };
  },

  // Keyset-paginated filter (#127). Returns one page + an opaque cursor for the
  // next (null when last). `seed` only matters for shuffle. Pass `signal` so a
  // superseded filter change can abort the in-flight page.
  filterVideosPage: (
    filter: PlaylistFilterRequest,
    opts: { sort: BrowseSort; dir?: 'asc' | 'desc'; limit?: number; cursor?: string | null; seed?: string },
    signal?: AbortSignal
  ): Promise<FilteredVideosPage> => {
    const qs = new URLSearchParams({ sort: opts.sort });
    if (opts.dir) qs.set('dir', opts.dir);
    if (opts.limit !== undefined) qs.set('limit', String(opts.limit));
    if (opts.cursor) qs.set('cursor', opts.cursor);
    if (opts.seed) qs.set('seed', opts.seed);
    return request<FilteredVideosPage>(`/api/videos/filter-page?${qs.toString()}`, {
      method: 'POST',
      body: JSON.stringify(filter),
      signal
    });
  },

  // Simple AND-of-tags filter. For richer filtering use filterVideos.
  listVideosByTags: (params: { tagIds?: string[] }) => {
    const qs = new URLSearchParams();
    for (const id of params.tagIds ?? []) qs.append('tagId', id);
    const s = qs.toString();
    return request<Video[]>(`/api/videos${s ? `?${s}` : ''}`);
  },

  getRelatedVideos: (id: string, take = 12) =>
    request<Video[]>(`/api/videos/${id}/related?take=${take}`),

  updateVideo: (id: string, body: UpdateVideoRequest) =>
    request<void>(`/api/videos/${id}`, {
      method: 'PUT',
      body: JSON.stringify(body)
    }),

  setVideoTags: (id: string, body: SetVideoTagsRequest) =>
    request<void>(`/api/videos/${id}/tags`, {
      method: 'PUT',
      body: JSON.stringify(body)
    }),

  // OCR the on-screen text at time t (seconds) of the video (issue #5).
  ocrVideoFrame: (id: string, t: number) =>
    request<OcrResult>(`/api/videos/${id}/ocr?t=${encodeURIComponent(String(t))}`),

  // Full-video OCR text scan (issue #5, ask 2). Start/resume runs a background
  // scan; poll progress; stop is cooperative (keeps the resume marker).
  startOcrScan: (id: string) =>
    request<OcrScanProgress>(`/api/videos/${id}/ocr-scan`, { method: 'POST' }),
  getOcrScanProgress: (id: string) =>
    request<OcrScanProgress>(`/api/videos/${id}/ocr-scan`),
  stopOcrScan: (id: string) =>
    request<OcrScanProgress>(`/api/videos/${id}/ocr-scan/stop`, { method: 'POST' }),
  // A video's stored OCR hits in playhead order (optionally filtered by q).
  getVideoOcrText: (id: string, q?: string) =>
    request<OcrTextLine[]>(
      `/api/videos/${id}/ocr-text${q ? `?q=${encodeURIComponent(q)}` : ''}`),

  // Tags whose name/alias appears in this video's file name or folder path,
  // excluding ones already applied (issue #10).
  getTagSuggestions: (id: string) =>
    request<TagSuggestion[]>(`/api/videos/${id}/tag-suggestions`),

  // Candidate *new* tag names parsed from the file name + path — the Tag
  // panel's Ctrl+Shift+T "potential tags" (issue #10).
  getTagCandidates: (id: string) =>
    request<string[]>(`/api/videos/${id}/tag-candidates`),

  setVideoProperties: (id: string, body: SetPropertyValuesRequest) =>
    request<void>(`/api/videos/${id}/properties`, {
      method: 'PUT',
      body: JSON.stringify(body)
    }),

  // Clip flag (#167) — user-settable so imported standalone clips can be marked.
  markClip: (id: string) =>
    request<void>(`/api/videos/${id}/mark-clip`, { method: 'POST' }),
  unmarkClip: (id: string) =>
    request<void>(`/api/videos/${id}/unmark-clip`, { method: 'POST' }),

  markForDeletion: (id: string) =>
    request<Video>(`/api/videos/${id}/mark-for-deletion`, { method: 'POST' }),
  unmarkForDeletion: (id: string) =>
    request<Video>(`/api/videos/${id}/unmark-for-deletion`, { method: 'POST' }),
  markPlaybackIssue: (id: string) =>
    request<Video>(`/api/videos/${id}/mark-playback-issue`, { method: 'POST' }),
  unmarkPlaybackIssue: (id: string) =>
    request<Video>(`/api/videos/${id}/unmark-playback-issue`, { method: 'POST' }),
  markReviewed: (id: string) =>
    request<void>(`/api/videos/${id}/mark-reviewed`, { method: 'POST' }),
  unmarkReviewed: (id: string) =>
    request<void>(`/api/videos/${id}/unmark-reviewed`, { method: 'POST' }),
  markFavorite: (id: string) =>
    request<void>(`/api/videos/${id}/mark-favorite`, { method: 'POST' }),
  unmarkFavorite: (id: string) =>
    request<void>(`/api/videos/${id}/unmark-favorite`, { method: 'POST' }),

  createClip: (parentId: string, req: CreateClipRequest) =>
    request<string>(`/api/videos/${parentId}/clips`, {
      method: 'POST',
      body: JSON.stringify(req)
    }),

  // Minimal list of clips of a parent video. Used by VideoPlayer to
  // paint green-tinted bands on the scrubber so the viewer can see
  // at a glance which slices have been clipped out. Returns [] for
  // a video that itself is a clip (it has no children).
  listClipsOfVideo: (parentId: string) =>
    request<ClipSummary[]>(`/api/videos/${parentId}/clips`),

  // --- Clip export (issue #69) -------------------------------------------
  // Parents that still have un-exported clips, with their clips.
  getClipExportQueue: () =>
    request<ClipExportQueueItem[]>(`/api/clips-export/queue`),
  // The keyframe-snapped start the stream-copy export will actually use.
  getClipKeyframeCut: (clipId: string) =>
    request<KeyframeCut>(`/api/videos/${clipId}/keyframe-cut`),
  // Start / poll / stop a background export run. Each item carries an optional
  // output name (#173); blank uses the default "<parent>_clip".
  startClipExport: (clips: { clipId: string; name?: string }[]) =>
    request<ClipExportProgress>(`/api/clips-export`, {
      method: 'POST',
      body: JSON.stringify({ clips })
    }),
  getClipExportProgress: () =>
    request<ClipExportProgress>(`/api/clips-export`),
  stopClipExport: () =>
    request<ClipExportProgress>(`/api/clips-export/stop`, { method: 'POST' }),

  // --- Remove blocked sections (issue #70) -------------------------------
  getBlockRemovalQueue: () =>
    request<BlockRemovalQueueItem[]>(`/api/remove-blocks/queue`),
  startBlockRemoval: (videoIds: string[]) =>
    request<BlockRemovalProgress>(`/api/remove-blocks`, {
      method: 'POST',
      body: JSON.stringify({ videoIds })
    }),
  getBlockRemovalProgress: () =>
    request<BlockRemovalProgress>(`/api/remove-blocks`),
  stopBlockRemoval: () =>
    request<BlockRemovalProgress>(`/api/remove-blocks/stop`, { method: 'POST' }),

  // --- Data validation ---------------------------------------------------
  // Video rows whose FilePath no longer resolves on disk. By default
  // hides files under disabled sources — pass includeDisabled=true
  // to include them too.
  getMissingFiles: (includeDisabled = false) =>
    request<MissingVideoFile[]>(
      `/api/validation/missing-files?includeDisabled=${includeDisabled ? 'true' : 'false'}`
    ),
  // DB-only removal of rows the missing-files scan surfaced. The
  // server re-probes File.Exists per row and refuses to delete rows
  // whose file has reappeared — see PurgeMissingFilesResult.
  purgeMissingFiles: (videoIds: string[]) =>
    request<PurgeMissingFilesResult>('/api/validation/missing-files/purge', {
      method: 'POST',
      body: JSON.stringify({ videoIds })
    }),
  // Video files on disk under a configured source that have no
  // matching Video row. sourceId optional — omit to scan every
  // enabled source (or every source if includeDisabled=true).
  getExtraFiles: (sourceId?: string, includeDisabled = false) => {
    const qs = new URLSearchParams();
    if (sourceId) qs.set('sourceId', sourceId);
    qs.set('includeDisabled', includeDisabled ? 'true' : 'false');
    return request<ExtraDiskFile[]>(`/api/validation/extra-files?${qs.toString()}`);
  },
  // Videos eligible for MD5 re-validation. Client iterates this
  // list and POSTs each id to validateMd5() so progress / cancel
  // happens in the browser instead of holding a long-running
  // request open.
  getMd5Candidates: (includeDisabled = false) =>
    request<Md5Candidate[]>(
      `/api/validation/md5-candidates?includeDisabled=${includeDisabled ? 'true' : 'false'}`
    ),
  validateMd5: (videoId: string) =>
    request<Md5CheckResult>(`/api/validation/md5-check/${videoId}`, { method: 'POST' }),

  deleteVideo: (id: string) =>
    request<void>(`/api/videos/${id}`, { method: 'DELETE' }),

  // --- File move (issue #4) ----------------------------------------------
  // Move the video's file into targetDirectory (an absolute folder under
  // some enabled source). Returns the refreshed Video. Poll moveProgress
  // during the call for a live bar on big cross-drive moves.
  moveVideo: (id: string, targetDirectory: string) =>
    request<Video>(`/api/videos/${id}/move`, {
      method: 'POST',
      body: JSON.stringify({ targetDirectory })
    }),
  getMoveProgress: (id: string) =>
    request<MoveProgress>(`/api/videos/${id}/move-progress`),
  listFileMoves: (limit = 50) =>
    request<FileMoveLog[]>(`/api/file-moves?limit=${limit}`),
  revertFileMove: (moveId: string) =>
    request<Video>(`/api/file-moves/${moveId}/revert`, { method: 'POST' }),

  getMarkedForDeletion: () => request<Video[]>('/api/videos/marked-for-deletion'),
  purgeVideo: (id: string) =>
    request<void>(`/api/videos/${id}/purge`, { method: 'POST' }),
  purgeAllMarkedForDeletion: () =>
    request<{ purged: number; failed: Array<{ id: string; fileName: string; error: string }> }>(
      '/api/videos/purge-all',
      { method: 'POST' }
    ),

  // Triage list — videos the user has flagged with PlaybackIssue.
  // Powers the /playback-issues page (parallel to /purge).
  getPlaybackIssues: () => request<Video[]>('/api/videos/playback-issues'),
  // Bulk-purge every PlaybackIssue row in one shot. Same response
  // shape as purgeAllMarkedForDeletion so the page's bulk-progress
  // modal can consume both interchangeably.
  purgeAllPlaybackIssues: () =>
    request<{ purged: number; failed: Array<{ id: string; fileName: string; error: string }> }>(
      '/api/videos/purge-all-playback-issues',
      { method: 'POST' }
    ),

  // Diagnostic affordances — both gated server-side on a loopback
  // request, so calling them from a remote browser returns 403.
  // Frontend reads /api/runtime-info and hides the buttons when not
  // local so the failure isn't surprising.
  revealVideo: (id: string) =>
    request<void>(`/api/videos/${id}/reveal`, { method: 'POST' }),
  // Opens a terminal at the video's parent directory. Same loopback +
  // VideoSet path-prefix guards as revealVideo — the API hides which
  // emulator it actually used (it tries a fallback chain), but on
  // success the call returns 204 and a window pops up on the host.
  openTerminalAtVideo: (id: string) =>
    request<void>(`/api/videos/${id}/open-terminal`, { method: 'POST' }),
  ffprobeVideo: (id: string) =>
    request<FfprobeResult>(`/api/videos/${id}/ffprobe`),

  streamUrl: (id: string) => `${BASE}/api/videos/${id}/stream`,
  thumbnailsVttUrl: (id: string) => `${BASE}/api/videos/${id}/thumbnails.vtt`,
  spriteUrl: (id: string) => `${BASE}/api/videos/${id}/sprite.jpg`,
  posterUrl: (id: string) => `${BASE}/api/videos/${id}/poster.jpg`,

  getVideosByFolder: (path: string, recursive = false) =>
    request<Video[]>(`/api/videos/by-folder?path=${enc(path)}${recursive ? '&recursive=true' : ''}`),

  markWatched: (id: string) =>
    request<void>(`/api/videos/${id}/watched`, { method: 'POST' }),

  // --- Tag groups ----------------------------------------------------------

  listTagGroups: () => request<TagGroup[]>('/api/tag-groups'),
  getTagGroup: (id: string) => request<TagGroup>(`/api/tag-groups/${id}`),
  createTagGroup: (req: CreateTagGroupRequest) =>
    request<TagGroup>('/api/tag-groups', { method: 'POST', body: JSON.stringify(req) }),
  updateTagGroup: (id: string, req: UpdateTagGroupRequest) =>
    request<void>(`/api/tag-groups/${id}`, { method: 'PUT', body: JSON.stringify(req) }),
  deleteTagGroup: (id: string) =>
    request<void>(`/api/tag-groups/${id}`, { method: 'DELETE' }),

  // --- Tags ----------------------------------------------------------------

  listTags: (params?: { groupId?: string; withCounts?: boolean; q?: string }) => {
    const qs = new URLSearchParams();
    if (params?.groupId) qs.set('groupId', params.groupId);
    if (params?.withCounts) qs.set('withCounts', 'true');
    if (params?.q) qs.set('q', params.q);
    const s = qs.toString();
    return request<Tag[]>(`/api/tags${s ? `?${s}` : ''}`);
  },
  getTag: (id: string) => request<Tag>(`/api/tags/${id}`),
  createTag: (req: CreateTagRequest) =>
    request<Tag>('/api/tags', { method: 'POST', body: JSON.stringify(req) }),
  // Create many tags in one request (issue #49) — used by the Tag
  // Management paste box instead of one POST per name.
  bulkCreateTags: (req: BulkCreateTagsRequest) =>
    request<BulkCreateTagsResponse>('/api/tags/bulk', {
      method: 'POST',
      body: JSON.stringify(req)
    }),
  updateTag: (id: string, req: UpdateTagRequest) =>
    request<void>(`/api/tags/${id}`, { method: 'PUT', body: JSON.stringify(req) }),
  // Toggle just the "hidden by default" flag (issue #84).
  setTagHiddenByDefault: (id: string, hidden: boolean) =>
    request<void>(`/api/tags/${id}/hidden-by-default`, {
      method: 'PUT',
      body: JSON.stringify({ hidden })
    }),
  deleteTag: (id: string) =>
    request<void>(`/api/tags/${id}`, { method: 'DELETE' }),
  mergeTags: (req: MergeTagsRequest) =>
    request<{ mergedVideos: number; removedSources: number }>('/api/tags/merge', {
      method: 'POST',
      body: JSON.stringify(req)
    }),
  searchTags: async (q: string): Promise<TagSearchHit[]> => {
    if (!q.trim()) return [];
    return request<TagSearchHit[]>(`/api/tags/search?q=${enc(q.trim())}`);
  },

  // Global search — powers the Ctrl+K command palette. Returns a
  // discriminated SearchResponse so the caller can pattern-match on
  // result.kind. Empty / whitespace queries short-circuit to an empty
  // response without a network round-trip.
  search: async (opts: SearchRequestOpts, signal?: AbortSignal): Promise<SearchResponse> => {
    const q = opts.q.trim();
    if (!q) {
      return { query: '', totalCount: 0, truncated: false, results: [] };
    }
    const qs = new URLSearchParams();
    qs.set('q', q);
    if (opts.limit !== undefined) qs.set('limit', String(opts.limit));
    if (opts.offset !== undefined) qs.set('offset', String(opts.offset));
    if (opts.kinds !== undefined) qs.set('kinds', opts.kinds);
    return request<SearchResponse>(`/api/search?${qs.toString()}`, { signal });
  },
  setTagProperties: (id: string, body: SetPropertyValuesRequest) =>
    request<void>(`/api/tags/${id}/properties`, {
      method: 'PUT',
      body: JSON.stringify(body)
    }),

  // --- Property definitions ------------------------------------------------

  listProperties: (tagGroupId?: string) => {
    const qs = tagGroupId ? `?tagGroupId=${tagGroupId}` : '';
    return request<PropertyDefinition[]>(`/api/properties${qs}`);
  },
  createProperty: (req: CreatePropertyDefinitionRequest) =>
    request<PropertyDefinition>('/api/properties', { method: 'POST', body: JSON.stringify(req) }),
  updateProperty: (id: string, req: UpdatePropertyDefinitionRequest) =>
    request<void>(`/api/properties/${id}`, { method: 'PUT', body: JSON.stringify(req) }),
  deleteProperty: (id: string) =>
    request<void>(`/api/properties/${id}`, { method: 'DELETE' }),

  // --- Duplicates ------------------------------------------------------------

  // Flag a pair as possible duplicates. Idempotent — re-flagging an
  // existing pair (either order) returns the existing candidate.
  createDuplicate: (videoAId: string, videoBId: string) =>
    request<DuplicateCandidate>('/api/duplicates', {
      method: 'POST',
      body: JSON.stringify({ videoAId, videoBId })
    }),
  listDuplicates: (status?: DuplicateStatus | 'all') =>
    request<DuplicateCandidate[]>(`/api/duplicates${status ? `?status=${status}` : ''}`),
  confirmDuplicate: (id: string) =>
    request<DuplicateCandidate>(`/api/duplicates/${id}/confirm`, { method: 'POST' }),
  rejectDuplicate: (id: string) =>
    request<DuplicateCandidate>(`/api/duplicates/${id}/reject`, { method: 'POST' }),
  reopenDuplicate: (id: string) =>
    request<DuplicateCandidate>(`/api/duplicates/${id}/reopen`, { method: 'POST' }),
  deleteDuplicate: (id: string) =>
    request<void>(`/api/duplicates/${id}`, { method: 'DELETE' }),

  // --- Playlists -----------------------------------------------------------

  createRandomPlaylist: (filter?: PlaylistFilterRequest) =>
    request<PlaylistDto>('/api/playlists/random', {
      method: 'POST',
      body: filter ? JSON.stringify(filter) : undefined
    }),
  createEvenPlaylist: (filter?: PlaylistFilterRequest) =>
    request<PlaylistDto>('/api/playlists/even', {
      method: 'POST',
      body: filter ? JSON.stringify(filter) : undefined
    }),
  getPlaylistNavigation: (playlistId: string, videoId: string) =>
    request<PlaylistNavigationDto | null>(`/api/playlists/${playlistId}/navigation/${videoId}`),

  // --- Logs / Imports ------------------------------------------------------

  // Windowed snapshot of the in-memory log buffer. Defaults to the
  // last 5 minutes capped at 1000 entries — matches the server-side
  // defaults so omitting the opts is safe. Older / wider queries
  // should go through Seq.
  getLogs: (opts?: { sinceMinutes?: number; take?: number }) => {
    const qs = new URLSearchParams();
    if (opts?.sinceMinutes !== undefined) qs.set('sinceMinutes', String(opts.sinceMinutes));
    if (opts?.take !== undefined) qs.set('take', String(opts.take));
    const s = qs.toString();
    return request<LogEvent[]>(`/api/logs${s ? `?${s}` : ''}`);
  },
  getRuntimeInfo: () => request<RuntimeInfo>('/api/runtime-info'),

  // --- Backups (issue #32) -------------------------------------------------
  createBackupSnapshot: () =>
    request<BackupInfo>('/api/backup/snapshot', { method: 'POST' }),
  listBackups: () => request<BackupInfo[]>('/api/backup'),
  getBackupSettings: () => request<BackupSettings>('/api/backup/settings'),
  setBackupDirectory: (directory: string) =>
    request<BackupSettings>('/api/backup/settings', {
      method: 'PUT',
      body: JSON.stringify({ directory })
    }),
  deleteBackup: (fileName: string) =>
    request<void>(`/api/backup/${encodeURIComponent(fileName)}`, { method: 'DELETE' }),
  // Replace the whole DB from a JSON snapshot. The server takes a pre-restore
  // safety snapshot first and returns its name. (#32)
  restoreBackup: (fileName: string) =>
    request<{
      restoredFrom: string;
      safetySnapshot: string;
      videos: number;
      tags: number;
      videoSets: number;
    }>(`/api/backup/${encodeURIComponent(fileName)}/restore`, { method: 'POST' }),
  backupDownloadUrl: (fileName: string) =>
    `/api/backup/${encodeURIComponent(fileName)}/download`,

  // refresh=true bypasses the server's per-folder scan cache and re-walks
  // the filesystem — backs the Sources refresh button. (issue #4)
  browseImport: (path?: string | null, refresh = false) => {
    const params = new URLSearchParams();
    if (path && path.trim().length > 0) params.set('path', path);
    if (refresh) params.set('refresh', 'true');
    const qs = params.toString();
    return request<ImportBrowseResponse>(`/api/import/browse${qs ? `?${qs}` : ''}`);
  },
  // Flat list of folders that already hold imported videos — the move
  // dialog's destination choices. (issue #4)
  listImportedFolders: () =>
    request<ImportedFolder[]>('/api/import/imported-folders'),

  // Remove every imported video under a folder from the library (issue #53).
  // Files on disk are left alone; returns how many videos were removed.
  removeLibraryFolder: (path: string) =>
    request<{ removed: number }>('/api/library/remove-folder', {
      method: 'POST',
      body: JSON.stringify({ path })
    }),
  // Live count of video files discovered by an in-flight browse scan.
  // Polled by the Import page while a source loads. (issue #27)
  getImportScanProgress: () =>
    request<ImportScanProgress>('/api/import/scan-progress'),
  getImportFiles: (directoryPath: string, includeSubdirectories: boolean = true) =>
    request<ImportFileListResponse>(
      `/api/import/files?directoryPath=${enc(directoryPath)}&includeSubdirectories=${includeSubdirectories}`
    ),
  startImport: async (body: DirectoryImportRequest): Promise<string> => {
    const payload = await request<{ jobId: string }>('/api/import/directory', {
      method: 'POST',
      body: JSON.stringify(body)
    });
    if (!payload?.jobId) throw new Error('Import start response missing jobId.');
    return payload.jobId;
  },
  getImportProgress: (jobId: string) =>
    request<ImportProgressResponse>(`/api/import/progress/${jobId}`),
  listImportJobs: () => request<ImportJobSummary[]>('/api/import/jobs'),
  clearCompletedImportJobs: () =>
    request<{ removed: number }>('/api/import/jobs/completed', { method: 'DELETE' }),
  getFailedImportFiles: () => request<ImportFailedFileRow[]>('/api/import/failed-files'),
  getImportQueue: () => request<ImportQueueFileRow[]>('/api/import/queue'),
  importThumbnailUrl: (dockerPath: string) =>
    `${BASE}/api/import/thumbnail?path=${enc(dockerPath)}`
};

export type Api = typeof api;
