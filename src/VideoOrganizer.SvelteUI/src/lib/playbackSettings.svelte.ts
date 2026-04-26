import { browser } from '$app/environment';

const STORAGE_KEY = 'videoOrganizer.playbackSettings';

export interface PlaybackSkipSettings {
  key1Seconds: number;
  key3Seconds: number;
  key4Seconds: number;
  key6Seconds: number;
  key7Seconds: number;
  key9Seconds: number;
}

export function defaultSettings(): PlaybackSkipSettings {
  return {
    key1Seconds: 2,
    key3Seconds: 2,
    key4Seconds: 30,
    key6Seconds: 30,
    key7Seconds: 240,
    key9Seconds: 240
  };
}

function readFromStorage(): PlaybackSkipSettings {
  if (!browser) return defaultSettings();
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return defaultSettings();
    const parsed = JSON.parse(raw) as Partial<PlaybackSkipSettings>;
    return { ...defaultSettings(), ...parsed };
  } catch {
    return defaultSettings();
  }
}

// Exported $state so any component can import and react.
export const playbackSettings = $state<PlaybackSkipSettings>(readFromStorage());

export function savePlaybackSettings(next: PlaybackSkipSettings): void {
  Object.assign(playbackSettings, next);
  if (browser) {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(playbackSettings));
    } catch {
      // ignore storage errors (quota, disabled, etc.)
    }
  }
}

export function resetPlaybackSettings(): void {
  savePlaybackSettings(defaultSettings());
}
