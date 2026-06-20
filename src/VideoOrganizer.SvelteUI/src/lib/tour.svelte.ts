// Lightweight on-demand guided tour (issue #170). A tiny step engine —
// no external dependency — driving the spotlight + callout rendered by
// TourOverlay.svelte (mounted once in the root layout). Steps anchor to
// elements by CSS selector (we tag the relevant UI with `data-tour="…"`);
// the overlay resolves the live element at render time, so a missing or
// hidden target just degrades to a centered callout rather than breaking.
//
// Most of the app's value is already covered by the static Help page, so
// this is deliberately optional: launched on demand ("Take a tour" on the
// Help page / a button on Browse) or once on first run, and dismissable at
// any step. The "seen" flag is persisted so first-run only auto-starts once.

const SEEN_KEY = 'guidedTourSeen';

export interface TourStep {
  /** CSS selector for the element to spotlight (e.g. '[data-tour="player"]'). */
  selector: string;
  title: string;
  body: string;
}

function _Tour() {
  let active = $state(false);
  let steps = $state<TourStep[]>([]);
  let index = $state(0);

  function start(s: TourStep[]) {
    if (s.length === 0) return;
    steps = s;
    index = 0;
    active = true;
    markSeen();
  }

  function next() {
    if (index < steps.length - 1) index += 1;
    else stop();
  }

  function prev() {
    if (index > 0) index -= 1;
  }

  function stop() {
    active = false;
  }

  function markSeen() {
    try { localStorage.setItem(SEEN_KEY, '1'); } catch { /* private mode / SSR */ }
  }

  function hasSeen(): boolean {
    try { return localStorage.getItem(SEEN_KEY) === '1'; } catch { return true; }
  }

  return {
    get active() { return active; },
    get steps() { return steps; },
    get index() { return index; },
    get total() { return steps.length; },
    get current(): TourStep | null { return steps[index] ?? null; },
    get isFirst() { return index === 0; },
    get isLast() { return index === steps.length - 1; },
    start,
    next,
    prev,
    stop,
    hasSeen,
  };
}

export const tour = _Tour();
