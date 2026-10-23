import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'

/**
 * Vitest (standalone from Next.js): React transform, jsdom, MSW (src/test/setup.ts). `next/navigation` and `next/link`
 * are replaced by an in-memory router in src/test/setup.ts.
 */
export default defineConfig({
  plugins: [react()],
  // shadcn/ui CLI alias (components.json, tsconfig.json paths).
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  // Tests only: src/lib/lib.test.ts reads the backend's ErrorCodes.cs to keep errors.ts in sync.
  server: { fs: { allow: ['.', '../backend/src/Aide.Domain/Common'] } },
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
