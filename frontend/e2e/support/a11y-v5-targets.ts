import { expect, type Page } from '@playwright/test'

import type { A11yFixture } from './a11y-fixture.ts'
import { stepHeading, wizardTo, type A11yTarget } from './a11y-v2-targets.ts'
import { COMPOSITION } from './composition.ts'
import { browseFakeTracker, fakeItemCheckbox } from './v5.ts'

/**
 * The wizard composed with React + Vite, ASP.NET Core and EF Core, and Railway for all three parts, up to the Deployment
 * step: one group window (FR-188, §4.28 #1).
 */
async function railwayGroupWindow(page: Page) {
  await wizardTo(page, 5)
  await page.getByRole('radio', { name: /^Choose the stack/ }).check()
  for (const [layer, option] of [
    ['Frontend', COMPOSITION.frontend.name],
    ['Backend', COMPOSITION.backend.name],
    ['Data', COMPOSITION.data.name],
    ['Version control', COMPOSITION.vcs.name],
  ] as const) {
    await expect(page.getByRole('heading', { level: 3, name: layer, exact: true })).toBeVisible()
    await page.getByRole('radio', { name: option, exact: true }).check()
    await page.getByRole('button', { name: 'Next', exact: true }).click()
  }
  await expect(page.getByRole('heading', { level: 3, name: 'Deployment', exact: true })).toBeVisible()
  await page.getByRole('group', { name: 'Frontend host' }).getByRole('radio', { name: 'Railway', exact: true }).check()
  await page.getByRole('group', { name: 'Backend host' }).getByRole('radio', { name: 'Railway', exact: true }).check()
  await page.getByRole('group', { name: 'Database host' }).getByRole('radio', { name: 'Railway Postgres', exact: true }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(stepHeading(page, 6)).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(stepHeading(page, 7)).toBeVisible()
  const group = page.getByRole('region', { name: /Frontend, backend and database on Railway$/ })
  await expect(group).toBeVisible()
  return group
}

/**
 * SPEC v5 §7 UI rule "Axe covers the Analysis page, the tracker dialog and the group windows": the Analysis page with a
 * blank to answer (Needs your input) and after approval (Deployment setup with the Railway group window), the "Import from
 * tracker" dialog with results and a tick, and the wizard's Deployment step with a Railway group window, open with
 * "Create a new project", and with the database separated (two windows and the caveat).
 */
export function v5Targets(fixture: () => A11yFixture): A11yTarget[] {
  return [
    {
      name: 'analysis (needs your input)',
      path: () => `/projects/${fixture().analysisInputProjectId}/analysis`,
      signedIn: true,
      ready: (p) => p.getByRole('region', { name: 'Needs your input', exact: true }),
      prepare: async (p) => {
        await expect(p.getByTestId('needs-input-backend')).toBeVisible()
        await expect(p.getByTestId('approve-reason')).toBeVisible()
      },
    },
    {
      name: 'analysis (approved, deployment setup with the Railway group window)',
      path: () => `/projects/${fixture().analysisApprovedProjectId}/analysis`,
      signedIn: true,
      ready: (p) => p.getByRole('region', { name: 'Deployment setup', exact: true }),
      prepare: async (p) => {
        await expect(p.getByRole('form', { name: 'Set up the Railway group' })).toBeVisible()
        await expect(p.getByRole('button', { name: 'Continue to Refinement' })).toBeEnabled()
      },
    },
    {
      name: 'tracker browse dialog (results, one item ticked)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: async (p) => {
        await p.getByRole('button', { name: 'Next', exact: true }).click()
        await expect(stepHeading(p, 2)).toBeVisible()
        await p.getByRole('button', { name: 'Import from tracker' }).click()
        const dialog = await browseFakeTracker(p)
        await fakeItemCheckbox(dialog, 2).check()
        await expect(dialog.getByTestId('tracker-selection-status')).toHaveText('1 selected.')
      },
    },
    {
      name: 'wizard Deployment step (Railway group window, creating a project)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: async (p) => {
        const group = await railwayGroupWindow(p)
        await group.getByRole('radio', { name: /^Create a new project/ }).check()
        await expect(group.getByLabel('Railway connection')).toBeVisible()
        await group.getByLabel('Railway connection').selectOption({ index: 1 })
        await expect(group.getByLabel('Name', { exact: true })).toBeVisible()
      },
    },
    {
      name: 'wizard Deployment step (database separated: two windows and the caveat)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: async (p) => {
        const group = await railwayGroupWindow(p)
        await group
          .getByRole('group', { name: 'Deploy parts to separate projects' })
          .getByRole('checkbox', { name: 'Database', exact: true })
          .check()
        await expect(p.getByRole('region', { name: /Frontend and backend on Railway$/ })).toBeVisible()
        await expect(p.getByTestId('wizard-host-database-production').getByTestId('window-caveat')).toBeVisible()
      },
    },
  ]
}
