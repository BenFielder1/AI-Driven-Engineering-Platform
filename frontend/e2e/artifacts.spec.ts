import { expect, test } from '@playwright/test'

import { apiJson, createProjectViaApi, registerViaApi, waitForDraft } from './support/api.ts'

/**
 * FR-18 (edit the latest version as a new draft), FR-21 (side-by-side diff between two versions), FR-17 (approve
 * locks it), and the editor's validation error. The agent's draft is v1 (Fake provider); the human edit is v2.
 */
test('FR-18, FR-21, FR-17: edit as a new draft, compare v1 with v2, then approve v2', async ({ page, context }) => {
  test.setTimeout(3 * 60_000)
  await registerViaApi(context, 'artifacts')
  const project = await createProjectViaApi(context, `E2E Artifact edits ${String(Date.now())}`)
  await apiJson(context, 'POST', `/api/projects/${project.id}/refinement/run`, { answers: [] })
  await waitForDraft(context, project.id, 'project_request')

  await page.goto(`/projects/${project.id}/artifacts/project_request`)
  await expect(page.getByRole('heading', { level: 2, name: 'Project request' })).toBeVisible()
  await expect(page.getByRole('table').getByRole('link', { name: 'v1' })).toBeVisible()
  await expect(page.getByText('There is only one version so far. Comparing needs at least two.')).toBeVisible()

  // Validation: an empty draft can't be saved.
  await page.getByRole('button', { name: 'Edit as new draft' }).click()
  const editor = page.getByRole('form', { name: 'Edit project request' })
  const markdown = editor.getByLabel('Markdown')
  await expect(markdown).not.toHaveValue('')
  const original = await markdown.inputValue()
  await expect(editor.getByText('Saving supersedes the open draft v1')).toBeVisible()
  await markdown.fill('   ')
  await editor.getByRole('button', { name: 'Save as new draft' }).click()
  await expect(editor.getByText('Write some Markdown before saving.').first()).toBeVisible()

  // FR-18: save an edit → v2 draft with parent v1; v1 is superseded.
  const addedLine = `- Added by an E2E edit ${String(Date.now())}`
  await markdown.fill(`${original.trimEnd()}\n${addedLine}\n`)
  await editor.getByRole('button', { name: 'Save as new draft' }).click()
  await expect(page.getByText('Saved as draft v2. An Owner can now approve or reject it.')).toBeVisible()
  await expect(page.getByText('Parent version', { exact: true }).locator('..').getByText('v1')).toBeVisible()
  const history = page.getByRole('region', { name: 'History' })
  await expect(history.getByRole('row', { name: /^v2/ }).getByText('Draft')).toBeVisible()
  await expect(history.getByRole('row', { name: /^v1/ }).getByText('Superseded')).toBeVisible()

  // FR-21: pick both versions and see the added line in the side-by-side diff.
  const compare = page.getByRole('form', { name: 'Compare versions' })
  await compare.getByLabel('From version').selectOption('1')
  await compare.getByLabel('To version').selectOption('2')
  await compare.getByRole('button', { name: 'Compare' }).click()
  const diff = page.getByRole('table', { name: /^Changes from v1 to v2: 1 line added, 0 lines removed$/ })
  await expect(diff).toBeVisible()
  await expect(diff.locator('[data-change]').filter({ hasText: addedLine })).toBeVisible()

  // Same version on both sides is refused client-side.
  await compare.getByLabel('From version').selectOption('2')
  await compare.getByRole('button', { name: 'Compare' }).click()
  await expect(compare.getByText('Pick two different versions.')).toBeVisible()

  // FR-17: approve v2 → locked ("Approved"), no more decision buttons.
  await page.getByRole('button', { name: 'Approve', exact: true }).click()
  const dialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Approve project request v2?' }) })
  await dialog.getByRole('button', { name: 'Approve v2' }).click()
  await expect(page.getByText('v2 approved and locked.')).toBeVisible()
  await expect(history.getByRole('row', { name: /^v2/ }).getByText('Approved')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
})
