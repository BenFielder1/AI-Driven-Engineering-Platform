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
} from './support/v2.ts'

/**
 * NFR-TEST2 (SPEC §8 "E2E imports a Local 'existing' repo") on the E2E deployment (Fake LLM, Local Git, seeded
 * `/data/import/existing-app` with tags v0.1.0 and v0.2.0 under Git:Local:ImportRoot):
 * the wizard imports it (FR-73) → badges → the locked codebase summary before the first refinement round (FR-70) and
 * as the refinement run's input (FR-72) → repo tool calls on the runs (FR-71) → the cumulative iteration spec's
 * "Changes in this version" filter (§4.13) → execution's branch aide/<slug>/v0.3.0 from the released tag (FR-73).
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

/** Version → Stack → Configuration → Deployment → Review & create, with the defaults. */
async function wizardToReview(page: Page) {
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
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

  // FR-70: the Repository Analyst's summary is locked (no gate) and shown on Refinement before the first round.
  await openStage(page, 'Refinement')
  await expect(page.getByRole('region', { name: 'Request', exact: true })).toContainText(request)
  const summary = page.getByRole('region', { name: 'Codebase summary', exact: true })
  await expect(summary.getByText('Locked', { exact: true })).toBeVisible({ timeout: 2 * AGENT_TIMEOUT })
  await expect(summary.getByRole('heading', { name: 'Overview' })).toBeVisible()
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

  // FR-73: execution prepares the working copy on aide/<slug>/v0.3.0 (from the imported tag's commit).
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
    .toBe(`aide/${project.slug}/v0.3.0`)
})
