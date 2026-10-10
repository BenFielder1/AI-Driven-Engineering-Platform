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
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    actionTimeout: 15_000,
    trace: 'on-first-retry',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] }, testIgnore: /a11y\.(spec|setup)\.ts$/ },
    // NFR-A11Y: axe scans run against data created once by the setup project.
    { name: 'a11y-setup', use: { ...devices['Desktop Chrome'] }, testMatch: /a11y\.setup\.ts$/ },
    { name: 'a11y', use: { ...devices['Desktop Chrome'] }, testMatch: /a11y\.spec\.ts$/, dependencies: ['a11y-setup'] },
  ],
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
