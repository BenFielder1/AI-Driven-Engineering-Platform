import { defineConfig, devices } from '@playwright/test'

// BASE_URL: http://localhost:5173 (dev loop, default) or http://localhost:8080 (Docker Desktop Kubernetes).
const baseURL = process.env.BASE_URL ?? 'http://localhost:5173'
const startDevServer = !process.env.BASE_URL

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  ...(startDevServer
    ? {
        webServer: {
          command: 'npm run dev',
          url: baseURL,
          reuseExistingServer: true,
          timeout: 60_000,
        },
      }
    : {}),
})
