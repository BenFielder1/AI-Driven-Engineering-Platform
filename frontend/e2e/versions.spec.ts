import { expect, test } from '@playwright/test'

import { api, apiJson, projectState, registerViaApi } from './support/api.ts'
import {
  AGENT_TIMEOUT,
  advanceFromHeader,
  approveInPlace,
  approveStageGate,
  createVercelConnection,
  currentPhase,
  drivePlanningToPreRelease,
  driveRefinementToPlanning,
  fakeUrl,
  headerBadges,
  openStage,
  waitForDeployment,
  waitForPhase,
} from './support/v2.ts'

/**
 * NFR-TEST2 (SPEC §8 "two-version flow", §1 success criteria) with Fake LLM + Local Git + Fake deployments:
 * create 0.1.0 from scratch in the wizard with Vercel (Fake) deployment (FR-66, FR-88) → lifecycle → release tagged
 * v0.1.0 with a READY production deployment, "live on Vercel" (FR-64, FR-91) → Start new version 0.2.0 (FR-60) →
 * Repository Analyst summary on Refinement (FR-70) → every stage page approved in place (FR-55) with the iteration
 * spec's "Changes in this version" filter (§4.13) → execution → review cycles → rollback to a 0.2.0 checkpoint before
 * releasing (FR-63) → release 0.2.0 → version switcher shows 0.1.0 read-only with its runs and review cycles
 * (FR-56, FR-57, FR-62) → the released version is closed for rollback (FR-63) → Deployments page with both production
 * deployments (FR-91).
 */

test('NFR-TEST2, FR-55–FR-66, FR-90, FR-91: 0.1.0 is released live on Vercel, 0.2.0 is developed on its stage pages, rolled back once and released', async ({
  page,
}) => {
  test.setTimeout(25 * 60_000)
  const context = page.context()
  const projectName = `E2E Two versions ${String(Date.now())}`
  await registerViaApi(context, 'versions', 'Versions Owner')
  await createVercelConnection(context, 'Fake Vercel')

  // FR-5/FR-66/FR-88: the eight-step wizard, from scratch, Local, 0.1.0, no stack (general), strict, Vercel (Fake) deployment.
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByLabel('Project name').fill(projectName)
  await page.getByLabel('Your idea').fill('A ticket tracker API for small support teams: create, assign and close tickets.')
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByLabel('Initial version')).toHaveValue('0.1.0')
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('radio', { name: /^No stack \(general\)/ })).toBeChecked()
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByRole('radio', { name: /^Strict/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()
  await page.getByRole('radio', { name: /^Vercel/ }).check()
  await expect(page.getByText('Vercel deploys from GitHub')).toBeVisible()
  const connectionSelect = page.getByLabel('Vercel connection')
  await expect(connectionSelect.locator('option')).toHaveCount(2)
  await connectionSelect.selectOption({ index: 1 })
  await expect(page.getByRole('radio', { name: /^Create a new Vercel project/ })).toBeChecked()
  await expect(page.getByLabel('Vercel project name')).not.toHaveValue('')
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Deployment' }).getByText('Vercel', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name: projectName })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  const created = await apiJson<{ deploymentsEnabled: boolean; developmentVersion: string; releasedVersion: string | null }>(
    context,
    'GET',
    `/api/projects/${projectId}`,
  )
  expect(created).toMatchObject({ deploymentsEnabled: true, developmentVersion: '0.1.0', releasedVersion: null })
  await expect(headerBadges(page, projectName)).toContainText('Not released')
  await expect(headerBadges(page, projectName)).toContainText('In development 0.1.0')

  // 0.1.0 through the API (the UI journey of a first version is lifecycle.spec.ts) up to the pre-release gate.
  await driveRefinementToPlanning(context, projectId)
  await drivePlanningToPreRelease(context, projectId)

  // FR-90: the Review page shows the READY preview of the version branch head at the Fake URL.
  const preview = await waitForDeployment(context, projectId, 'preview', 'ready', { version: '0.1.0' })
  expect(preview.url).toBe(fakeUrl(preview.gitSha))
  expect(preview.gitRef).toMatch(/^aide\/.+\/v0\.1\.0$/)
  await page.reload()
  await openStage(page, 'Review')
  // The stage's Preview deployment panel (its deep-link anchor), and the same preview as a review input in the report.
  const previewSection = page.locator('#review-preview-deployment')
  const previewHost = new RegExp(preview.url?.replace('https://', '') ?? 'missing-url')
  await expect(previewSection.getByTestId('deployment-status').first()).toHaveText('Ready')
  await expect(previewSection.getByRole('link', { name: previewHost })).toBeVisible()
  await expect(page.getByTestId('report-preview').getByRole('link', { name: previewHost })).toBeVisible()

  // FR-64, FR-91: approve the release on the Release page; the tag is the initial version, production goes live.
  await openStage(page, 'Release')
  await approveStageGate(page, 'Release approval')
  const releasePanel = page.locator('#release')
  await expect(releasePanel.getByText('Released 0.1.0', { exact: true })).toBeVisible({ timeout: AGENT_TIMEOUT })
  await expect(releasePanel.getByText('v0.1.0', { exact: true })).toBeVisible()
  const production = await waitForDeployment(context, projectId, 'production', 'ready', { version: '0.1.0' })
  expect(production.url).toBe(fakeUrl(production.gitSha))
  const live = page.getByTestId('production-live')
  await expect(live).toContainText('Released 0.1.0 · live on Vercel', { timeout: 10_000 })
  await expect(live.getByRole('link', { name: new RegExp(production.url?.replace('https://', '') ?? '') })).toBeVisible()
  await expect(headerBadges(page, projectName)).toContainText('Released 0.1.0')
  await expect(headerBadges(page, projectName)).not.toContainText('In development')

  // FR-63, D-V2-3: the released 0.1.0 is closed for rollback.
  const blockedAfter010 = await api(context, 'POST', `/api/projects/${projectId}/rollback`, {
    targetCheckpoint: 1,
    reason: 'E2E: 0.1.0 is released',
  })
  expect(blockedAfter010.status()).toBe(409)
  const problem010 = (await blockedAfter010.json()) as { errorCode: string; details?: string[] }
  expect(problem010.errorCode).toBe('ROLLBACK_BLOCKED')
  expect(problem010.details).toContain('reason: version_released')

  // FR-60: Start new version on the Release page (prefilled with the next minor).
  await page.getByRole('button', { name: 'Start new version' }).click()
  const start = page.getByRole('dialog', { name: 'Start a new version' })
  await expect(start.getByLabel('New version')).toHaveValue('0.2.0')
  const request = 'Add ticket priorities (low, normal, urgent) and sort the ticket list by priority.'
  await start.getByLabel('What should this version add or fix?').fill(request)
  await start.getByRole('button', { name: 'Start development' }).click()
  await expect(start).toBeHidden()
  await expect(currentPhase(page)).toContainText('Refinement')
  await expect(headerBadges(page, projectName)).toContainText('Released 0.1.0')
  await expect(headerBadges(page, projectName)).toContainText('In development 0.2.0')

  // FR-55, FR-70: Refinement shows the request and the Repository Analyst's locked codebase summary.
  await openStage(page, 'Refinement')
  await expect(page.getByRole('region', { name: 'Request', exact: true })).toContainText(request)
  const summary = page.getByRole('region', { name: 'Codebase summary', exact: true })
  await expect(summary.getByText('Locked', { exact: true })).toBeVisible({ timeout: 2 * AGENT_TIMEOUT })
  await expect(summary.getByRole('heading', { name: 'Overview' })).toBeVisible()
  await page.getByRole('button', { name: 'Start refinement' }).click()
  await approveInPlace(page, 'Project request')
  await advanceFromHeader(page, 'Specification')
  await expect(currentPhase(page)).toContainText('Specification')

  // FR-55, §4.13: the iteration spec, approved in place, with the "Changes in this version" filter.
  await openStage(page, 'Specification')
  await approveInPlace(page, 'Technical specification')
  const changes = page.getByRole('region', { name: 'Changes in this version' })
  await expect(changes).toBeVisible()
  await changes.getByRole('button', { name: /^New \(\d+\)$/ }).click()
  await expect(changes.getByRole('button', { name: /^New \(\d+\)$/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(changes.getByRole('list', { name: 'Requirements in this filter' }).getByRole('listitem')).not.toHaveCount(0)
  await expect(page.getByRole('navigation', { name: 'Section outline' })).toBeVisible()
  await advanceFromHeader(page, 'Planning')
  await approveStageGate(page, 'Specification approval')
  await expect(currentPhase(page)).toContainText('Planning')

  // FR-55, FR-76: the plan's step table, approved in place; the plan gate on the Planning page.
  await openStage(page, 'Planning')
  await approveInPlace(page, 'Implementation plan')
  await expect(page.getByRole('table', { name: /^Plan steps with their specialist/ })).toBeVisible()
  await advanceFromHeader(page, 'Execution')
  await approveStageGate(page, 'Plan approval')
  await expect(currentPhase(page)).toContainText('Execution')

  // FR-57: the development run executes on the Execution page.
  await openStage(page, 'Execution')
  await page.getByRole('button', { name: 'Start execution' }).click()
  await expect(page.getByText(/^(\d+) of \1 steps completed$/)).toBeVisible({ timeout: 2 * AGENT_TIMEOUT })

  // FR-56: review cycle 1 asks for another cycle; its optimisation plan is approved in place on the Review page.
  await waitForPhase(context, projectId, 'review')
  await openStage(page, 'Review')
  await approveInPlace(page, 'Optimisation plan')
  await expect.poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT }).toBe(
    'release_eligible',
  )
  const cycles = page.getByRole('table', { name: /^Review cycles, newest first/ })
  await expect(cycles.getByRole('row')).toHaveCount(3, { timeout: AGENT_TIMEOUT })

  // FR-63 (rollback before release): roll back to 0.2.0's Planning checkpoint; only 0.2.0 checkpoints are offered.
  await page.goto(`/projects/${projectId}/settings/danger`)
  await page.getByRole('button', { name: 'Roll back…' }).click()
  const rollback = page.getByRole('alertdialog', { name: 'Roll back the project' })
  const targetSelect = rollback.getByLabel('Checkpoint to restore')
  await expect(targetSelect.locator('option')).not.toHaveCount(1)
  const optionTexts = (await targetSelect.locator('option').allTextContents()).slice(1)
  expect(optionTexts.length).toBeGreaterThan(1)
  for (const text of optionTexts) {
    expect(text).toMatch(/^0\.2\.0 · checkpoint \d+ · /)
  }
  const planning = optionTexts.find((t) => t.endsWith(' · Planning')) ?? ''
  const target = /checkpoint (\d+)/.exec(planning)?.[1] ?? ''
  expect(target).not.toBe('')
  const before = (await projectState(context, projectId)).checkpoint
  await targetSelect.selectOption({ label: planning })
  await rollback.getByLabel('Reason').fill('E2E: re-plan 0.2.0 before releasing it')
  await rollback.getByRole('button', { name: 'Continue' }).click()
  await rollback.getByRole('button', { name: `Roll back to checkpoint ${target}` }).click()
  await expect(page.getByText(`Rolled back from 0.2.0 · checkpoint ${String(before)} to checkpoint ${target}`)).toBeVisible()
  await expect(page.getByText('The project is back in Planning.')).toBeVisible()
  const rolledBack = await projectState(context, projectId)
  expect(rolledBack).toMatchObject({ phase: 'planning', status: 'rolled_back', checkpoint: Number(target), developmentVersion: '0.2.0' })
  const gatesAfterRollback = await apiJson<{ status: string }[]>(context, 'GET', `/api/projects/${projectId}/gates`)
  expect(gatesAfterRollback.filter((g) => g.status === 'pending')).toEqual([])

  // Continue 0.2.0 to the pre-release gate again, then release it on the Release page.
  await drivePlanningToPreRelease(context, projectId)
  await page.goto(`/projects/${projectId}/release`)
  await approveStageGate(page, 'Release approval')
  await expect(page.locator('#release').getByText('Released 0.2.0', { exact: true })).toBeVisible({
    timeout: AGENT_TIMEOUT,
  })
  await waitForDeployment(context, projectId, 'production', 'ready', { version: '0.2.0' })
  await expect(page.getByTestId('production-live')).toContainText('Released 0.2.0 · live on Vercel', { timeout: 10_000 })
  await expect(headerBadges(page, projectName)).toContainText('Released 0.2.0')
  await expect(headerBadges(page, projectName)).not.toContainText('In development')
  const versions = await apiJson<{ version: string; status: string; tag: string | null }[]>(context, 'GET', `/api/projects/${projectId}/versions`)
  expect(versions.map((v) => [v.version, v.status, v.tag]).sort()).toEqual([
    ['0.1.0', 'released', 'v0.1.0'],
    ['0.2.0', 'released', 'v0.2.0'],
  ])

  // FR-57: 0.2.0's execution history keeps every run, including the review-fix run of the rolled-back attempt.
  await openStage(page, 'Execution')
  const runs = page.getByRole('list', { name: 'Runs' })
  await expect(runs.getByRole('heading', { name: /· Development run$/ }).first()).toBeVisible()
  await expect(runs.getByRole('heading', { name: /· Review-fix run, cycle 1$/ }).first()).toBeVisible()

  // FR-62: the version switcher opens 0.1.0 read-only (?release=0.1.0) with its runs, cycles and release.
  await page.getByRole('button', { name: /^Release version: 0\.2\.0/ }).click()
  await page.getByRole('menuitem', { name: /^0\.1\.0/ }).click()
  await expect(page).toHaveURL(/[?&]release=0\.1\.0/)
  await expect(page.getByTestId('read-only-banner')).toHaveText(/Viewing 0\.1\.0 \(released\)\. Read-only/)
  await expect(page.getByRole('list', { name: 'Runs' }).getByRole('heading', { name: /· Development run$/ })).toHaveCount(1)
  await expect(page.getByRole('list', { name: 'Runs' }).getByRole('heading', { name: /· Review-fix run, cycle 1$/ })).toHaveCount(1)
  await expect(page.getByRole('button', { name: /^(Start|Resume|Pause) execution$/ })).toHaveCount(0)
  await openStage(page, 'Review')
  await expect(page).toHaveURL(/[?&]release=0\.1\.0/)
  await expect(page.getByRole('table', { name: /^Review cycles, newest first/ }).getByRole('row')).toHaveCount(3)
  await expect(page.getByRole('button', { name: 'Run review' })).toHaveCount(0)
  await openStage(page, 'Specification')
  await expect(page.getByTestId('read-only-banner')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Technical specification' }).getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Edit as new draft' })).toHaveCount(0)
  await openStage(page, 'Release')
  await expect(page.getByRole('heading', { name: 'Release attempts' })).toBeVisible()
  await expect(page.getByText('v0.1.0', { exact: true }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Back to the current version' }).click()
  await expect(page.getByTestId('read-only-banner')).toBeHidden()

  // FR-63, D-V2-3: the released 0.2.0 is closed: the UI says so and the API refuses.
  await page.goto(`/projects/${projectId}/settings/danger`)
  const closed = page.getByRole('button', { name: 'Roll back…' })
  await expect(closed).toHaveAttribute('aria-disabled', 'true')
  await expect(closed).toHaveAccessibleDescription(/Version 0\.2\.0 is released and closed/)
  const blocked = await api(context, 'POST', `/api/projects/${projectId}/rollback`, { targetCheckpoint: 1, reason: 'E2E: must be refused' })
  expect(blocked.status()).toBe(409)
  const problem = (await blocked.json()) as { errorCode: string; details?: string[] }
  expect(problem.errorCode).toBe('ROLLBACK_BLOCKED')
  expect(problem.details).toContain('reason: version_released')

  // FR-91: the Deployments page lists both production deployments; 0.2.0's is live; an earlier one can be promoted.
  await page.goto(`/projects/${projectId}/deployments`)
  await page.getByLabel('Target').selectOption('production')
  const table = page.getByRole('table', { name: /^Deployments, newest first/ })
  await expect(table.getByRole('row')).toHaveCount(3)
  const liveRow = table.getByRole('row').filter({ has: page.getByTestId('deployment-live') })
  await expect(liveRow).toHaveCount(1)
  await expect(liveRow).toContainText('0.2.0')
  await table.getByRole('button', { name: /^Promote 0\.1\.0 / }).click()
  const promote = page.getByRole('alertdialog', { name: 'Promote 0.1.0 to production?' })
  await promote.getByRole('button', { name: 'Promote to production' }).click()
  await expect(page.getByText(/^Promoting 0\.1\.0 \(/)).toBeVisible()
})
