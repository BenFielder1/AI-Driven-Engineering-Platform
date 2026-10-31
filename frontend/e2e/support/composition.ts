import { expect, type BrowserContext, type Page } from '@playwright/test'

import { apiJson, waitForDraft } from './api.ts'
import { AGENT_TIMEOUT, approveGateViaApi } from './v2.ts'

/**
 * Stack composition E2E helpers (FR-114, FR-120–FR-129; SPEC §8 composition E2E) on the E2E deployment: Fake LLM with
 * FakeStackScripts (the plan and files of each composed specialist), Local Git and the Fake deployment provider
 * (§4.15). The v2 presets are retired (FR-129); this replaces their E2E path.
 */

/**
 * The composition the Fake path is proven on end to end (backend CompositionAgentTests): React + Vite, ASP.NET Core and
 * EF Core + PostgreSQL, with GitHub as the VCS (a Local repository allows any VCS) and no hosts (Compose dev loop).
 */
export const COMPOSITION = {
  frontend: { pack: 'fe-react-vite', name: 'React + Vite' },
  backend: { pack: 'be-aspnet', name: 'ASP.NET Core' },
  data: { pack: 'data-efcore-postgres', name: 'EF Core + PostgreSQL' },
  vcs: { pack: 'vcs-github', name: 'GitHub' },
} as const

/** FR-123: the composition's extra spec sections (be-aspnet and data-efcore-postgres). */
export const COMPOSITION_SECTIONS = ['API Contract & Error Codes', 'Data Model & Migrations'] as const

/** FR-120: specialists the composition must bring (display names in the Stack summary, keys on plan steps and runs). */
export const COMPOSITION_SPECIALISTS = [
  { key: 'data-engineer', name: 'Data engineer' },
  { key: 'backend-engineer', name: 'Backend engineer' },
  { key: 'frontend-engineer', name: 'Frontend engineer' },
] as const

export interface RunDto {
  id: string
  agent: string
  status: string
  agentKey: string | null
  legacyAgentKey?: string | null
  presetId: string | null
  presetVersion: string | null
}

export async function agentRuns(context: BrowserContext, projectId: string, agent?: string): Promise<RunDto[]> {
  const page = await apiJson<{ items: RunDto[] }>(
    context,
    'GET',
    `/api/projects/${projectId}/runs?limit=100${agent ? `&agent=${agent}` : ''}`,
  )
  return page.items
}

export interface ProjectStackDto {
  composition: {
    frontend: { pack: string; path: string | null } | null
    backend: { pack: string; path: string | null } | null
    data: { pack: string; mode: string | null } | null
    vcs: { pack: string }
    deploy: { frontendHost: string | null; backendHost: string | null; databaseHost: string | null; local: string }
    derived: { agents: string[] }
  } | null
  catalogVersion: string | null
  catalogHash: string | null
  legacyPreset: { label: string } | null
}

export const projectStack = (context: BrowserContext, projectId: string) =>
  apiJson<ProjectStackDto>(context, 'GET', `/api/projects/${projectId}/stack`)

const next = (page: Page) => page.getByRole('button', { name: 'Next', exact: true }).click()

async function pickLayer(page: Page, layer: string, option: string) {
  await expect(page.getByRole('heading', { level: 3, name: layer, exact: true })).toBeVisible()
  await page.getByRole('radio', { name: option, exact: true }).check()
  await next(page)
}

/**
 * FR-5/FR-114/FR-115: the eight-step wizard from scratch with Local Git and the Stack step composed layer by layer
 * (Frontend → Backend → Data → VCS → Deployment, the hosts left at None); the Stack summary names the specialists.
 * `hosts` picks the Deployment layer's hosts by their card names instead. Returns the new project's id.
 */
export async function createComposedProjectInWizard(
  page: Page,
  name: string,
  idea: string,
  { hosts }: { hosts?: { frontend: string; backend: string; database: string } } = {},
): Promise<string> {
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await next(page)
  await page.getByLabel('Project name').fill(name)
  await page.getByLabel('Your idea').fill(idea)
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 4 of 8: Version' })).toBeVisible()
  await next(page)

  // FR-114: "No stack (general)" is the default; FR-129: there is no legacy preset choice any more.
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
  await expect(page.getByRole('radio', { name: /^No stack \(general\)/ })).toBeChecked()
  await expect(page.getByText(/Legacy presets/)).toHaveCount(0)
  await page.getByRole('radio', { name: /^Choose the stack/ }).check()
  await pickLayer(page, 'Frontend', COMPOSITION.frontend.name)
  await pickLayer(page, 'Backend', COMPOSITION.backend.name)
  await pickLayer(page, 'Data', COMPOSITION.data.name)
  await pickLayer(page, 'Version control', COMPOSITION.vcs.name)
  await expect(page.getByRole('heading', { level: 3, name: 'Deployment', exact: true })).toBeVisible()
  if (hosts) {
    // FR-115: each host card is in its choice's group (Render is offered for both the frontend and the backend).
    await page.getByRole('group', { name: 'Frontend host' }).getByRole('radio', { name: hosts.frontend, exact: true }).check()
    await page.getByRole('group', { name: 'Backend host' }).getByRole('radio', { name: hosts.backend, exact: true }).check()
    await page.getByRole('group', { name: 'Database host' }).getByRole('radio', { name: hosts.database, exact: true }).check()
  }
  // FR-115, FR-120: the summary derives the specialists in play from the composition.
  const summary = page.getByRole('region', { name: 'Stack summary' })
  for (const s of COMPOSITION_SPECIALISTS) {
    await expect(summary).toContainText(s.name, { timeout: 10_000 })
  }
  await next(page)

  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()
  await page.getByRole('radio', { name: /^Strict/ }).check()
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()
  await next(page)
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  const stack = page.getByRole('region', { name: 'Stack' })
  for (const layer of [COMPOSITION.frontend, COMPOSITION.backend, COMPOSITION.data]) {
    await expect(stack).toContainText(layer.name)
  }
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  return projectId
}

/** Strict, through the API: refinement → project request approved → Specification (spec draft waiting). */
export async function driveToSpecDraft(context: BrowserContext, projectId: string) {
  await apiJson(context, 'POST', `/api/projects/${projectId}/refinement/run`, { answers: [] })
  const request = await waitForDraft(context, projectId, 'project_request', AGENT_TIMEOUT)
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${request.id}/approve`, {})
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  return waitForDraft(context, projectId, 'technical_specification', AGENT_TIMEOUT)
}

/** Strict, through the API: the approved spec → spec_approval gate → Planning (plan draft waiting). */
export async function driveSpecToPlanDraft(context: BrowserContext, projectId: string) {
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGateViaApi(context, projectId, 'spec_approval')
  return waitForDraft(context, projectId, 'implementation_plan', AGENT_TIMEOUT)
}

/** Strict, through the API: the approved plan → plan_approval gate → execution started. */
export async function drivePlanToExecutionStarted(context: BrowserContext, projectId: string) {
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGateViaApi(context, projectId, 'plan_approval')
  await apiJson(context, 'POST', `/api/projects/${projectId}/execution/start`)
}

interface GateDto {
  id: string
  type: string
  status: string
  escalationReason: string | null
  reason: string | null
  stepNumber: number | null
}

/**
 * Waits for the pending gate of `type`; fails at once (with the escalation reason and the review-fix steps' violations)
 * if an escalation gate opens instead, so a blocked step reads as what it is rather than as a timeout.
 */
export async function waitForGateOrEscalation(context: BrowserContext, projectId: string, type: string, timeout: number) {
  let escalation: GateDto | undefined
  await expect
    .poll(
      async () => {
        const gates = await apiJson<GateDto[]>(context, 'GET', `/api/projects/${projectId}/gates`)
        escalation = gates.find((g) => g.status === 'pending' && g.type === 'escalation')
        if (escalation) {
          return 'escalation'
        }
        return gates.some((g) => g.status === 'pending' && g.type === type) ? type : 'waiting'
      },
      { timeout, intervals: [500, 1000] },
    )
    .not.toBe('waiting')
  if (escalation) {
    const steps = await apiJson<{ steps: { number: number; agentKey?: string | null; status: string; violations?: unknown }[] }>(
      context,
      'GET',
      `/api/projects/${projectId}/execution/steps`,
    )
    const report = await apiJson<{
      cycle: number
      overallScore: number
      categoryScores: unknown
      violations: unknown[]
      deterministicFindings: unknown[]
    }>(context, 'GET', `/api/projects/${projectId}/compliance/latest`)
    throw new Error(
      `Expected a pending ${type} gate, got an escalation (step ${String(escalation.stepNumber)}): ${escalation.escalationReason ?? ''} ${escalation.reason ?? ''}\n` +
        `Unfinished steps: ${JSON.stringify(steps.steps.filter((s) => s.status !== 'completed'))}\n` +
        `Latest report: ${JSON.stringify({ cycle: report.cycle, score: report.overallScore, categories: report.categoryScores, violations: report.violations, deterministic: report.deterministicFindings }, null, 1)}`,
    )
  }
}

const PLAN_BLOCK = /^```aide-plan[ \t]*\n([\s\S]*?)\n```[ \t]*$/m

interface PlanStep {
  number: number
  agent: string
  files: { path: string; action: string }[]
}

/** FR-124: the specialist of each step of the latest implementation plan's aide-plan block, in step order. */
export async function planAgents(context: BrowserContext, projectId: string): Promise<string[]> {
  const plan = await apiJson<{ content: string }>(context, 'GET', `/api/projects/${projectId}/artifacts/implementation_plan/latest`)
  const match = PLAN_BLOCK.exec(plan.content)
  expect(match, 'the plan has an aide-plan block').not.toBeNull()
  const doc = JSON.parse(match?.[1] ?? '{}') as { steps?: PlanStep[] } | PlanStep[]
  return (Array.isArray(doc) ? doc : (doc.steps ?? [])).map((s) => s.agent)
}

/**
 * §4.15 failure path: an Owner edit of the plan draft (FR-18, POST …/artifacts/implementation_plan with baseVersion) that
 * makes the last step also create `.aide-fake/fail-build`, so the Fake provider fails the preview build. Only the
 * aide-plan block is changed; the server re-renders the document from it. Returns the new draft's id.
 */
export async function addFailBuildMarkerToPlan(context: BrowserContext, projectId: string): Promise<string> {
  const plan = await apiJson<{ id: string; version: number; content: string; status: string }>(
    context,
    'GET',
    `/api/projects/${projectId}/artifacts/implementation_plan/latest`,
  )
  expect(plan.status).toBe('draft')
  const match = PLAN_BLOCK.exec(plan.content)
  expect(match, 'the plan has an aide-plan block').not.toBeNull()
  const doc = JSON.parse(match?.[1] ?? '{}') as { steps?: PlanStep[] } | PlanStep[]
  const steps = Array.isArray(doc) ? doc : (doc.steps ?? [])
  expect(steps.length).toBeGreaterThan(0)
  steps[steps.length - 1]?.files.push({ path: '.aide-fake/fail-build', action: 'create' })
  const content = plan.content.replace(PLAN_BLOCK, () => `\`\`\`aide-plan\n${JSON.stringify(doc, null, 2)}\n\`\`\``)
  const edited = await apiJson<{ id: string; status: string; content: string }>(
    context,
    'POST',
    `/api/projects/${projectId}/artifacts/implementation_plan`,
    { content, baseVersion: plan.version },
  )
  expect(edited.status).toBe('draft')
  expect(edited.content).toContain('.aide-fake/fail-build')
  return edited.id
}
