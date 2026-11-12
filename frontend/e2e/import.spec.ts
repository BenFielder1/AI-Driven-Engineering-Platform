import { expect, test, type Page } from '@playwright/test'

import { api, apiJson, projectState, registerViaApi } from './support/api.ts'
import {
  AGENT_TIMEOUT,
  advanceFromHeader,
  approveDraftViaApi,
  approveGateViaApi,
  approveInPlace,
  approveStageGate,
  currentPhase,
  headerBadges,
  openStage,
  waitForPhase,
} from './support/lifecycle.ts'
import { createRailwayConnection, deploymentGroups } from './support/tracker-inference.ts'

/**
 * NFR-TEST2 (SPEC §8 "E2E imports a Local 'existing' repo") on the E2E deployment (Fake LLM, Local Git, seeded
 * `/data/import/existing-app` with tags v0.1.0 and v0.2.0 under Git:Local:ImportRoot):
 * the wizard imports it (FR-73) → badges → the locked codebase summary before the first refinement round (FR-70) and
 * as the refinement run's input (FR-72) → repo tool calls on the runs (FR-71) → the cumulative iteration spec's
 * "Changes in this version" filter (§4.13) → execution's branch tevrin/<slug>/v0.3.0 from the released tag (FR-73, FR-232).
 */

const SEEDED_REPO = '/data/import/existing-app'

interface RunDto {
  id: string
  agent: string
  status: string
  toolCallCount: number
  inputArtifacts: { type: string; version: number }[]
}

interface VersionDto {
  version: string
  status: string
  tag: string | null
  baseTag: string | null
  branchName: string | null
}

/** Wizard steps 1–4 for a Local import: Source=Import, Request, Repository (path + typed released tag), Version. */
async function wizardToVersion(page: Page, name: string, request: string, releasedTag: string) {
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await page.getByRole('radio', { name: 'Import an existing repository' }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 2 of 8: Request' })).toBeVisible()
  await page.getByLabel('Project name').fill(name)
  await page.getByLabel('What should this version deliver?').fill(request)
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await page.getByLabel(/Path of an existing repository on the server \(development only\)/).fill(SEEDED_REPO)
  await page.getByLabel('Tag of the currently released version (optional)').fill(releasedTag)
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 4 of 8: Version' })).toBeVisible()
}

/**
 * Version → Stack → Configuration → Deployment → Review & create. FR-178: an import preselects Infer; these tests choose
 * No stack (general) explicitly, so the version starts in Refinement as they expect.
 */
async function wizardToReview(page: Page) {
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
  await expect(page.getByRole('radio', { name: /^Infer from the repository/ })).toBeChecked()
  await page.getByRole('radio', { name: /^No stack \(general\)/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()
  await page.getByRole('radio', { name: /^Strict/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
}

/** FR-71 in the UI: the runs page row of `agentLabel` and its detail sheet with the Fake script's tool calls. */
async function expectToolCallsOnRunsPage(page: Page, projectId: string, agentLabel: string) {
  await page.goto(`/projects/${projectId}/runs`)
  await expect(page.getByRole('heading', { level: 2, name: 'Agent runs' })).toBeVisible()
  await page.getByLabel('Agent', { exact: true }).selectOption({ label: agentLabel })
  const runButton = page.getByRole('button', { name: new RegExp(`^${agentLabel} run, started `) }).first()
  await expect(runButton).toBeVisible()
  await runButton.click()
  const sheet = page.getByRole('dialog', { name: `${agentLabel} run` })
  await expect(sheet).toBeVisible()
  const calls = sheet.getByRole('table', { name: 'Repository tool calls of this run' })
  await expect(calls.getByRole('rowheader', { name: 'list_files' }).first()).toBeVisible()
  await expect(calls.getByRole('rowheader', { name: 'read_file' }).first()).toBeVisible()
  await expect(calls).toContainText('README.md')
  await page.keyboard.press('Escape')
  await expect(sheet).toBeHidden()
}

test.describe.configure({ mode: 'serial' })

test('FR-73: a released tag that is not in the repository shows the TAG_NOT_FOUND copy and creates nothing', async ({ page }) => {
  const context = page.context()
  await registerViaApi(context, 'import-tag', 'Import Tag Owner')
  const name = `E2E Import missing tag ${String(Date.now())}`

  await wizardToVersion(page, name, 'Add CSV export', 'v9.9.9')
  await wizardToReview(page)
  const created = page.waitForResponse((r) => r.url().endsWith('/api/projects') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Create project' }).click()
  const response = await created
  expect(response.status()).toBe(400)
  expect(((await response.json()) as { errorCode: string }).errorCode).toBe('TAG_NOT_FOUND')
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await expect(page.getByText(/That tag isn't in the repository/).first()).toBeVisible()

  const list = await apiJson<{ total: number }>(context, 'GET', `/api/projects?search=${encodeURIComponent(name)}`)
  expect(list.total).toBe(0)
})

test('NFR-TEST2, FR-70–FR-73, §4.13: a Local repo is imported at v0.2.0 and 0.3.0 is developed on it', async ({ page }) => {
  test.setTimeout(12 * 60_000)
  const context = page.context()
  await registerViaApi(context, 'import', 'Import Owner')
  const projectName = `E2E Import ${String(Date.now())}`
  const request = 'Add CSV export'

  // FR-73: Source=Import, Local path, typed released tag v0.2.0 → version to develop prefilled 0.3.0.
  await wizardToVersion(page, projectName, request, 'v0.2.0')
  await expect(page.getByLabel('Version to develop')).toHaveValue('0.3.0')
  await wizardToReview(page)
  await expect(page.getByRole('region', { name: 'Source' }).getByText('Import an existing repository')).toBeVisible()
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name: projectName })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  await expect(headerBadges(page, projectName)).toContainText('Released 0.2.0')
  await expect(headerBadges(page, projectName)).toContainText('In development 0.3.0')
  await expect(currentPhase(page)).toContainText('Refinement')

  const project = await apiJson<{ slug: string; releasedVersion: string | null; developmentVersion: string }>(
    context,
    'GET',
    `/api/projects/${projectId}`,
  )
  expect(project).toMatchObject({ releasedVersion: '0.2.0', developmentVersion: '0.3.0' })
  const state = await projectState(context, projectId)
  expect(state).toMatchObject({ phase: 'refinement', checkpoint: 1 })
  const versions = await apiJson<VersionDto[]>(context, 'GET', `/api/projects/${projectId}/versions`)
  expect(versions.find((v) => v.version === '0.2.0')).toMatchObject({ status: 'imported', tag: 'v0.2.0' })
  expect(versions.find((v) => v.version === '0.3.0')).toMatchObject({ status: 'in_development', baseTag: 'v0.2.0' })

  // FR-70, FR-182: the Repository Analyst's summary is locked (no gate) and shown on the Analysis page; Refinement links to it.
  await page.goto(`/projects/${projectId}/analysis`)
  await expect(page.getByRole('heading', { level: 2, name: 'Analysis', exact: true })).toBeVisible()
  const summary = page.getByRole('region', { name: 'Codebase summary', exact: true })
  await expect(summary.getByText('Locked', { exact: true })).toBeVisible({ timeout: 2 * AGENT_TIMEOUT })
  await expect(summary.getByRole('heading', { name: 'Overview' })).toBeVisible()
  // A version whose stack wasn't inferred has nothing to approve here.
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
  await openStage(page, 'Refinement')
  await expect(page.getByRole('region', { name: 'Request', exact: true })).toContainText(request)
  await expect(
    page.getByRole('region', { name: 'Codebase summary', exact: true }).getByRole('link', { name: 'Open the codebase summary on the Analysis page' }),
  ).toBeVisible()
  const codebaseSummary = await apiJson<{ version: number; status: string }>(
    context,
    'GET',
    `/api/projects/${projectId}/artifacts/codebase_summary/latest`,
  )
  expect(codebaseSummary).toMatchObject({ version: 1, status: 'locked' })

  // FR-72: the first refinement round; its run lists the codebase summary as an input.
  await page.getByRole('button', { name: 'Start refinement' }).click()
  await approveInPlace(page, 'Project request')
  const runs = await apiJson<{ items: RunDto[] }>(context, 'GET', `/api/projects/${projectId}/runs?limit=50`)
  const analyst = runs.items.filter((r) => r.agent === 'repository_analyst')
  const refinement = runs.items.filter((r) => r.agent === 'idea_refinement')
  expect(analyst).toHaveLength(1)
  expect(analyst[0]).toMatchObject({ status: 'succeeded' })
  expect(analyst[0]?.toolCallCount).toBeGreaterThan(0)
  expect(refinement.length).toBeGreaterThan(0)
  expect(refinement[0]?.inputArtifacts).toContainEqual({ type: 'codebase_summary', version: 1 })
  expect(refinement[0]?.toolCallCount).toBeGreaterThan(0)

  // FR-71: both runs show their repository tool calls (Fake script: list_files, then read_file README.md).
  await expectToolCallsOnRunsPage(page, projectId, 'Repository Analyst')
  await expectToolCallsOnRunsPage(page, projectId, 'Idea refinement')

  // §4.13: the cumulative iteration spec with tagged requirements and the "Changes in this version" filter.
  await page.goto(`/projects/${projectId}/refinement`)
  await advanceFromHeader(page, 'Specification')
  await expect(currentPhase(page)).toContainText('Specification')
  await openStage(page, 'Specification')
  await approveInPlace(page, 'Technical specification')
  const changes = page.getByRole('region', { name: 'Changes in this version' })
  await expect(changes).toBeVisible()
  const newFilter = changes.getByRole('button', { name: /^New \(\d+\)$/ })
  await newFilter.click()
  await expect(newFilter).toHaveAttribute('aria-pressed', 'true')
  await expect(changes.getByRole('list', { name: 'Requirements in this filter' }).getByRole('listitem')).not.toHaveCount(0)
  await advanceFromHeader(page, 'Planning')
  await approveStageGate(page, 'Specification approval')
  await expect(currentPhase(page)).toContainText('Planning')

  // FR-73: execution prepares the working copy on tevrin/<slug>/v0.3.0 (FR-232; from the imported tag's commit).
  await approveDraftViaApi(context, projectId, 'implementation_plan')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGateViaApi(context, projectId, 'plan_approval')
  await waitForPhase(context, projectId, 'execution')
  const started = await api(context, 'POST', `/api/projects/${projectId}/execution/start`)
  expect([200, 202], await started.text()).toContain(started.status())
  await expect
    .poll(
      async () =>
        (await apiJson<VersionDto[]>(context, 'GET', `/api/projects/${projectId}/versions`)).find((v) => v.version === '0.3.0')
          ?.branchName ?? null,
      { timeout: 2 * AGENT_TIMEOUT, intervals: [500, 1000] },
    )
    .toBe(`tevrin/${project.slug}/v0.3.0`)
})

/**
 * FR-178–FR-185 on the E2E deployment (SPEC 0.5.0 §8 "infer a stack, fill a blank, edit a finding, pick one Railway group,
 * approve, continue to Refinement" and "reject a profile and see a re-run"): a Local import with Infer (preselected) starts
 * in Analysis. The Fake Stack Analyst's `[fake-stack:unknown]` marker in the version request leaves the backend `unknown`.
 * The Owner rejects the first draft (the analyst re-runs with the feedback), answers Needs your input (the layers the
 * detector couldn't decide too), edits a finding and the hosts (Prisma; backend and database on Railway: a Local repository's
 * VCS is GitHub, so R9 allows Railway), approves, sets up the one Railway group window and continues to Refinement.
 */
test('FR-178, FR-181, FR-183, FR-184, FR-185, FR-188: infer the stack, reject and re-run, fill the blank, edit, approve, set up the Railway group and continue to Refinement', async ({
  page,
}) => {
  test.setTimeout(12 * 60_000)
  const context = page.context()
  await registerViaApi(context, 'import-infer', 'Infer Owner')
  const railwayConnectionId = await createRailwayConnection(context)
  const projectName = `E2E Infer ${String(Date.now())}`

  await wizardToVersion(page, projectName, 'Add CSV export [fake-stack:unknown]', 'v0.2.0')
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
  await expect(page.getByRole('radio', { name: /^Infer from the repository/ })).toBeChecked()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()
  // FR-178: the Deployment step is skipped.
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Stack', exact: true })).toContainText('Infer from the repository')
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name: projectName })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  expect(await projectState(context, projectId)).toMatchObject({ phase: 'analysis' })

  // FR-182: Analysis is in the project nav and the tracker.
  await page.getByRole('navigation', { name: 'Project sections' }).getByRole('link', { name: 'Analysis' }).click()
  await expect(page.getByRole('heading', { level: 2, name: 'Analysis', exact: true })).toBeVisible()

  // FR-181: the backend (and anything the detector couldn't decide) is listed first, with a picker per layer.
  const needs = page.getByRole('region', { name: 'Needs your input', exact: true })
  await expect(needs).toBeVisible({ timeout: 3 * AGENT_TIMEOUT })
  const approve = page.getByRole('button', { name: 'Approve', exact: true })
  await expect(approve).toBeDisabled()
  await expect(page.getByTestId('approve-reason')).toContainText('Answer these before approving')

  // FR-183: Reject needs feedback; with it, the Stack Analyst runs again (a run triggered by the rejection, a new draft).
  await page.getByRole('button', { name: 'Reject', exact: true }).click()
  const reject = page.getByRole('alertdialog', { name: /^Reject stack profile v\d+\?$/ })
  await reject.getByRole('button', { name: /^Reject v\d+$/ }).click()
  await expect(reject.getByText('Tell the Stack Analyst what to change.').first()).toBeVisible()
  await reject.getByLabel('Feedback for the Stack Analyst').fill('The backend is an Express app at the repository root.')
  await reject.getByRole('button', { name: /^Reject v\d+$/ }).click()
  await expect(reject).toBeHidden()
  await expect(page.getByRole('region', { name: 'Stack Analyst', exact: true })).toContainText('Started after the profile was rejected', {
    timeout: AGENT_TIMEOUT,
  })
  await expect(page.getByRole('region', { name: 'Findings', exact: true })).toContainText('Revised after review feedback', {
    timeout: 3 * AGENT_TIMEOUT,
  })
  const analystRuns = await apiJson<{ items: RunDto[] }>(context, 'GET', `/api/projects/${projectId}/runs?limit=50`)
  expect(analystRuns.items.filter((r) => r.agent === 'stack_analyst')).toHaveLength(2)
  // The re-run keeps the marker's blank: it is asked again.
  await expect(needs).toBeVisible({ timeout: AGENT_TIMEOUT })
  await expect(approve).toBeDisabled()

  const backend = needs.getByTestId('needs-input-backend')
  await expect(backend).toContainText('Which backend serves')
  await backend.getByLabel('Backend option').selectOption({ label: 'Express' })
  await backend.getByLabel('Backend folder').fill('.')
  for (const layer of ['frontend', 'data'] as const) {
    const item = needs.getByTestId(`needs-input-${layer}`)
    if ((await item.count()) > 0) {
      await item.getByLabel(layer === 'frontend' ? 'Frontend option' : 'Data option').selectOption('none')
    }
  }
  await needs.getByRole('button', { name: 'Save answers as a new draft' }).click()
  await expect(needs).toBeHidden({ timeout: AGENT_TIMEOUT })
  await expect(page.getByTestId('finding-backend')).toContainText('Express')

  // FR-183: Edit is a form over the profile, saved as a new draft by the user: Data becomes Prisma (allowed with Express,
  // R3), and FR-184's hosts put the backend and database on Railway.
  await page.getByRole('region', { name: 'Decide', exact: true }).getByRole('button', { name: 'Edit', exact: true }).click()
  const editor = page.getByRole('form', { name: 'Edit the stack profile' })
  await expect(editor).toBeVisible()
  await editor.getByLabel('Frontend option').selectOption('none')
  await editor.getByLabel('Data option').selectOption('data-prisma')
  await editor.getByLabel('Backend host', { exact: true }).selectOption('deploy-railway')
  await editor.getByLabel('Database host', { exact: true }).selectOption('railway-postgres')
  await editor.getByRole('button', { name: 'Save as new draft' }).click()
  await expect(editor).toBeHidden({ timeout: AGENT_TIMEOUT })
  await expect(page.getByTestId('finding-data')).toContainText('Prisma')
  await expect(page.getByTestId('finding-data')).toContainText('Set by a person')

  // FR-183: Approve pins the stack.
  await expect(approve).toBeEnabled()
  await approve.click()
  const dialog = page.getByRole('dialog', { name: /^Approve stack profile v\d+\?$/ })
  await dialog.getByRole('button', { name: /^Approve v\d+$/ }).click()
  await expect(dialog).toBeHidden()
  const stack = await apiJson<{
    composition: {
      backend: { pack: string } | null
      data: { pack: string } | null
      deploy: { backendHost: string | null; databaseHost: string | null }
    } | null
  }>(context, 'GET', `/api/projects/${projectId}/stack`)
  expect(stack.composition?.backend?.pack).toBe('be-express')
  expect(stack.composition?.data?.pack).toBe('data-prisma')
  expect(stack.composition?.deploy).toMatchObject({ backendHost: 'deploy-railway', databaseHost: 'railway-postgres' })

  // FR-184, FR-188: Deployment setup shows the backend and database as one Railway group window; the Owner creates it.
  const setup = page.getByRole('region', { name: 'Deployment setup', exact: true })
  await expect(setup).toBeVisible()
  await expect(setup.getByRole('list', { name: 'Deployment setup windows' }).locator(':scope > li')).toHaveCount(1)
  const group = setup.getByRole('region', { name: /Backend and database on Railway$/ })
  const groupForm = group.getByRole('form', { name: 'Set up the Railway group' })
  await groupForm.getByLabel('Railway connection').selectOption(railwayConnectionId)
  await expect(groupForm.getByRole('radio', { name: /^Create a new project/ })).toBeChecked()
  await groupForm.getByRole('button', { name: 'Set up the group' }).click()
  await expect
    .poll(async () => (await deploymentGroups(context, projectId)).find((g) => g.provider === 'railway')?.id ?? null, {
      timeout: AGENT_TIMEOUT,
    })
    .not.toBeNull()
  const railway = (await deploymentGroups(context, projectId)).find((g) => g.provider === 'railway')
  expect([...(railway?.parts ?? [])].sort()).toEqual(['backend', 'database'])

  // FR-185: Continue to Refinement (setup still running doesn't block it).
  await page.getByRole('button', { name: 'Continue to Refinement' }).click()
  await expect(page.getByRole('heading', { level: 2, name: 'Refinement', exact: true })).toBeVisible()
  expect(await projectState(context, projectId)).toMatchObject({ phase: 'refinement' })
})
