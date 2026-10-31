import { AxeBuilder } from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

import { api, apiJson, projectState } from './support/api.ts'
import { agentRuns, planAgents, projectStack, waitForGateOrEscalation } from './support/composition.ts'
import { AGENT_TIMEOUT, approveDraftViaApi, approveStageGate, openStage, waitForPhase } from './support/v2.ts'

/**
 * SPEC §8 v4 E2E, "a migrated v2 preset project releasing a version" (FR-129, §4.23, §9 seed).
 *
 * A preset can't be chosen any more, so a preset project only exists through the RetirePresets migration or the dev seed.
 * This takes the seed's "Demo: Recipe Finder": migrated from nextjs-frontend@1.0.0, advisory, in Planning with its plan
 * job queued, owned by the seed Admin. It needs no test-only API, only that Admin's credentials.
 *
 * CI exposes those credentials to its a11y shard alone, so the file name puts it in the `a11y` Playwright project (its
 * testMatch ends in `a11y.spec.ts`, and the `chromium` project ignores the same pattern). It also scans Settings → Stack
 * with the retired-preset note, which only a migrated project shows.
 *
 * The seeded project is shared and this test moves it on for good, so a retry can't start over: retries are off.
 */
test.describe.configure({ retries: 0 })

const DEMO_NAME = 'Demo: Recipe Finder'
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

test('S8_FR129: the migrated preset demo keeps its retired preset as history and releases 0.1.0', async ({ page }) => {
  const email = process.env.E2E_ADMIN_EMAIL
  const adminPassword = process.env.E2E_ADMIN_PASSWORD
  test.skip(!email || !adminPassword, 'needs E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD (the seed Admin on the target)')
  test.setTimeout(20 * 60_000)
  const context = page.context()
  const login = await context.request.post('/api/auth/login', { data: { email, password: adminPassword } })
  expect(login.ok(), `${String(login.status())} ${await login.text()}`).toBe(true)

  const list = await apiJson<{ items: { id: string; name: string; presetId: string | null }[] }>(
    context,
    'GET',
    `/api/projects?search=${encodeURIComponent(DEMO_NAME)}&limit=100`,
  )
  const demo = list.items.find((p) => p.name === DEMO_NAME)
  expect(demo, `${DEMO_NAME} is seeded (Seed:Enabled)`).toBeDefined()
  const projectId = demo?.id ?? ''
  expect(demo?.presetId).toBe('nextjs-frontend')

  const start = await projectState(context, projectId)
  // Against a long-lived database the demo may already have moved on. CI's database is new for every run.
  test.skip(!process.env.CI && start.phase !== 'planning', `${DEMO_NAME} has already left Planning on this database`)
  expect(start.phase).toBe('planning')

  // FR-129: the retired preset stays as history and the composition runs the project.
  const legacyLabel = 'Preset nextjs-frontend@1.0.0 (retired, migrated)'
  const stack = await projectStack(context, projectId)
  expect(stack.legacyPreset?.label).toBe(legacyLabel)
  expect(stack.composition?.frontend).toMatchObject({ pack: 'fe-nextjs', path: '.' })
  await page.goto(`/projects/${projectId}/settings/stack`)
  await expect(page.getByTestId('legacy-preset')).toHaveText(legacyLabel)
  await expect(page.getByRole('status').filter({ hasText: /^Loading/ })).toHaveCount(0)
  const scan = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze()
  const violations = scan.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target.join(' ')) }))
  expect(violations, JSON.stringify(violations, null, 2)).toEqual([])

  // §4.2 advisory: the plan needs approving, but entering Execution has no gate.
  await approveDraftViaApi(context, projectId, 'implementation_plan')
  const agents = await planAgents(context, projectId)
  expect(agents[0]).toBe('scaffolder')
  expect(agents).toContain('frontend-engineer')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await waitForPhase(context, projectId, 'execution')
  const started = await api(context, 'POST', `/api/projects/${projectId}/execution/start`)
  expect([200, 202], await started.text()).toContain(started.status())
  await waitForPhase(context, projectId, 'review', 3 * AGENT_TIMEOUT)

  // FR-120, FR-121: the code is written by the composition's specialists, not by retired preset agents.
  const codeRuns = await agentRuns(context, projectId, 'code_generator')
  expect(codeRuns.length).toBeGreaterThanOrEqual(agents.length)
  for (const run of codeRuns) {
    expect(agents).toContain(run.agentKey)
    expect(run.legacyAgentKey ?? null).toBeNull()
  }

  // The Fake's review: cycle 1 asks for a cycle with a one-step optimisation plan, cycle 2 is release eligible.
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT })
    .not.toBeNull()
  if ((await projectState(context, projectId)).review.latestDecision === 'review_required') {
    await approveDraftViaApi(context, projectId, 'optimization_plan')
  }
  await waitForGateOrEscalation(context, projectId, 'pre_release', 4 * AGENT_TIMEOUT)

  // FR-64: released on the Release page, tagged with the initial version; the preset is still its history.
  await page.goto(`/projects/${projectId}`)
  await openStage(page, 'Release')
  await approveStageGate(page, 'Release approval')
  await expect
    .poll(async () => (await projectState(context, projectId)).status, { timeout: 2 * AGENT_TIMEOUT, intervals: [500, 1000] })
    .toBe('released')
  await expect(page.getByTestId('version-badges').first()).toContainText('Released 0.1.0', { timeout: AGENT_TIMEOUT })
  const versions = await apiJson<{ version: string; status: string; tag: string | null }[]>(context, 'GET', `/api/projects/${projectId}/versions`)
  expect(versions.map((v) => [v.version, v.status, v.tag])).toEqual([['0.1.0', 'released', 'v0.1.0']])
  expect((await projectStack(context, projectId)).legacyPreset?.label).toBe(legacyLabel)
})
