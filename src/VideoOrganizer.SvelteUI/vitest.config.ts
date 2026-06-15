// Vitest config. Kept separate from vite.config.ts because Vitest 4
// no longer reads `test:` keys from a vite config that uses the
// SvelteKit plugin — the plugin's setup interferes with the test
// runner's module resolution. Having its own file also means
// vitest doesn't need to spin up the dev-server proxies.
//
// Two projects (#129):
//   · unit       — pure-ish helpers (*.test.ts / *.test.svelte.ts). No
//                  Svelte plugin, exactly as before, so this surface is
//                  unchanged and fast.
//   · components — Svelte component tests (*.component.test.ts), which DO
//                  need the Svelte compiler to mount `.svelte` files. The
//                  plugin is scoped to this project only, so it can't
//                  interfere with the unit project.
//
// Tests are colocated next to the source they cover. Run with `npm test`.

import { defineConfig } from 'vitest/config';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { svelteTesting } from '@testing-library/svelte/vite';

const libAlias = { $lib: new URL('./src/lib', import.meta.url).pathname };

export default defineConfig({
  test: {
    projects: [
      {
        resolve: { alias: libAlias },
        test: {
          name: 'unit',
          environment: 'jsdom',
          include: ['src/**/*.test.{ts,svelte.ts}'],
          // Component tests live in the other project; keep them out of here
          // so they don't run without the Svelte plugin.
          exclude: [
            'node_modules/**',
            '.svelte-kit/**',
            'build/**',
            'src/**/*.component.test.ts',
          ],
        },
      },
      {
        plugins: [svelte(), svelteTesting()],
        resolve: { alias: libAlias },
        test: {
          name: 'components',
          environment: 'jsdom',
          include: ['src/**/*.component.test.ts'],
          setupFiles: ['./src/lib/components/test-setup.ts'],
        },
      },
    ],
  },
});
