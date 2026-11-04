import { expect, test, type Page } from '@playwright/test'

import { password, registerViaApi } from './support/api.ts'

/**
 * v6 M41: the marketing site and the root/auth routing (FR-210, FR-211, FR-221, §4.32). Visitors use a fresh
 * (signed-out) context; signed-in rows register through the API (support/api.ts's register lock).
 */

const HERO = 'Ship software with AI agents, under human control.'

function header(page: Page) {
  return page.getByTestId('marketing-header')
}

for (const width of [375, 1440]) {
  test(`FR210_above_the_fold: / for a visitor shows the h1, Get started and Sign in without scrolling at ${String(width)} px`, async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width, height: 800 } })
    try {
      const page = await context.newPage()
      const consoleErrors: string[] = []
      page.on('console', (message) => {
        // SPEC §6: the visitor's 401 from /api/auth/me is expected. The app never logs it; only the browser's own
        // network line for that request is tolerated.
        if (message.type() === 'error' && !message.location().url.endsWith('/api/auth/me')) {
          consoleErrors.push(message.text())
        }
      })
      await page.goto('/')

      await expect(page).toHaveURL(/\/$/)
      await expect(page.getByRole('heading', { level: 1, name: HERO, exact: true })).toBeInViewport()
      await expect(header(page).getByRole('link', { name: 'Get started', exact: true })).toBeInViewport()
      await expect(header(page).getByRole('link', { name: 'Sign in', exact: true })).toBeInViewport()
      expect(consoleErrors).toEqual([])
    } finally {
      await context.close()
    }
  })
}

test('FR211_signed_in_root: / sends a signed-in user to /home', async ({ page, context }) => {
  await registerViaApi(context, 'mkroot', 'Mara Root')

  await page.goto('/')

  await expect(page).toHaveURL(/\/home$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Mara', exact: true })).toBeVisible()
})

test('FR210_open_app: /product signed in stays on /product and the header shows Open app', async ({ page, context }) => {
  await registerViaApi(context, 'mkproduct')

  await page.goto('/product')

  await expect(header(page).getByRole('link', { name: 'Open app', exact: true })).toBeVisible()
  await expect(header(page).getByRole('link', { name: 'Sign in', exact: true })).toHaveCount(0)
  await expect(page).toHaveURL(/\/product$/)
})

test('§4.32: /login?returnUrl=https://evil.example lands on /home after sign-in (open-redirect guard)', async ({ page, browser }) => {
  const other = await browser.newContext()
  const user = await registerViaApi(other, 'mkevil', 'Eve Redirect')
  await other.close()

  await page.goto('/login?returnUrl=https%3A%2F%2Fevil.example')
  await page.getByLabel('Email').fill(user.email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()

  await expect(page).toHaveURL(/\/home$/)
})

for (const [path, returnUrl] of [
  ['/projects', '%2Fprojects'],
  ['/home', '%2Fhome'],
] as const) {
  test(`§4.32: ${path} signed out redirects to /login?returnUrl=${returnUrl}`, async ({ page }) => {
    await page.goto(path)

    await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=${returnUrl}$`))
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Tevrin', exact: true })).toBeVisible()
  })
}

test('FR210_get_started: Get started opens /register', async ({ page }) => {
  await page.goto('/')

  await header(page).getByRole('link', { name: 'Get started', exact: true }).click()

  await expect(page).toHaveURL(/\/register$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Create your Tevrin account', exact: true })).toBeVisible()
})

test('FR210_mobile_menu_keyboard: open with the keyboard, Tab stays in the sheet, Escape closes it and focus returns', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 375, height: 800 } })
  try {
    const page = await context.newPage()
    await page.goto('/product')
    const button = header(page).getByRole('button', { name: 'Open menu' })
    await expect(button).toBeVisible()

    await button.focus()
    await page.keyboard.press('Enter')
    const sheet = page.getByRole('dialog', { name: 'Menu' })
    await expect(sheet).toBeVisible()
    for (let i = 0; i < 12; i++) {
      await page.keyboard.press('Tab')
      expect(await sheet.evaluate((el) => el.contains(document.activeElement))).toBe(true)
    }

    await page.keyboard.press('Escape')

    await expect(sheet).toHaveCount(0)
    await expect(button).toBeFocused()
  } finally {
    await context.close()
  }
})

test('FR221_robots_and_sitemap: robots.txt and sitemap.xml return 200 with the marketing routes on this host', async ({ request, baseURL }) => {
  const origin = new URL(baseURL ?? 'http://localhost:3000').origin

  const robots = await request.get('/robots.txt')
  expect(robots.status()).toBe(200)
  const robotsText = await robots.text()
  expect(robotsText).toContain('Allow: /product')
  expect(robotsText).toContain('Allow: /login')
  expect(robotsText).toContain('Disallow: /projects')
  expect(robotsText).toContain(`Sitemap: ${origin}/sitemap.xml`)

  const sitemap = await request.get('/sitemap.xml')
  expect(sitemap.status()).toBe(200)
  const sitemapText = await sitemap.text()
  for (const path of ['/', '/product', '/how-it-works', '/integrations', '/self-host', '/security', '/faq', '/changelog']) {
    expect(sitemapText).toContain(`<loc>${origin}${path}</loc>`)
  }
})

test('FR221_noindex: app routes send X-Robots-Tag: noindex; marketing and auth routes do not', async ({ request }) => {
  expect((await request.get('/projects', { maxRedirects: 0 })).headers()['x-robots-tag']).toBe('noindex')
  for (const path of ['/', '/product', '/login', '/register']) {
    expect((await request.get(path)).headers()['x-robots-tag'], path).toBeUndefined()
  }
})

test('FR218_faq_keyboard: Tab to a question, Enter and Space toggle it, ArrowDown and End move between questions', async ({ page }) => {
  await page.goto('/faq')
  const first = page.getByRole('button', { name: 'What is Tevrin?', exact: true })
  const second = page.getByRole('button', { name: 'Who is it for?', exact: true })
  const last = page.getByRole('button', { name: 'Was it called AIDE?', exact: true })
  await expect(first).toBeVisible()

  // Tab from the main landmark (the skip link's target) to the first question.
  await page.getByRole('main').focus()
  for (let i = 0; i < 20 && !(await first.evaluate((el) => el === document.activeElement)); i++) {
    await page.keyboard.press('Tab')
  }
  await expect(first).toBeFocused()

  await page.keyboard.press('Enter')
  await expect(first).toHaveAttribute('aria-expanded', 'true')
  await expect(page.getByRole('region', { name: 'What is Tevrin?' })).toBeVisible()
  await page.keyboard.press('Space')
  await expect(first).toHaveAttribute('aria-expanded', 'false')
  await expect(page.getByRole('region', { name: 'What is Tevrin?' })).toBeHidden()

  await page.keyboard.press('ArrowDown')
  await expect(second).toBeFocused()
  await page.keyboard.press('End')
  await expect(last).toBeFocused()
})

/** The running CSS/Web animations inside the How it works lifecycle diagram. */
async function runningDiagramAnimations(page: Page): Promise<number> {
  const diagram = page.getByTestId('lifecycle-diagram')
  await expect(diagram).toBeVisible()
  return diagram.evaluate((el) => el.getAnimations({ subtree: true }).filter((a) => a.playState === 'running').length)
}

test('FR214_reduced_motion: the lifecycle diagram animates normally and not at all with prefers-reduced-motion', async ({ browser }) => {
  for (const [reducedMotion, expectRunning] of [
    ['no-preference', true],
    ['reduce', false],
  ] as const) {
    const context = await browser.newContext({ reducedMotion })
    try {
      const page = await context.newPage()
      await page.goto('/how-it-works')
      const running = await runningDiagramAnimations(page)
      if (expectRunning) {
        expect(running, reducedMotion).toBeGreaterThan(0)
      } else {
        expect(running, reducedMotion).toBe(0)
      }
    } finally {
      await context.close()
    }
  }
})

test('FR220_reduced_motion: Home previews are static with prefers-reduced-motion', async ({ browser }) => {
  const context = await browser.newContext({ reducedMotion: 'reduce' })
  try {
    const page = await context.newPage()
    await page.goto('/')
    const preview = page.getByTestId('home-hero').getByTestId('product-preview')
    await expect(preview).toBeAttached()
    expect(await preview.evaluate((el) => el.getAnimations({ subtree: true }).filter((a) => a.playState === 'running').length)).toBe(0)
  } finally {
    await context.close()
  }
})

test('FR212_bento_anchor: a Home feature tile opens its Product section', async ({ page }) => {
  await page.goto('/')

  await page.getByTestId('bento-tile').filter({ hasText: 'Issue-tracker imports' }).click()

  await expect(page).toHaveURL(/\/product#issue-trackers$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Issue-tracker imports', exact: true })).toBeInViewport()
})
