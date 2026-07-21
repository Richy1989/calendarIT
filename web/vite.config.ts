import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// In dev, the Vite server proxies API calls to the ASP.NET Core backend so the SPA
// and API share an origin (no CORS). The default matches the backend's "http" launch
// profile (Properties/launchSettings.json). Override with VITE_API_TARGET.
const apiTarget = process.env.VITE_API_TARGET ?? 'http://localhost:5299'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // Static assets (incl. the favicon) live in the repo-root public/ folder — one source
  // shared by the app and the README.
  publicDir: '../public',
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
    },
  },
})
