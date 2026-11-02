import { expect, test, type Page } from '@playwright/test'

import { apiJson, registerViaApi } from './support/api.ts'
import { COMPOSITION } from './support/composition.ts'

/**
 * SPEC v5 §8 E2E "Railway ×3 wizard shows one window, then separating the database shows two" (FR-187–FR-189, §4.28
 * #1 and #5). A Local repository allows Railway: its VCS layer is GitHub (R5, R9).
 */

const next = (page: Page) => page.getByRole('button', { name: 'Next', exact: true }).click()

/** Every window of the wizard's Deployment step (a group, a target or a database). */
const windows = (page: Page) => page.locator('[data-testid^="wizard-host-"]')

test('FR-188, FR-189: Railway for all three parts shows one group window; separating the database shows two, with its caveat', async ({ page }) => {
  const context = page.context()
  await registerViaApi(context, 'railway-groups', 'Railway Groups Owner')
  const name = `E2E Railway groups ${String(Date.now())}`

  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await next(page)
  await page.getByLabel('Project name').fill(name)
  await page.getByLabel('Your idea').fill('A recipe box: save recipes through an API and browse them in a web app.')
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 4 of 8: Version' })).toBeVisible()
  await next(page)

  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
  await page.getByRole('radio', { name: /^Choose the stack/ }).check()
  for (const [layer, option] of [
    ['Frontend', COMPOSITION.frontend.name],
    ['Backend', COMPOSITION.backend.name],
    ['Data', COMPOSITION.data.name],
    ['Version control', COMPOSITION.vcs.name],
  ] as const) {
    await expect(page.getByRole('heading', { level: 3, name: layer, exact: true })).toBeVisible()
    await page.getByRole('radio', { name: option, exact: true }).check()
    await next(page)
  }
  await expect(page.getByRole('heading', { level: 3, name: 'Deployment', exact: true })).toBeVisible()
  await page.getByRole('group', { name: 'Frontend host' }).getByRole('radio', { name: 'Railway', exact: true }).check()
  await page.getByRole('group', { name: 'Backend host' }).getByRole('radio', { name: 'Railway', exact: true }).check()
  await page.getByRole('group', { name: 'Database host' }).getByRole('radio', { name: 'Railway Postgres', exact: true }).check()
  await expect(page.getByRole('group', { name: 'Database host' }).getByRole('radio', { name: 'Railway Postgres', exact: true })).toBeChecked()
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()

  // FR-188 (§4.28 #1): one window where v4 had three, covering both database environments.
  const group = page.getByRole('region', { name: /Frontend, backend and database on Railway$/ })
  await expect(group).toBeVisible()
  await expect(windows(page)).toHaveCount(1)
  await expect(group).toContainText('This covers both the production and the preview database.')
  await expect(group.getByRole('radio', { name: /^Set up later/ })).toBeChecked()

  // FR-189 (§4.28 #5): ticking the database takes it out of the group into its own window, with the R8 caveat as text.
  await group
    .getByRole('group', { name: 'Deploy parts to separate projects' })
    .getByRole('checkbox', { name: 'Database', exact: true })
    .check()
  await expect(page.getByRole('region', { name: /Frontend and backend on Railway$/ })).toBeVisible()
  await expect(windows(page)).toHaveCount(2)
  const database = page.getByTestId('wizard-host-database-production')
  await expect(database).toBeVisible()
  await expect(database.getByTestId('window-caveat')).toContainText('Separate Railway project: no private network, public connection, higher latency')

  // The choice is stored on the composition (`deploy.separateParts`).
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  const stack = await apiJson<{ composition: { deploy: { frontendHost: string | null; backendHost: string | null; databaseHost: string | null; separateParts?: string[] } } | null }>(
    context,
    'GET',
    `/api/projects/${projectId}/stack`,
  )
  expect(stack.composition?.deploy).toMatchObject({
    frontendHost: 'deploy-railway',
    backendHost: 'deploy-railway',
    databaseHost: 'railway-postgres',
    separateParts: ['database'],
  })
})
