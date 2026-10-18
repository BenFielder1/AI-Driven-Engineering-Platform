import { expect, type BrowserContext, type Page } from '@playwright/test'

import { apiJson, waitForDraft } from './api.ts'
import { AGENT_TIMEOUT, approveGateViaApi } from './v2.ts'

/**
 * Preset E2E helpers (NFR-TEST2 "the preset path with nextjs-frontend", FR-74–FR-77) on the E2E deployment: Fake LLM
 * with FakePresetScripts, Local Git and the Fake deployment provider (§4.15).
 */

export const PRESET_ID = 'nextjs-frontend'
export const PRESET_NAME = 'Next.js frontend'
/** The nextjs-frontend preset's extra spec sections (preset.json `specSections`). */
export const PRESET_SECTIONS = ['Routes & Rendering Strategy', 'Data Sources & Consumed APIs', 'Environment Variables'] as const
/** The Fake preset plan's specialists on an empty repo (FakePresetScripts.Plan for nextjs-frontend), in step order. */
export const PRESET_PLAN_AGENTS = [
  'project-scaffolder',
  'domain-logic-engineer',
  'api-engineer',
  'ui-engineer',
  'test-engineer',
  'devops-engineer',
] as const

export interface RunDto {
  id: string
  agent: string
  status: string
  agentKey: string | null
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

/**
 * FR-5/FR-74/FR-88: the eight-step wizard from scratch with Local Git, the `nextjs-frontend` preset (its card and the
 * "What the preset brings" panel with the specialist agents) and Vercel (Fake). Returns the new project's id.
 */
export async function createPresetProjectInWizard(page: Page, name: string, idea: string): Promise<string> {
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByLabel('Project name').fill(name)
  await page.getByLabel('Your idea').fill(idea)
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 4 of 8: Version' })).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()

  // FR-74: the Stack step's preset cards (name, version, description, stack chips, deploy targets).
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
  const card = page.getByRole('radio', { name: new RegExp(`^${PRESET_NAME.replace('.', '\\.')} \\d+\\.\\d+\\.\\d+$`) })
  await expect(page.getByRole('list', { name: `${PRESET_NAME} stack` })).toContainText('Vercel')
  await card.check()
  const brings = page.getByRole('region', { name: 'What the preset brings' })
  const agents = brings.getByRole('region', { name: /^Specialist agents \(\d+\)$/ })
  await expect(agents).toBeVisible()
  for (const key of PRESET_PLAN_AGENTS) {
    await expect(agents.getByText(key, { exact: true })).toBeVisible()
  }
  const sections = brings.getByRole('region', { name: 'Extra specification sections' })
  for (const title of PRESET_SECTIONS) {
    await expect(sections.getByText(title, { exact: true })).toBeVisible()
  }
  await page.getByRole('button', { name: 'Next' }).click()

  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()
  await page.getByRole('radio', { name: /^Strict/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()
  await page.getByRole('radio', { name: /^Vercel/ }).check()
  const connectionSelect = page.getByLabel('Vercel connection')
  await expect(connectionSelect.locator('option')).toHaveCount(2)
  await connectionSelect.selectOption({ index: 1 })
  await expect(page.getByRole('radio', { name: /^Create a new Vercel project/ })).toBeChecked()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Stack' })).toContainText(PRESET_NAME)
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

const PLAN_BLOCK =/^```aide-plan[ \t]*\n([\s\S]*?)\n```[ \t]*$/m

interface PlanStep {
  number: number
  agent: string
  files: { path: string; action: string }[]
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
