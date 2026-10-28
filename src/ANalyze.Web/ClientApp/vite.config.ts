import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { readFileSync } from 'node:fs'
import { defineConfig } from 'vitest/config'

const backend = process.env.ANALYZE_BACKEND ?? 'http://127.0.0.1:5199'
const version = (() => {
  if (process.env.APP_VERSION) return process.env.APP_VERSION
  try {
    return readFileSync(new URL('../../../VERSION', import.meta.url), 'utf8').trim()
  } catch {
    return 'dev'
  }
})()

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: { __APP_VERSION__: JSON.stringify(version) },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': backend,
      '/health': backend,
      '/hubs': { target: backend, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
  },
})
