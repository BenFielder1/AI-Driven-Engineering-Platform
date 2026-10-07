/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Same-origin /api: in dev Vite proxies to the API (5080); in Kubernetes the web container's nginx does.
// `ws: true` forwards WebSocket upgrades (SignalR hubs under /api/hubs).
const apiProxy = {
  '/api': {
    target: 'http://localhost:5080',
    changeOrigin: false,
    ws: true,
  },
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    // Must come before react(): generates src/routeTree.gen.ts from src/routes/.
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  server: {
    port: 5173,
    strictPort: true,
    proxy: apiProxy,
    // Tests only: src/lib/lib.test.ts reads the backend's ErrorCodes.cs to keep errors.ts in sync.
    ...(process.env.VITEST ? { fs: { allow: ['.', '../backend/src/Aide.Domain/Common'] } } : {}),
  },
  preview: {
    port: 4173,
    proxy: apiProxy,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
  },
})
