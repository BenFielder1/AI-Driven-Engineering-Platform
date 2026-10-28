import { expect, test } from '@playwright/test'

import { apiJson, projectState, registerViaApi } from './support/api.ts'
import {
  COMPOSITION,
  COMPOSITION_SECTIONS,
  COMPOSITION_SPECIALISTS,
  addFailBuildMarkerToPlan,
  agentRuns,
  createComposedProjectInWizard,
  drivePlanToExecutionStarted,
  driveSpecToPlanDraft,
  driveToSpecDraft,
  planAgents,
  projectStack,
  waitForGateOrEscalation,
} from './support/composition.ts'
import {
  AGENT_TIMEOUT,
  approveDraftViaApi,
  approveInPlace,
  approveStageGate,
  createLocalProject,
  createVercelConnection,
  openStage,
  waitForDeployment,
  waitForPhase,
} from './support/v2.ts'

/**
 * FR-129 replaces the v2 preset E2E with the composition path (SPEC §8) on the E2E deployment (Fake LLM +
 * FakeStackScripts, Local Git): the wizard's Stack step composes React + Vite + ASP.NET Core + EF Core + PostgreSQL
 * (FR-114, FR-115) → Settings → Stack shows it with no legacy preset (FR-117, FR-129) → the spec has the composition's
 * sections (FR-123) → plan steps carry the specialists, scaffolder first (FR-124) → every code_generator run is a
 * specialist of the composition with no preset (FR-120, FR-121, FR-129) → review is release eligible (FR-126) → the
 * release is approved on the Release page. A second test keeps the v2 preview build-failure path (FR-90) on a project
 * without a stack.
 */

const IDEA = 'A recipe box: save recipes with their ingredients through an API and browse them in a web app.'

test('FR129_FR120_FR121_FR124: a composed project runs its specialist plan from refinement to release', async ({ page }) => {
  test.setTimeout(15 * 60_000)
  const context = page.context()
  const projectName = `E2E Composition ${String(Date.now())}`
  await registerViaApi(context, 'compose', 'Composition Owner')

  // FR-114, FR-115: composed in the wizard; FR-129: nothing is pinned to a preset.
  const projectId = await createComposedProjectInWizard(page, projectName, IDEA)
  const project = await apiJson<{ presetId: string | null }>(context, 'GET', `/api/projects/${projectId}`)
  expect(project.presetId).toBeNull()
  const stack = await projectStack(context, projectId)
  expect(stack.composition).toMatchObject({
    frontend: { pack: COMPOSITION.frontend.pack },
    backend: { pack: COMPOSITION.backend.pack },
    data: { pack: COMPOSITION.data.pack },
    vcs: { pack: COMPOSITION.vcs.pack },
  })
  expect(stack.legacyPreset).toBeNull()
  expect(stack.catalogHash).not.toBeNull()
  const specialists = stack.composition?.derived.agents ?? []
  for (const s of COMPOSITION_SPECIALISTS) {
    expect(specialists).toContain(s.key)
  }

  // FR-117: Settings → Stack shows the composition, with no legacy preset note.
  await page.goto(`/projects/${projectId}/settings/stack`)
  const current = page.getByRole('region', { name: 'Current stack' })
  for (const layer of [COMPOSITION.frontend, COMPOSITION.backend, COMPOSITION.data]) {
    await expect(current).toContainText(layer.name)
  }
  await expect(page.getByTestId('legacy-preset')).toHaveCount(0)

  // FR-123: the spec has the composition's extra sections; approved in place on the Specification page.
  await driveToSpecDraft(context, projectId)
  await page.goto(`/projects/${projectId}/specification`)
  const spec = page.getByRole('region', { name: 'Technical specification', exact: true })
  for (const title of COMPOSITION_SECTIONS) {
    await expect(spec.getByRole('heading', { name: title, exact: true })).toBeVisible({ timeout: AGENT_TIMEOUT })
  }
  await approveInPlace(page, 'Technical specification')

  // FR-124: the plan's steps carry the composition's specialists (scaffolder first on the empty repo); it is valid.
  const planDraft = await driveSpecToPlanDraft(context, projectId)
  expect(planDraft.status).toBe('draft')
  const planArtifact = await apiJson<{ warnings: { code: string }[] }>(
    context,
    'GET',
    `/api/projects/${projectId}/artifacts/implementation_plan/latest`,
  )
  expect(planArtifact.warnings.map((w) => w.code)).not.toContain('INVALID_PLAN')
  const agents = await planAgents(context, projectId)
  expect(agents[0]).toBe('scaffolder')
  expect(agents).not.toContain('generalist')
  for (const s of COMPOSITION_SPECIALISTS) {
    expect(agents).toContain(s.key)
  }
  for (const key of agents) {
    expect(specialists).toContain(key)
  }
  await page.goto(`/projects/${projectId}/planning`)
  const steps = page.getByRole('table', { name: /^Plan steps with their specialist/ })
  await expect(steps).toBeVisible({ timeout: AGENT_TIMEOUT })
  const rows = steps.locator('tbody tr')
  await expect(rows).toHaveCount(agents.length)
  for (const [i, key] of agents.entries()) {
    await expect(rows.nth(i).getByText(key, { exact: true })).toBeVisible()
  }
  await expect(page.getByText('INVALID_PLAN')).toHaveCount(0)
  await approveInPlace(page, 'Implementation plan')

  // FR-121, FR-125: execution runs each step as its specialist.
  await drivePlanToExecutionStarted(context, projectId)
  await waitForPhase(context, projectId, 'review', 3 * AGENT_TIMEOUT)
  const executed = await apiJson<{ steps: { agentKey: string | null; legacyAgentKey?: string | null }[] }>(
    context,
    'GET',
    `/api/projects/${projectId}/execution/steps`,
  )
  expect(executed.steps.map((s) => s.agentKey)).toEqual(agents)
  expect(executed.steps.every((s) => !s.legacyAgentKey)).toBe(true)

  await page.goto(`/projects/${projectId}/execution`)
  await expect(page.getByRole('heading', { level: 2, name: 'Execution', exact: true })).toBeVisible()
  const devRun = page.getByRole('list', { name: 'Runs' }).getByRole('listitem', { name: /^Implementation plan v\d+/ }).first()
  await expect(devRun).toBeVisible({ timeout: AGENT_TIMEOUT })
  const toggle = devRun.getByRole('button', { name: /^Implementation plan v\d+/ })
  if ((await toggle.count()) > 0 && (await toggle.first().getAttribute('aria-expanded')) === 'false') {
    await toggle.first().click()
  }
  for (const s of COMPOSITION_SPECIALISTS) {
    await expect(devRun.getByText(s.key).first()).toBeVisible()
  }

  // FR-120, FR-121, FR-129: every code_generator run is a specialist of the composition, with no preset.
  const codeRuns = await agentRuns(context, projectId, 'code_generator')
  expect(codeRuns.length).toBeGreaterThanOrEqual(agents.length)
  for (const r of codeRuns) {
    expect(specialists).toContain(r.agentKey)
    expect(r.presetId).toBeNull()
    expect(r.legacyAgentKey ?? null).toBeNull()
  }
  await page.goto(`/projects/${projectId}/runs`)
  await page.getByLabel('Agent', { exact: true }).selectOption({ label: 'Code generator' })
  const runsTable = page.getByRole('table', { name: /^Agent runs, newest first/ })
  await expect(runsTable.getByRole('columnheader', { name: 'Preset' })).toHaveCount(0)
  await expect(runsTable.getByRole('row').filter({ hasText: 'backend-engineer' }).first()).toBeVisible()

  // FR-126: the composed review completes: cycle 1 may ask for a cycle; then it is release eligible.
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT })
    .not.toBeNull()
  if ((await projectState(context, projectId)).review.latestDecision === 'review_required') {
    await approveDraftViaApi(context, projectId, 'optimization_plan')
  }
  await waitForGateOrEscalation(context, projectId, 'pre_release', 4 * AGENT_TIMEOUT)
  expect((await projectState(context, projectId)).review.latestDecision).toBe('release_eligible')
  const reviewRuns = await agentRuns(context, projectId, 'code_review')
  expect(reviewRuns.length).toBeGreaterThan(0)
  for (const r of reviewRuns) {
    expect(r.presetId).toBeNull()
  }

  // Release: approved on the Release page, tagged with the initial version.
  await page.goto(`/projects/${projectId}`)
  await openStage(page, 'Release')
  await approveStageGate(page, 'Release approval')
  await expect
    .poll(async () => (await projectState(context, projectId)).status, { timeout: 2 * AGENT_TIMEOUT, intervals: [500, 1000] })
    .toBe('released')
  await expect(page.getByTestId('version-badges').first()).toContainText('Released 0.1.0', { timeout: AGENT_TIMEOUT })
})

test('FR90_preview_build_failure: a failed preview build is a critical "Vercel preview build failed" finding with the build log', async ({
  page,
}) => {
  test.setTimeout(12 * 60_000)
  const context = page.context()
  const projectName = `E2E Preview fail ${String(Date.now())}`
  await registerViaApi(context, 'previewfail', 'Preview Fail Owner')
  const vercelConnectionId = await createVercelConnection(context, 'Fake Vercel')
  const created = await createLocalProject(context, { name: projectName, idea: IDEA, vercelConnectionId })
  expect(created.deploymentsEnabled).toBe(true)
  const projectId = created.id

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
