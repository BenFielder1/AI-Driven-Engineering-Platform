import { expect, test } from '@playwright/test'

import { apiJson, projectState, registerViaApi } from './support/api.ts'
import {
  PRESET_ID,
  PRESET_NAME,
  PRESET_PLAN_AGENTS,
  PRESET_SECTIONS,
  addFailBuildMarkerToPlan,
  agentRuns,
  createPresetProjectInWizard,
  drivePlanToExecutionStarted,
  driveSpecToPlanDraft,
  driveToSpecDraft,
  waitForGateOrEscalation,
} from './support/preset.ts'
import {
  AGENT_TIMEOUT,
  LIVE_BOUND_MS,
  approveDraftViaApi,
  approveInPlace,
  createVercelConnection,
  deployments,
  fakeUrl,
  waitForDeployment,
  waitForPhase,
} from './support/v2.ts'

/**
 * NFR-TEST2 (SPEC §8 "the preset path with nextjs-frontend and the Fake provider's scripted specialist outputs") on the
 * E2E deployment (Fake LLM + FakePresetScripts, Local Git, Fake deployments §4.15):
 * wizard Stack = nextjs-frontend + Vercel (FR-74, FR-88) → preset chip → spec with the preset's sections (FR-75) →
 * plan steps with specialist agents and covers (FR-76) → code_generator runs with agent keys and the pinned preset
 * (FR-74, FR-76) → Review: the preview turns READY live, "preview ready" in the bell (FR-90, FR-92) → review completes
 * (FR-77 review with the preset). A second test drives the preview build-failure path (FR-90).
 */

const IDEA = 'A recipe finder web app: search recipes from a public recipes API and save favourites in the browser.'

test('NFR-TEST2, FR-74–FR-77, FR-90, FR-92: a nextjs-frontend project runs its specialist plan and its preview turns ready live', async ({
  page,
}) => {
  test.setTimeout(15 * 60_000)
  const context = page.context()
  const projectName = `E2E Preset ${String(Date.now())}`
  await registerViaApi(context, 'preset', 'Preset Owner')
  await createVercelConnection(context, 'Fake Vercel')

  // FR-74: the wizard's Stack step, then the preset chip in the project header.
  const projectId = await createPresetProjectInWizard(page, projectName, IDEA)
  const header = page.getByRole('heading', { level: 1, name: projectName }).locator('xpath=ancestor::header[1]')
  await expect(header.getByTestId('preset-chip')).toHaveText(new RegExp(`^Preset ${PRESET_NAME.replace('.', '\\.')} \\d+\\.\\d+\\.\\d+$`))
  const project = await apiJson<{ presetId: string | null; presetVersion: string | null; deploymentsEnabled: boolean }>(
    context,
    'GET',
    `/api/projects/${projectId}`,
  )
  expect(project).toMatchObject({ presetId: PRESET_ID, deploymentsEnabled: true })
  const presetRef = `${PRESET_ID}@${project.presetVersion ?? ''}`

  // FR-75: the spec has the preset's extra sections; approved in place on the Specification page.
  await driveToSpecDraft(context, projectId)
  await page.goto(`/projects/${projectId}/specification`)
  const spec = page.getByRole('region', { name: 'Technical specification', exact: true })
  for (const title of PRESET_SECTIONS) {
    await expect(spec.getByRole('heading', { name: title, exact: true })).toBeVisible({ timeout: AGENT_TIMEOUT })
  }
  await approveInPlace(page, 'Technical specification')

  // FR-76: plan steps carry specialist agents (scaffolder first on the empty repo) and covers; the plan is valid.
  const planDraft = await driveSpecToPlanDraft(context, projectId)
  const planArtifact = await apiJson<{ warnings: { code: string }[] }>(
    context,
    'GET',
    `/api/projects/${projectId}/artifacts/implementation_plan/latest`,
  )
  expect(planArtifact.warnings.map((w) => w.code)).not.toContain('INVALID_PLAN')
  expect(planDraft.status).toBe('draft')
  await page.goto(`/projects/${projectId}/planning`)
  const steps = page.getByRole('table', { name: /^Plan steps with their specialist/ })
  await expect(steps).toBeVisible({ timeout: AGENT_TIMEOUT })
  const rows = steps.locator('tbody tr')
  await expect(rows).toHaveCount(PRESET_PLAN_AGENTS.length)
  for (const [i, key] of PRESET_PLAN_AGENTS.entries()) {
    await expect(rows.nth(i).getByText(key, { exact: true })).toBeVisible()
  }
  // Covers column (6th): the feature steps cover requirement ids.
  await expect(rows.filter({ hasText: 'api-engineer' }).locator('td').nth(4)).toHaveText(/FR-\d+/)
  await expect(page.getByText('INVALID_PLAN')).toHaveCount(0)
  await approveInPlace(page, 'Implementation plan')

  // Execution; open the Review page as soon as the phase moves so the preview's READY arrives live (no reload).
  await drivePlanToExecutionStarted(context, projectId)
  await waitForPhase(context, projectId, 'review', 3 * AGENT_TIMEOUT)
  await page.goto(`/projects/${projectId}/review`)
  await expect(page.getByRole('heading', { level: 2, name: 'Review', exact: true })).toBeVisible()
  const previewSection = page.locator('#review-preview-deployment')
  const pill = previewSection.getByTestId('deployment-status').first()
  await expect(pill).toBeVisible({ timeout: AGENT_TIMEOUT })
  const firstPreview = (await deployments(context, projectId, 'preview'))[0]
  expect(firstPreview, 'a preview deployment exists once execution completed').toBeDefined()
  expect(firstPreview?.status, 'the preview is still building when the Review page opens').not.toBe('ready')

  // FR-90, FR-92: READY on the API → the page shows it within 2 s without a reload, and the bell gets "preview ready".
  const ready = await waitForDeployment(context, projectId, 'preview', 'ready', { intervals: [200] })
  await expect(pill).toHaveText('Ready', { timeout: LIVE_BOUND_MS })
  expect(ready.url).toBe(fakeUrl(ready.gitSha))
  await expect(previewSection.getByRole('link', { name: new RegExp(ready.url?.replace('https://', '') ?? 'missing') })).toBeVisible()
  const bell = page.getByRole('button', { name: /^Notifications, \d+ unread$/ })
  await expect(bell).not.toHaveAccessibleName('Notifications, 0 unread')
  await bell.click()
  const latest = page.getByRole('list', { name: 'Latest notifications' })
  await expect(latest.getByText(new RegExp(`^${projectName}: preview .*is ready$`))).toBeVisible()
  await page.keyboard.press('Escape')

  // FR-74, FR-76: every code_generator run is a specialist of the plan and records the pinned preset.
  const codeRuns = await agentRuns(context, projectId, 'code_generator')
  expect(codeRuns.length).toBeGreaterThanOrEqual(PRESET_PLAN_AGENTS.length)
  expect(new Set(codeRuns.map((r) => r.agentKey))).toEqual(new Set(PRESET_PLAN_AGENTS))
  for (const r of codeRuns) {
    expect(`${r.presetId ?? ''}@${r.presetVersion ?? ''}`).toBe(presetRef)
  }
  await page.goto(`/projects/${projectId}/runs`)
  await page.getByLabel('Agent', { exact: true }).selectOption({ label: 'Code generator' })
  const runsTable = page.getByRole('table', { name: /^Agent runs, newest first/ })
  const scaffolderRow = runsTable.getByRole('row').filter({ hasText: 'project-scaffolder' })
  await expect(scaffolderRow.first()).toBeVisible()
  await expect(scaffolderRow.first().getByTestId('run-preset')).toHaveText(presetRef)

  // FR-77: the review (with the preset's checklist) completes: cycle 1 asks for a cycle, cycle 2 is release eligible.
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT })
    .not.toBeNull()
  if ((await projectState(context, projectId)).review.latestDecision === 'review_required') {
    await approveDraftViaApi(context, projectId, 'optimization_plan')
  }
  await waitForGateOrEscalation(context, projectId, 'pre_release', 4 * AGENT_TIMEOUT)
  expect((await projectState(context, projectId)).review.latestDecision).toBe('release_eligible')
  const reviewRuns = await agentRuns(context, projectId, 'code_review')
  for (const r of reviewRuns) {
    expect(`${r.presetId ?? ''}@${r.presetVersion ?? ''}`).toBe(presetRef)
  }
  await page.goto(`/projects/${projectId}/review`)
  await expect(page.getByTestId('report-preview').getByTestId('deployment-status').first()).toHaveText('Ready')
  await expect(page.getByRole('table', { name: /^Review cycles, newest first/ }).getByRole('row')).not.toHaveCount(1)
})

test('FR-90: a preview whose build fails is a critical "Vercel preview build failed" finding with the build log', async ({
  page,
}) => {
  test.setTimeout(12 * 60_000)
  const context = page.context()
  const projectName = `E2E Preset fail ${String(Date.now())}`
  await registerViaApi(context, 'presetfail', 'Preset Fail Owner')
  await createVercelConnection(context, 'Fake Vercel')
  const projectId = await createPresetProjectInWizard(page, projectName, IDEA)

  const specDraft = await driveToSpecDraft(context, projectId)
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${specDraft.id}/approve`, {})
  await driveSpecToPlanDraft(context, projectId)
  // §4.15: an Owner-edited plan makes the last step write .aide-fake/fail-build, so the Fake preview build fails.
  const edited = await addFailBuildMarkerToPlan(context, projectId)
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${edited}/approve`, {})
  await drivePlanToExecutionStarted(context, projectId)
  await waitForPhase(context, projectId, 'review', 3 * AGENT_TIMEOUT)

  const failed = await waitForDeployment(context, projectId, 'preview', 'error')
  expect(failed.buildLogTail ?? '').toContain('exited with 1')
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT })
    .not.toBeNull()
  expect((await projectState(context, projectId)).review.latestDecision).not.toBe('release_eligible')

  await page.goto(`/projects/${projectId}/review`)
  await expect(page.locator('#review-preview-deployment').getByTestId('deployment-status').first()).toHaveText('Error')
  const reportPreview = page.getByTestId('report-preview')
  await expect(reportPreview.getByText('Vercel preview build failed')).toBeVisible()
  await expect(reportPreview.getByTestId('build-log-tail')).toContainText('exited with 1')
  const critical = page.getByRole('region', { name: /^Critical \(\d+ violations?\)$/ })
  const finding = critical.getByRole('listitem').filter({ hasText: 'Vercel preview build failed' })
  await expect(finding).toBeVisible()
  await expect(finding).toContainText('Category: Architecture')
  await expect(finding).toContainText(/Evidence: [\s\S]*Failed to compile[\s\S]*exited with 1/)
})
