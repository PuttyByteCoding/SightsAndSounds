// A tiny shared concurrency-limited queue. Used by VideoCard to pre-warm
// thumbnail sprites without saturating the server (which runs ffmpeg per
// video). 2 concurrent is a reasonable trade-off — more doesn't help because
// the server serializes work per video anyway, and larger N just fills the
// browser's HTTP/1 queue with pending requests.

const MAX_CONCURRENT = 2;
const queue: (() => Promise<unknown>)[] = [];
let active = 0;

function drain() {
  while (active < MAX_CONCURRENT && queue.length > 0) {
    const task = queue.shift()!;
    active++;
    task().finally(() => {
      active--;
      drain();
    });
  }
}

// Enqueue a task. Returns a promise that resolves when the task completes.
export function enqueueWarm<T>(task: () => Promise<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    queue.push(async () => {
      try {
        resolve(await task());
      } catch (err) {
        reject(err);
      }
    });
    drain();
  });
}

// Abandon any tasks still waiting. Already-running tasks continue.
// Call when the user navigates to a different folder so stale prewarms
// don't keep hitting the server for videos the user no longer cares about.
export function cancelPendingWarms() {
  queue.length = 0;
}
