import { defineConfig } from 'vitest/config'
import { loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  return {
    base: env.VITE_BASE_PATH || '/',
    plugins: [react()],
    server: {
      // Standalone SPA dev server (HMR). Browse this port during `npm run dev`; API calls to
      // /api/* are proxied to the example web host (Kestrel) below.
      port: 5173,
      proxy: {
        '/api': {
          // Must match the web host's HTTPS port in
          // tests/Swarmwright.Example.WebHost/Properties/launchSettings.json.
          target: 'https://localhost:7001',
          secure: false,
          changeOrigin: true,
        },
      },
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test-setup.ts'],
    },
  }
})
