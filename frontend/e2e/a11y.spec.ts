import { readFileSync } from 'node:fs'

import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Browser, type Locator, type Page } from '@playwright/test'

import { A11Y_FIXTURE, type A11yFixture } from './support/a11y-fixture.ts'

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
    ready: (p) => p.getByRole('heading', { name: 'Step 1 of 4: Project idea' }),
    prepare: async (p) => {
      await p.getByRole('button', { name: 'Next' }).click()
      await expect(p.getByText('Check this step')).toBeVisible()
    },
  },
  {
    name: 'wizard (configuration step)',
    path: () => '/projects/new',
    signedIn: true,
    ready: (p) => p.getByRole('heading', { name: 'Step 1 of 4: Project idea' }),
    prepare: async (p) => {
      await p.getByLabel('Project name').fill('A11y wizard')
      await p.getByLabel('Your idea').fill('Checking the configuration step for accessibility.')
      await p.getByRole('button', { name: 'Next' }).click()
      await expect(p.getByRole('heading', { name: 'Step 2 of 4: Configuration' })).toBeVisible()
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
    path: () => `/projects/${fixture().projectId}/settings`,
    signedIn: true,
    ready: (p) => p.getByRole('tab', { name: 'General' }),
  },
  {
    name: 'settings (danger zone)',
    path: () => `/projects/${fixture().projectId}/settings?tab=danger`,
    signedIn: true,
    ready: (p) => p.getByRole('button', { name: 'Roll back…' }),
  },
  {
    name: 'notifications',
    path: () => '/notifications',
    signedIn: true,
    ready: (p) => p.getByRole('list', { name: 'Notifications' }),
  },
]

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
