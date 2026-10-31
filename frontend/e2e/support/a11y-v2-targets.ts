import { expect, type Locator, type Page } from '@playwright/test'

import type { A11yFixture } from './a11y-fixture.ts'

/** A page (in a state) scanned by axe in both themes and checked at 375 px (a11y.spec.ts). */
export interface A11yTarget {
  name: string
  path: () => string
  signedIn: boolean
  /** Visible once the page has loaded its data. */
  ready: (page: Page) => Locator
  prepare?: (page: Page) => Promise<void>
}

const STAGES = ['Refinement', 'Specification', 'Planning', 'Execution', 'Review', 'Release'] as const

const WIZARD_STEPS = ['Source', 'Project idea', 'Repository', 'Version', 'Stack', 'Configuration', 'Deployment', 'Review & create'] as const

export const stepHeading = (page: Page, n: number) =>
  page.getByRole('heading', { name: `Step ${String(n)} of 8: ${WIZARD_STEPS[n - 1] ?? ''}` })

/**
 * FR-5, FR-66, FR-74, FR-88 (§7.2): walks the eight-step wizard from scratch up to `step`, with valid input on every
 * step before it (Local repository; Vercel with the fixture user's Fake connection on the Deployment step).
 */
export async function wizardTo(page: Page, step: number) {
  for (let n = 1; n < step; n++) {
    await expect(stepHeading(page, n)).toBeVisible()
    if (n === 2) {
      await page.getByLabel('Project name').fill('A11y wizard')
      await page.getByLabel('Your idea').fill('Checking every wizard step for accessibility.')
    } else if (n === 3) {
      await page.getByRole('radio', { name: /^Local repository/ }).check()
    } else if (n === 7) {
      await page.getByRole('radio', { name: /^Vercel/ }).check()
      await page.getByLabel('Vercel connection').selectOption({ index: 1 })
      await expect(page.getByLabel('Vercel project name')).not.toHaveValue('')
    }
    await page.getByRole('button', { name: 'Next' }).click()
  }
  await expect(stepHeading(page, step)).toBeVisible()
  if (step === 7) {
    // The Vercel branch of the step (connection picker, project choice) is the part with the most controls.
    await page.getByRole('radio', { name: /^Vercel/ }).check()
    await page.getByLabel('Vercel connection').selectOption({ index: 1 })
  }
}

/** Clicks every "show more" toggle in `scope` that is still collapsed (the label of a toggled one changes). */
async function expandAll(toggles: Locator) {
  for (let guard = 0; guard < 20 && (await toggles.count()) > 0; guard++) {
    await toggles.first().click()
  }
  await expect(toggles).toHaveCount(0)
}

/**
 * NFR-A11Y2 (§8): the v2 pages — every wizard step, the six stage pages of a released project, artifacts, Execution
 * runs with an attempt history open, Review with the charts' table alternatives, deployments, deployment settings and
 * connections.
 */
export function v2Targets(fixture: () => A11yFixture): A11yTarget[] {
  const project = (path: string) => () => `/projects/${fixture().projectId}${path}`
  return [
    ...WIZARD_STEPS.map(
      (title, i): A11yTarget => ({
        name: `wizard step ${String(i + 1)} of 8 (${title})`,
        path: () => '/projects/new',
        signedIn: true,
        ready: (p) => stepHeading(p, 1),
        prepare: (p) => wizardTo(p, i + 1),
      }),
    ),
    ...STAGES.map(
      (stage): A11yTarget => ({
        name: `stage page ${stage}`,
        path: project(`/${stage.toLowerCase()}`),
        signedIn: true,
        // The Release page also has a "Release" panel heading; the stage title comes first.
        ready: (p) => p.getByRole('heading', { level: 2, name: stage, exact: true }).first(),
      }),
    ),
    {
      name: 'artifacts',
      path: project('/artifacts'),
      signedIn: true,
      ready: (p) => p.getByRole('list', { name: 'Artifact types' }),
    },
    {
      name: 'execution runs expanded with attempt history',
      path: project('/execution'),
      signedIn: true,
      ready: (p) => p.getByRole('list', { name: 'Runs' }),
      prepare: async (p) => {
        await expandAll(p.getByRole('list', { name: 'Runs' }).getByRole('button', { expanded: false, name: /·/ }))
        await p.getByRole('button', { name: /^Show attempt history/ }).first().click()
        await expect(p.getByRole('list', { name: /^Attempts of step/ }).first()).toBeVisible()
      },
    },
    {
      name: 'review with the charts as tables',
      path: project('/review'),
      signedIn: true,
      ready: (p) => p.getByRole('heading', { name: /^Latest report: cycle/ }),
      prepare: async (p) => {
        // §8: charts have table alternatives.
        const toTable = p.getByRole('button', { name: 'View as table' })
        await expect(toTable.first()).toBeVisible()
        const charts = await toTable.count()
        await expandAll(toTable)
        await expect(p.getByRole('button', { name: 'View as chart' })).toHaveCount(charts)
        await expect(p.getByRole('region', { name: /table$/ })).toHaveCount(charts)
      },
    },
    {
      name: 'deployments',
      path: project('/deployments'),
      signedIn: true,
      ready: (p) => p.getByRole('heading', { level: 2, name: 'Deployments', exact: true }),
    },
    {
      name: 'settings (deployment)',
      path: project('/settings/deployment'),
      signedIn: true,
      ready: (p) => p.getByRole('heading', { level: 2, name: 'Deployment', exact: true }),
    },
    {
      name: 'connections',
      path: () => '/connections',
      signedIn: true,
      ready: (p) => p.getByRole('heading', { level: 1, name: 'Connections', exact: true }),
    },
  ]
}
