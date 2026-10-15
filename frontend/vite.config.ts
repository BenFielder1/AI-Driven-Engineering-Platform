/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
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
    // No code splitting under Vitest: split route chunks are imported lazily on the first navigation, i.e. inside each
    // test's findBy timeout, and transforming them under parallel load left whole-app renders stuck on the router's
    // pending "Loading…" (NFR-TEST). Unsplit, route components load with routeTree.gen.ts at import time instead.
    tanstackRouter({ target: 'react', autoCodeSplitting: !process.env.VITEST }),
    react(),
    tailwindcss(),
  ],
  // shadcn/ui CLI alias (components.json, tsconfig.app.json paths).
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
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
    // src/lib/ui/tokens.test.ts reads index.css?raw to check it against tokens.ts; other CSS stays stubbed.
    css: { include: [/src[\\/]index\.css(\?|$)/] },
    // Page-level tests with MSW take several seconds under parallel load.
    testTimeout: 20_000,
  },
})
