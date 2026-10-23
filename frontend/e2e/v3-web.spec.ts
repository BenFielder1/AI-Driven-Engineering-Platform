import { expect, test, type Page, type WebSocket } from '@playwright/test'

import { createProjectViaApi, registerViaApi } from './support/api.ts'

/**
 * v3 web server checks in a real browser (runs in CI's v1 shard: every spec not named as v2 or a11y):
 * - FR-98: on a project page, SignalR's projects hub runs over a WebSocket through the same-origin /api proxy
 * - FR-99: the security headers and the per-request CSP nonce on /login, and no CSP violations during a smoke flow
 * - FR-101: the theme class is on <html> before hydration (the inline theme-init script, no light flash)
 * Test-only observation goes through Playwright (WebSocket events, init scripts); the app has no test hooks.
 */

declare global {
  interface Window {
    __cspViolations?: string[]
  }
}

/** Records CSP violations (the DOM event and console reports) for the page's whole life, across navigations. */
async function watchCsp(page: Page): Promise<() => Promise<string[]>> {
  const reported: string[] = []
  page.on('console', (message) => {
    if (/content[- ]security[- ]policy/i.test(message.text())) {
      reported.push(message.text())
    }
  })
  await page.addInitScript(() => {
    window.__cspViolations = []
    document.addEventListener('securitypolicyviolation', (e) => {
      window.__cspViolations?.push(`${e.violatedDirective} ${e.blockedURI}`)
    })
  })
  return async () => [...reported, ...(await page.evaluate(() => window.__cspViolations ?? []))]
}

test('FR-99: /login has the security headers and a fresh CSP nonce that every inline script carries', async ({ page }) => {
  const first = await page.goto('/login')
  expect(first?.status()).toBe(200)
  const headers = first?.headers() ?? {}
  const csp = headers['content-security-policy'] ?? ''
  expect(csp).toContain("default-src 'self'")
  expect(csp).toContain("connect-src 'self'")
  expect(csp).toMatch(/script-src 'self' 'nonce-[A-Za-z0-9+/=]+' 'strict-dynamic'/)
  expect(csp).toContain("object-src 'none'")
  expect(csp).toContain("base-uri 'self'")
  expect(csp).toContain("frame-ancestors 'none'")
  expect(headers['x-content-type-options']).toBe('nosniff')
  expect(headers['referrer-policy']).toBe('strict-origin-when-cross-origin')
  expect(headers['x-frame-options']).toBe('DENY')
  expect(headers['cache-control']).toMatch(/no-cache|no-store/)

  const nonce = /'nonce-([^']+)'/.exec(csp)?.[1]
  expect(nonce).toBeTruthy()
  // Browsers hide the nonce attribute from the DOM; the `nonce` property keeps it.
  const inlineNonces = await page.evaluate(() =>
    [...document.querySelectorAll('script')].filter((s) => !s.src).map((s) => s.nonce),
  )
  expect(inlineNonces.length).toBeGreaterThan(0)
  expect(new Set(inlineNonces)).toEqual(new Set([nonce]))

  const second = await page.goto('/login')
  expect(second?.headers()['content-security-policy']).not.toContain(`'nonce-${nonce ?? ''}'`)

  const api = await page.request.get('/api/health/ready')
  expect(api.headers()['x-content-type-options']).toBe('nosniff')
})

test('FR-98, FR-99: a project page runs live updates over a WebSocket through /api, with no CSP violations', async ({ page, context }) => {
  const violations = await watchCsp(page)
  const sockets: WebSocket[] = []
  const joinedOverSocket = new Promise<void>((resolve) => {
    page.on('websocket', (ws) => {
      sockets.push(ws)
      ws.on('framesent', (frame) => {
        if (typeof frame.payload === 'string' && frame.payload.includes('"target":"Join"')) {
          resolve()
        }
      })
    })
  })

  // Smoke flow: signed out → sign-in page, then signed in → projects → a project.
  await page.goto('/')
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fprojects$/)
  await registerViaApi(context, 'v3web')
  const project = await createProjectViaApi(context, `V3 web ${String(Date.now())}`)
  await page.goto('/projects')
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()
  await page.goto(`/projects/${project.id}`)

  await expect(page.getByTestId('live-indicator')).toHaveAttribute('data-status', 'live', { timeout: 20_000 })
  await joinedOverSocket
  const hub = sockets.filter((ws) => new URL(ws.url()).pathname === '/api/hubs/projects')
  expect(hub.length).toBeGreaterThan(0)
  const origin = new URL(page.url()).host
  for (const ws of hub) {
    expect(new URL(ws.url()).host).toBe(origin)
  }
  expect(hub.some((ws) => !ws.isClosed())).toBe(true)

  expect(await violations()).toEqual([])
})

for (const scheme of ['dark', 'light'] as const) {
  test(`FR-101: with system ${scheme} mode and empty storage, <html> is ${scheme} before hydration`, async ({ browser }) => {
    const context = await browser.newContext({ colorScheme: scheme })
    const page = await context.newPage()
    // No app JavaScript at all: only the inline theme-init script in <head> can set the class.
    await page.route('**/_next/static/**/*.js', (route) => route.abort())
    await page.goto('/login', { waitUntil: 'domcontentloaded' })
    const html = await page.evaluate(() => ({
      className: document.documentElement.className,
      colorScheme: document.documentElement.style.colorScheme,
    }))
    expect(html.className.split(/\s+/)).toContain(scheme)
    expect(html.className.split(/\s+/)).not.toContain(scheme === 'dark' ? 'light' : 'dark')
    expect(html.colorScheme).toBe(scheme)
    await context.close()
  })
}
