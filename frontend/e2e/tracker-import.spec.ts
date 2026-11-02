import { expect, test } from '@playwright/test'

import { apiJson, registerViaApi, waitForDraft } from './support/api.ts'
import { AGENT_TIMEOUT } from './support/v2.ts'
import { browseFakeTracker, createLinearConnection, fakeItemCheckbox } from './support/v5.ts'

/**
 * SPEC v5 §8 E2E "import items in the wizard, then see them as refinement inputs" (Fake LLM, Local Git, Fake tracker):
 * FR-169 browse and tick, FR-171 chips and the required idea, FR-170 the locked snapshot, FR-172 the refinement run's input.
 */

interface RunDto {
  agent: string
  inputArtifacts: { type: string; version: number }[]
}

test('FR-169, FR-170, FR-171, FR-172: tracker items picked in the wizard become Refinement source items and the refinement run input', async ({ page }) => {
  test.setTimeout(6 * 60_000)
  const context = page.context()
  await registerViaApi(context, 'tracker-import', 'Tracker Import Owner')
  await createLinearConnection(context)
  const name = `E2E Tracker import ${String(Date.now())}`

  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 2 of 8: Project idea' })).toBeVisible()
  await page.getByLabel('Project name').fill(name)

  // FR-169: Import from tracker → the Fake container → tick two items → Add.
  await page.getByRole('button', { name: 'Import from tracker' }).click()
  const dialog = await browseFakeTracker(page)
  await fakeItemCheckbox(dialog, 2).check()
  await fakeItemCheckbox(dialog, 3).check()
  await expect(dialog.getByTestId('tracker-selection-status')).toHaveText('2 selected.')
  await dialog.getByRole('button', { name: 'Add 2 items' }).click()
  await expect(dialog).toBeHidden()

  // FR-171: the picks show as removable chips.
  const chips = page.getByRole('list', { name: 'Items to import' })
  await expect(chips.getByRole('listitem')).toHaveCount(2)
  await expect(chips.getByRole('button', { name: 'Remove FAKE-2' })).toBeVisible()
  await expect(chips.getByRole('button', { name: 'Remove FAKE-3' })).toBeVisible()

  // FR-171 (D-V5-6): the idea stays required; imported items only add to it.
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByText('Say what you want to build. Imported items add to this.').first()).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Step 2 of 8: Project idea' })).toBeVisible()
  await page.getByLabel('Your idea').fill('A board for small teams to track their work, built from the picked Linear issues.')
  await page.getByRole('button', { name: 'Next', exact: true }).click()

  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 4 of 8: Version' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()
  await page.getByRole('radio', { name: /^Strict/ }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')

  // FR-170: the import job writes the locked `source_items` v1; Refinement's Source items panel lists the two items.
  await page.goto(`/projects/${projectId}/refinement`)
  const sources = page.getByRole('region', { name: 'Source items', exact: true })
  const imported = sources.getByRole('listitem', { name: 'Linear import, Imported' })
  await expect(imported).toBeVisible({ timeout: AGENT_TIMEOUT })
  await expect(imported).toContainText('2 items')
  const items = imported.getByRole('list', { name: 'Items of this Linear import' })
  await expect(items.getByTestId('source-item')).toHaveCount(2)
  await expect(items).toContainText('FAKE-2')
  await expect(items).toContainText('Fake item 3')
  const snapshot = await apiJson<{ version: number; status: string }>(context, 'GET', `/api/projects/${projectId}/artifacts/source_items/latest`)
  expect(snapshot).toMatchObject({ version: 1, status: 'locked' })

  // FR-172: the first refinement round reads the snapshot; its run lists source_items v1 as an input.
  await page.getByRole('button', { name: 'Start refinement' }).click()
  await waitForDraft(context, projectId, 'project_request', AGENT_TIMEOUT)
  const runs = await apiJson<{ items: RunDto[] }>(context, 'GET', `/api/projects/${projectId}/runs?limit=50`)
  const refinement = runs.items.filter((r) => r.agent === 'idea_refinement')
  expect(refinement.length).toBeGreaterThan(0)
  expect(refinement[0]?.inputArtifacts).toContainEqual({ type: 'source_items', version: 1 })

  // The same, as the run's detail sheet shows it.
  await page.goto(`/projects/${projectId}/runs`)
  await expect(page.getByRole('heading', { level: 2, name: 'Agent runs' })).toBeVisible()
  await page.getByLabel('Agent', { exact: true }).selectOption({ label: 'Idea refinement' })
  await page.getByRole('button', { name: /^Idea refinement run, started / }).first().click()
  const sheet = page.getByRole('dialog', { name: 'Idea refinement run' })
  await expect(sheet.getByRole('link', { name: 'Source items v1' })).toBeVisible()
})
