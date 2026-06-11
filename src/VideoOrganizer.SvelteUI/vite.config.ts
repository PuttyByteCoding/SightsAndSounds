import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  // Load process env (from shell + .env files) so the Docker dev container can
  // override the proxy target via VITE_API_PROXY_TARGET=http://api:8080.
  // Third arg '' loads ALL env vars, not just VITE_-prefixed ones, so
  // CHOKIDAR_USEPOLLING works too.
  const env = loadEnv(mode, process.cwd(), '');

  const apiTarget = env.VITE_API_PROXY_TARGET || 'http://localhost:5098';
  const usePolling = env.CHOKIDAR_USEPOLLING === 'true';

  return {
    plugins: [tailwindcss(), sveltekit()],
    server: {
      host: '0.0.0.0',
      port: 5173,
      strictPort: false,
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: false
        },
        // Scalar API reference UI lives on the API host. Proxy so the in-app
        // /api-docs iframe loads the same in dev as in production.
        '/swagger': {
          target: apiTarget,
          changeOrigin: false
        },
        // Scalar fetches the OpenAPI document at runtime using a URL
        // relative to its own origin (window.location.origin + /openapi/...).
        // When the page loads through this dev server, that origin is the
        // Vite host, not the API — so /openapi must be proxied too or the
        // iframe loads with an empty sidebar.
        '/openapi': {
          target: apiTarget,
          changeOrigin: false
        }
      },
      watch: usePolling ? { usePolling: true, interval: 500 } : undefined
    }
  };
});
