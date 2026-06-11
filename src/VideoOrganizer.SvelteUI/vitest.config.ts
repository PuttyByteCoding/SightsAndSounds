// Vitest config. Kept separate from vite.config.ts because Vitest 4
// no longer reads `test:` keys from a vite config that uses the
// SvelteKit plugin — the plugin's setup interferes with the test
// runner's module resolution. Having its own file also means
// vitest doesn't need to spin up the dev-server proxies.
//
// Tests are colocated next to the source files they cover, named
// `*.test.ts` or `*.test.svelte.ts`. Run with `npm test` (one-shot)
// or `npm run test:watch` for an active loop.

import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // jsdom gives us `document` / `window` for tests that touch the
    // localStorage API (loadColumnWidths / saveColumnWidths round-trip),
    // even though the helpers themselves are pure-ish TS. Switch to
    // 'happy-dom' later if jsdom feels slow; for the test surface
    // we have now, either is fine.
    environment: 'jsdom',
    // Pick up *.test.ts and *.test.svelte.ts under src/. Doesn't
    // need the .svelte-kit folder.
    include: ['src/**/*.test.{ts,svelte.ts}'],
    // Don't try to bundle these — they're runtime-only and would
    // pull in the entire SvelteKit boot if vitest tried to compile
    // them through the plugin.
    exclude: [
      'node_modules/**',
      '.svelte-kit/**',
      'build/**'
    ]
  },
  // Vitest reads tsconfig.json for path aliases; nothing else to add
  // here. Vite plugins (tailwind, sveltekit) are intentionally NOT
  // wired in — see the file-level comment.
  resolve: {
    // The $lib alias is the SvelteKit convention for src/lib/*.
    // Tests need it manually since the sveltekit plugin isn't loaded.
    alias: {
      $lib: new URL('./src/lib', import.meta.url).pathname
    }
  }
});
