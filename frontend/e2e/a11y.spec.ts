import { readFileSync } from 'node:fs'

import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Browser, type Locator, type Page } from '@playwright/test'

import { A11Y_FIXTURE, type A11yFixture } from './support/a11y-fixture.ts'
import { v2Targets } from './support/a11y-v2-targets.ts'

/**
 * NFR-A11Y: automated WCAG 2.2 AA checks (axe-core) on the core flows, in the light and the dark theme (the app
 * follows `prefers-color-scheme` by default), plus §8 "usable at 375 px width" (no horizontal page scroll).
 * One user and one released Fake + Local project (strict mode), created by a11y.setup.ts, give every page real content.
 */

const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

let cached: A11yFixture | undefined
/** Written by a11y.setup.ts (the `a11y-setup` project this project depends on); read lazily, after setup ran. */
function fixture(): A11yFixture {
  cached ??= JSON.parse(readFileSync(A11Y_FIXTURE, 'utf-8')) as A11yFixture
  return cached
}

interface Target {
  name: string
  path: () => string
  signedIn: boolean
  /** Visible once the page has loaded its data. */
  ready: (page: Page) => Locator
  prepare?: (page: Page) => Promise<void>
}

const targets: Target[] = [
  {
    name: 'login',
    path: () => '/login',
    signedIn: false,
    ready: (p) => p.getByRole('heading', { level: 1, name: 'Sign in to AIDE' }),
  },
  {
    name: 'projects list',
    path: () => '/projects',
    signedIn: true,
    ready: (p) => p.getByRole('link', { name: /E2E Accessibility/ }).first(),
  },
  {
    name: 'wizard (idea step, with errors)',
    path: () => '/projects/new',
    signedIn: true,
    ready: (p) => p.getByRole('heading', { name: 'Step 1 of 8: Source' }),
    prepare: async (p) => {
      await p.getByRole('button', { name: 'Next' }).click()
      await expect(p.getByRole('heading', { name: 'Step 2 of 8: Project idea' })).toBeVisible()
      await p.getByRole('button', { name: 'Next' }).click()
      await expect(p.getByText('Check this step')).toBeVisible()
    },
  },
  {
    name: 'project overview',
    path: () => `/projects/${fixture().projectId}`,
    signedIn: true,
    ready: (p) => p.getByRole('list', { name: 'Activity' }),
  },
  {
    name: 'artifact detail (plan steps)',
    path: () => `/projects/${fixture().projectId}/artifacts/implementation_plan`,
    signedIn: true,
    ready: (p) => p.getByRole('region', { name: 'History' }),
  },
  {
    name: 'artifact diff',
    path: () => `/projects/${fixture().diffProjectId}/artifacts/project_request`,
    signedIn: true,
    ready: (p) => p.getByRole('region', { name: 'History' }),
    prepare: async (p) => {
      const compare = p.getByRole('form', { name: 'Compare versions' })
      await compare.getByLabel('From version').selectOption('1')
      await compare.getByLabel('To version').selectOption('2')
      await compare.getByRole('button', { name: 'Compare' }).click()
      await expect(p.getByRole('table', { name: /^Changes from v1 to v2/ })).toBeVisible()
    },
  },
  {
    name: 'execution',
    path: () => `/projects/${fixture().projectId}/execution`,
    signedIn: true,
    ready: (p) => p.getByRole('list', { name: 'Execution steps' }),
  },
  {
    name: 'review',
    path: () => `/projects/${fixture().projectId}/review`,
    signedIn: true,
    ready: (p) => p.getByRole('heading', { name: /^Latest report: cycle/ }),
  },
  {
    name: 'settings (general)',
    path: () => `/projects/${fixture().projectId}/settings/general`,
    signedIn: true,
    ready: (p) => p.getByRole('navigation', { name: 'Settings sections' }).getByRole('link', { name: 'General' }),
  },
  {
    name: 'settings (danger zone)',
    path: () => `/projects/${fixture().projectId}/settings/danger`,
    signedIn: true,
    ready: (p) => p.getByRole('button', { name: 'Roll back…' }),
  },
  {
    name: 'notifications',
    path: () => '/notifications',
    signedIn: true,
    ready: (p) => p.getByRole('list', { name: 'Notifications' }),
  },
  {
    name: 'register',
    path: () => '/register',
    signedIn: false,
    ready: (p) => p.getByRole('heading', { level: 1, name: 'Create your AIDE account' }),
  },
  {
    name: 'account',
    path: () => '/account',
    signedIn: true,
    ready: (p) => p.getByRole('region', { name: 'Profile' }),
  },
  {
    name: 'agent runs',
    path: () => `/projects/${fixture().projectId}/runs`,
    signedIn: true,
    ready: (p) => p.getByRole('table', { name: /^Agent runs, newest first/ }),
  },
  ...v2Targets(fixture),
]

/**
 * NFR-A11Y2 (FR-53, FR-54): the v2 shell in its interactive states: the sidebar in project mode with the header's
 * live indicator and stage stepper (1440 px), the drawer opened at 375 px, the command palette, and the user and theme
 * menus. Each state is checked in both themes.
 */
interface ShellState {
  name: string
  path: () => string
  width: number
  ready: (page: Page) => Locator
  /** Opens the state under test; returns the element that must be visible when it is open. */
  open?: (page: Page) => Promise<Locator>
}

const shellStates: ShellState[] = [
  {
    name: 'sidebar in project mode, header and stage stepper (1440 px)',
    path: () => `/projects/${fixture().projectId}/review`,
    width: 1440,
    ready: (p) => p.getByRole('navigation', { name: 'Lifecycle stages' }),
  },
  {
    name: 'sidebar collapsed to icons at lg (1100 px)',
    path: () => `/projects/${fixture().projectId}/artifacts`,
    width: 1100,
    ready: (p) => p.getByRole('complementary', { name: 'Sidebar' }).getByRole('button', { name: 'Expand sidebar' }),
  },
  {
    name: 'mobile drawer open (375 px)',
    path: () => `/projects/${fixture().projectId}`,
    width: 375,
    ready: (p) => p.getByRole('button', { name: 'Open navigation' }),
    open: async (p) => {
      await p.getByRole('button', { name: 'Open navigation' }).click()
      return p.getByTestId('mobile-drawer')
    },
  },
  {
    name: 'command palette open',
    path: () => '/projects',
    width: 1440,
    ready: (p) => p.getByRole('heading', { level: 1, name: 'Projects', exact: true }),
    open: async (p) => {
      await p.getByRole('complementary', { name: 'Sidebar' }).getByRole('button', { name: 'Search pages' }).click()
      return p.getByRole('dialog', { name: 'Command palette' })
    },
  },
  {
    name: 'user menu open',
    path: () => '/projects',
    width: 1440,
    ready: (p) => p.getByRole('heading', { level: 1, name: 'Projects', exact: true }),
    open: async (p) => {
      await p.getByRole('complementary', { name: 'Sidebar' }).getByRole('button', { name: /Account menu for/ }).click()
      return p.getByRole('menu')
    },
  },
]

for (const colorScheme of ['light', 'dark'] as const) {
  for (const state of shellStates) {
    test(`NFR-A11Y2: shell — ${state.name} has no WCAG 2.2 AA violations (${colorScheme} theme)`, async ({ browser }, testInfo) => {
      const context = await browser.newContext({
        colorScheme,
        storageState: fixture().storageState,
        viewport: { width: state.width, height: 900 },
      })
      try {
        const page = await context.newPage()
        await page.goto(state.path())
        await expect(state.ready(page)).toBeVisible({ timeout: 20_000 })
        if (state.open) {
          await expect(await state.open(page)).toBeVisible()
        }
        await expect(page.getByRole('status').filter({ hasText: /^Loading/ })).toHaveCount(0)
        const results = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze()
        const violations = results.violations.map((v) => ({
          id: v.id,
          impact: v.impact,
          help: v.help,
          nodes: v.nodes.map((n) => ({ target: n.target.join(' '), summary: n.failureSummary })),
        }))
        await testInfo.attach('axe-violations.json', {
          body: JSON.stringify({ state: state.name, colorScheme, violations }, null, 2),
          contentType: 'application/json',
        })
        expect(violations, JSON.stringify(violations, null, 2)).toEqual([])
        if (state.width === 375) {
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
          expect(overflow).toBeLessThanOrEqual(0)
        }
      } finally {
        await context.close()
      }
    })
  }
}

test('FR54: the stage stepper is keyboard navigable and marks the current stage', async ({ browser }) => {
  const context = await browser.newContext({ storageState: fixture().storageState, viewport: { width: 1440, height: 900 } })
  try {
    const page = await context.newPage()
    await page.goto(`/projects/${fixture().projectId}/review`)
    const stepper = page.getByRole('navigation', { name: 'Lifecycle stages' })
    await expect(stepper).toBeVisible({ timeout: 20_000 })
    await expect(stepper.locator('[aria-current="page"]')).toContainText('Review')
    const links = stepper.getByRole('link')
    await expect(links).toHaveCount(6)
    await links.first().focus()
    await page.keyboard.press('ArrowRight')
    await expect(links.nth(1)).toBeFocused()
    await page.keyboard.press('End')
    await expect(links.nth(5)).toBeFocused()
  } finally {
    await context.close()
  }
})

/** Tabs forward (at most `max` times) until focus is on or inside `target`; returns whether it got there. */
async function tabTo(page: Page, target: Locator, max = 60): Promise<boolean> {
  await target.evaluate((el) => {
    el.setAttribute('data-tab-target', '')
  })
  for (let i = 0; i < max; i++) {
    await page.keyboard.press('Tab')
    if (await page.evaluate(() => document.activeElement?.closest('[data-tab-target]') != null)) {
      await target.evaluate((el) => {
        el.removeAttribute('data-tab-target')
      })
      return true
    }
  }
  return false
}

test('NFR-A11Y2, FR-53: sidebar items are reachable by Tab and the active item has aria-current', async ({ browser }) => {
  const context = await browser.newContext({ storageState: fixture().storageState, viewport: { width: 1440, height: 900 } })
  try {
    const page = await context.newPage()
    await page.goto(`/projects/${fixture().projectId}/artifacts`)
    const sidebar = page.getByRole('complementary', { name: 'Sidebar' })
    await expect(sidebar.getByRole('navigation', { name: 'Project sections' })).toBeVisible({ timeout: 20_000 })
    const current = sidebar.locator('a[aria-current="page"]')
    await expect(current).toHaveCount(1)
    await expect(current).toContainText('Artifacts')
    // From the top of the page: skip link, lockup, search, project switcher, then the project nav, in order.
    expect(await tabTo(page, sidebar.getByRole('navigation', { name: 'Project sections' }).getByRole('link').first())).toBe(true)
    expect(await tabTo(page, current)).toBe(true)
    await expect(current).toBeFocused()
    // FR-54: the stepper comes after the sidebar in Tab order, and its links are focusable.
    expect(await tabTo(page, page.getByRole('navigation', { name: 'Lifecycle stages' }))).toBe(true)
    await page.keyboard.press('Enter')
    await expect(page.getByRole('navigation', { name: 'Lifecycle stages' }).locator('[aria-current="page"]')).toHaveCount(1)
    // Navigating moves aria-current in the sidebar too (no stale active item).
    await expect(sidebar.locator('a[aria-current="page"]')).toHaveCount(1)
    await expect(sidebar.locator('a[aria-current="page"]')).not.toContainText('Artifacts')
  } finally {
    await context.close()
  }
})

test('FR-54: stepper ArrowLeft moves back, Home goes to the first stage and arrows stop at the ends', async ({ browser }) => {
  const context = await browser.newContext({ storageState: fixture().storageState, viewport: { width: 1440, height: 900 } })
  try {
    const page = await context.newPage()
    await page.goto(`/projects/${fixture().projectId}/planning`)
    const stepper = page.getByRole('navigation', { name: 'Lifecycle stages' })
    await expect(stepper.locator('[aria-current="page"]')).toContainText('Planning', { timeout: 20_000 })
    const links = stepper.getByRole('link')
    await links.nth(2).focus()
    await page.keyboard.press('ArrowLeft')
    await expect(links.nth(1)).toBeFocused()
    await page.keyboard.press('Home')
    await expect(links.first()).toBeFocused()
    await page.keyboard.press('ArrowLeft')
    await expect(links.first()).toBeFocused()
    await page.keyboard.press('End')
    await page.keyboard.press('ArrowRight')
    await expect(links.nth(5)).toBeFocused()
  } finally {
    await context.close()
  }
})

/**
 * NFR-A11Y2: Admin → Integrations needs an Admin (the first account on the cluster, seeded as admin@aide.local with a
 * generated password). Set E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD to run it.
 */
for (const colorScheme of ['light', 'dark'] as const) {
  test(`NFR-A11Y2: admin integrations has no WCAG 2.2 AA violations (${colorScheme} theme)`, async ({ browser }, testInfo) => {
    const email = process.env.E2E_ADMIN_EMAIL
    const adminPassword = process.env.E2E_ADMIN_PASSWORD
    test.skip(!email || !adminPassword, 'needs E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD (an Admin account on the target)')
    const context = await browser.newContext({ colorScheme })
    try {
      const login = await context.request.post('/api/auth/login', { data: { email, password: adminPassword } })
      expect(login.status(), await login.text()).toBe(200)
      const page = await context.newPage()
      await page.goto('/admin/integrations')
      await expect(page.getByRole('heading', { level: 2, name: 'Integrations', exact: true })).toBeVisible({ timeout: 20_000 })
      await expect(page.getByRole('status').filter({ hasText: /^Loading/ })).toHaveCount(0)
      const results = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze()
      const violations = results.violations.map((v) => ({
        id: v.id,
        impact: v.impact,
        nodes: v.nodes.map((n) => ({ target: n.target.join(' '), summary: n.failureSummary })),
      }))
      await testInfo.attach('axe-violations.json', { body: JSON.stringify(violations, null, 2), contentType: 'application/json' })
      expect(violations, JSON.stringify(violations, null, 2)).toEqual([])
    } finally {
      await context.close()
    }
  })
}

async function open(browser: Browser, target: Target, colorScheme: 'light' | 'dark', width?: number) {
  const context = await browser.newContext({
    colorScheme,
    ...(target.signedIn ? { storageState: fixture().storageState } : {}),
    ...(width ? { viewport: { width, height: 800 } } : {}),
  })
  const page = await context.newPage()
  await page.goto(target.path())
  await expect(target.ready(page)).toBeVisible({ timeout: 20_000 })
  await target.prepare?.(page)
  // Let late queries settle (no pending "Loading…" status regions).
  await expect(page.getByRole('status').filter({ hasText: /^Loading/ })).toHaveCount(0)
  return { context, page }
}

for (const colorScheme of ['light', 'dark'] as const) {
  for (const target of targets) {
    test(`NFR-A11Y: ${target.name} has no WCAG 2.2 AA violations (${colorScheme} theme)`, async ({ browser }, testInfo) => {
      const { context, page } = await open(browser, target, colorScheme)
      try {
        expect(await page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(colorScheme === 'dark')
        const results = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze()
        const violations = results.violations.map((v) => ({
          id: v.id,
          impact: v.impact,
          help: v.help,
          nodes: v.nodes.map((n) => ({ target: n.target.join(' '), summary: n.failureSummary })),
        }))
        await testInfo.attach('axe-violations.json', {
          body: JSON.stringify({ page: target.name, colorScheme, violations }, null, 2),
          contentType: 'application/json',
        })
        expect(violations, JSON.stringify(violations, null, 2)).toEqual([])
      } finally {
        await context.close()
      }
    })
  }
}

for (const target of targets) {
  test(`§8: ${target.name} is usable at 375 px (no horizontal page scroll)`, async ({ browser }) => {
    const { context, page } = await open(browser, target, 'light', 375)
    try {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
      // Name the widest offenders, so a failure says where the overflow comes from.
      const offenders = await page.evaluate(() => {
        const width = document.documentElement.clientWidth
        return [...document.querySelectorAll('body *')]
          .filter((e) => e.getBoundingClientRect().right > width + 0.5)
          .slice(-5)
          .map((e) => `<${e.tagName.toLowerCase()} class="${e.getAttribute('class') ?? ''}"> right=${e.getBoundingClientRect().right.toFixed(0)}px`)
      })
      expect(overflow, offenders.join(' | ')).toBeLessThanOrEqual(0)
    } finally {
      await context.close()
    }
  })
}
