import { existsSync } from 'node:fs'

import { defineConfig, devices } from '@playwright/test'

// BASE_URL: http://localhost:3000 (dev loop, default; FR-102) or http://localhost:8080 (Docker Desktop Kubernetes).
const baseURL = process.env.BASE_URL ?? 'http://localhost:3000'
const startServer = !process.env.BASE_URL
/**
 * Without BASE_URL Playwright starts the web server on port 3000 (API at BACKEND_URL, default http://localhost:5080).
 * In CI it's the production server (`next build` + `node server.ts`), which is stable under parallel load and matches
 * the image (no dev compile-on-demand, no `'unsafe-eval'` in the CSP); locally it's the dev server.
 */
const webServerCommand = process.env.CI ? 'npm run build && npm start' : 'npm run dev'

/**
 * NFR-Browsers (SPEC §8: current Chrome, Edge, Firefox and Safari). Chromium runs every spec; Firefox and WebKit
 * (Safari's engine) run the cross-browser set below. Edge runs it too when it is installed locally (or E2E_MSEDGE=1),
 * otherwise Chromium stands in for Edge's engine. a11y scans stay on Chromium only (axe results are engine-neutral).
 * Every registration in every project goes through the one register lock in support/api.ts (5/min per IP).
 */
const crossBrowserSpecs = /(smoke|auth|lifecycle|permissions)\.spec\.ts$/
const edgePaths = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
  '/opt/microsoft/msedge/msedge',
]
const edgeAvailable = process.env.E2E_MSEDGE === '1' || (process.env.E2E_MSEDGE !== '0' && edgePaths.some((p) => existsSync(p)))

function definedEnv(): Record<string, string> {
  return Object.fromEntries(Object.entries(process.env).filter((e): e is [string, string] => e[1] !== undefined))
}

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
    { name: 'firefox', use: { ...devices['Desktop Firefox'] }, testMatch: crossBrowserSpecs },
    { name: 'webkit', use: { ...devices['Desktop Safari'] }, testMatch: crossBrowserSpecs },
    ...(edgeAvailable
      ? [{ name: 'msedge', use: { ...devices['Desktop Edge'], channel: 'msedge' }, testMatch: crossBrowserSpecs }]
      : []),
    // NFR-A11Y: axe scans run against data created once by the setup project.
    { name: 'a11y-setup', use: { ...devices['Desktop Chrome'] }, testMatch: /a11y\.setup\.ts$/ },
    { name: 'a11y', use: { ...devices['Desktop Chrome'] }, testMatch: /a11y\.spec\.ts$/, dependencies: ['a11y-setup'] },
  ],
  ...(startServer
    ? {
        webServer: {
          command: webServerCommand,
          url: baseURL,
          reuseExistingServer: !process.env.CI,
          timeout: process.env.CI ? 600_000 : 120_000,
          // server.ts listens on PORT (production default 8080); BACKEND_URL passes through from the environment.
          env: { ...definedEnv(), PORT: '3000' },
        },
      }
    : {}),
})
