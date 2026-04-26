import type {
  CreateClipRequest,
  CreatePropertyDefinitionRequest,
  CreateTagGroupRequest,
  CreateTagRequest,
  DirectoryImportRequest,
  ImportBrowseResponse,
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
  PlaylistNavigationDto,
  PropertyDefinition,
  SetPropertyValuesRequest,
  SetVideoTagsRequest,
  Tag,
  TagGroup,
  TagSearchHit,
  UpdatePropertyDefinitionRequest,
  UpdateTagGroupRequest,
  UpdateTagRequest,
  UpdateVideoRequest,
  Video,
  VideoSet,
  VideoSetInput
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
  getVideo: (id: string) => request<Video | null>(`/api/videos/${id}`),

  // POST the three-way filter (Required / Optional / Excluded). Empty filter
  // returns every enabled-set video.
  filterVideos: (filter: PlaylistFilterRequest) =>
    request<Video[]>('/api/videos/filter', {
      method: 'POST',
      body: JSON.stringify(filter)
    }),

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

  setVideoProperties: (id: string, body: SetPropertyValuesRequest) =>
    request<void>(`/api/videos/${id}/properties`, {
      method: 'PUT',
      body: JSON.stringify(body)
    }),

  markForDeletion: (id: string) =>
    request<Video>(`/api/videos/${id}/mark-for-deletion`, { method: 'POST' }),
  unmarkForDeletion: (id: string) =>
    request<Video>(`/api/videos/${id}/unmark-for-deletion`, { method: 'POST' }),
  markWontPlay: (id: string) =>
    request<Video>(`/api/videos/${id}/mark-wont-play`, { method: 'POST' }),
  unmarkWontPlay: (id: string) =>
    request<Video>(`/api/videos/${id}/unmark-wont-play`, { method: 'POST' }),
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

  deleteVideo: (id: string) =>
    request<void>(`/api/videos/${id}`, { method: 'DELETE' }),

  getMarkedForDeletion: () => request<Video[]>('/api/videos/marked-for-deletion'),
  purgeVideo: (id: string) =>
    request<void>(`/api/videos/${id}/purge`, { method: 'POST' }),
  purgeAllMarkedForDeletion: () =>
    request<{ purged: number; failed: Array<{ id: string; fileName: string; error: string }> }>(
      '/api/videos/purge-all',
      { method: 'POST' }
    ),

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
  updateTag: (id: string, req: UpdateTagRequest) =>
    request<void>(`/api/tags/${id}`, { method: 'PUT', body: JSON.stringify(req) }),
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

  getLogs: () => request<LogEvent[]>('/api/logs'),

  browseImport: (path?: string | null) => {
    const url = path && path.trim().length > 0
      ? `/api/import/browse?path=${enc(path)}`
      : '/api/import/browse';
    return request<ImportBrowseResponse>(url);
  },
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
