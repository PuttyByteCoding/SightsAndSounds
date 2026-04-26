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
        // Swagger UI lives on the API host. Proxy so the in-app /api-docs
        // iframe loads the same in dev as in production.
        '/swagger': {
          target: apiTarget,
          changeOrigin: false
        }
      },
      watch: usePolling ? { usePolling: true, interval: 500 } : undefined
    }
  };
});
