// Module-level state caching the result of GET /api/runtime-info.
// Loaded once on app boot from +layout.svelte's onMount; the value
// then drives:
//   · the "must be on the host machine" banner in the layout
//   · whether to render local-only buttons (reveal in file manager,
//     run ffprobe) on the player and triage pages.
// The IsLocal answer is determined server-side from the inbound
// request's IP (loopback ⇒ local). On fetch failure we fall back to
// `not local` so the banner errs on the side of warning the user.

import { api } from './api';
import type { RuntimeInfo } from './types';

function _RuntimeStore() {
  let info = $state<RuntimeInfo | null>(null);
  let loaded = $state(false);
  let dismissed = $state(false);

  async function load() {
    if (loaded) return;
    try {
      info = await api.getRuntimeInfo();
    } catch {
      info = { isLocal: false, os: 'other' };
    } finally {
      loaded = true;
    }
  }

  function dismiss() {
    dismissed = true;
  }

  return {
    get info() { return info; },
    get loaded() { return loaded; },
    get dismissed() { return dismissed; },
    get isLocal() { return info?.isLocal ?? false; },
    get os() { return info?.os ?? 'other'; },
    load,
    dismiss
  };
}

export const runtimeStore = _RuntimeStore();
