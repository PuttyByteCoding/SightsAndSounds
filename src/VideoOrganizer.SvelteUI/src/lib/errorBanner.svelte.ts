// Module-level state for the global error banner (issue #201). Every API call
// goes through api.ts's request()/fetch helpers; when one comes back 4xx/5xx
// they push a message here, and <ErrorBanner> (mounted once in the root layout)
// shows it briefly before it fades out. Mirrors the tagFlash store's
// push-then-auto-expire shape.

export interface ErrorBannerEntry {
  id: number;
  message: string;
}

// How long an entry stays before it's removed (which triggers the fade-out
// transition in the component). "3 second" banner per the issue.
const LIFETIME_MS = 3000;

function _ErrorBanner() {
  let entries = $state<ErrorBannerEntry[]>([]);
  let nextId = 0;

  function push(message: string) {
    const id = nextId++;
    entries = [...entries, { id, message }];
    setTimeout(() => dismiss(id), LIFETIME_MS);
  }

  function dismiss(id: number) {
    entries = entries.filter((e) => e.id !== id);
  }

  return {
    get entries() { return entries; },
    push,
    dismiss,
  };
}

export const errorBanner = _ErrorBanner();

// Builds a concise banner line from an HTTP failure. Prefers a short server
// message (e.g. {"error":"…"} or plain text) and falls back to the method/path.
export function httpErrorMessage(status: number, method: string, url: string, body?: string): string {
  const path = (() => {
    try { return new URL(url, 'http://x').pathname; } catch { return url; }
  })();
  let detail = '';
  if (body) {
    try {
      const parsed = JSON.parse(body);
      detail = typeof parsed?.error === 'string' ? parsed.error : '';
    } catch { /* not JSON */ }
    if (!detail) detail = body.trim();
  }
  detail = detail.replace(/\s+/g, ' ').slice(0, 160);
  return detail
    ? `Error ${status}: ${detail}`
    : `Error ${status} on ${method} ${path}`;
}
