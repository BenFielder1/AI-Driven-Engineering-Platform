import { expect, type BrowserContext, type Locator, type Page } from '@playwright/test'

import { api, apiJson, projectState, waitForDraft, waitForPendingGate } from './api.ts'

/**
 * v2 E2E helpers (NFR-TEST2): Fake LLM + Local Git + the Fake deployment provider (§4.15: READY after 2 polls at
 * https://fake-<sha8>.vercel.test). Everything here talks to the real API or the real UI; no fixed waits.
 */

export const AGENT_TIMEOUT = 60_000
export const LIVE_BOUND_MS = 2_000

/**
 * FR-87, FR-142: a Vercel platform connection of the context's user. The Fake provider accepts any token not starting
 * `fake_invalid`.
 */
export async function createVercelConnection(context: BrowserContext, label = 'Fake Vercel'): Promise<string> {
  const connection = await apiJson<{ id: string; status: string }>(context, 'POST', '/api/me/connections/platforms', {
    provider: 'vercel',
    label,
    token: `fake_e2e_${String(Date.now())}`,
  })
  expect(connection.status).toBe('valid')
  return connection.id
}

export interface LocalProjectOptions {
  name: string
  idea?: string
  /** FR-114: a stack composition request (omitted: No stack (general)). */
  stack?: Record<string, unknown>
  /** A Vercel connection id: the project deploys with the Fake provider (FR-88, Local + Fake). */
  vercelConnectionId?: string
  approvalMode?: 'strict' | 'advisory' | 'autonomous'
  maxReviewCycles?: number
  initialVersion?: string
}

/** POST /api/projects from scratch with Local Git (FR-66), optionally with a stack composition (FR-114) and Fake deployments (FR-88). */
export async function createLocalProject(context: BrowserContext, o: LocalProjectOptions) {
  return apiJson<{ id: string; name: string; slug: string; deploymentsEnabled: boolean }>(context, 'POST', '/api/projects', {
    name: o.name,
    idea: o.idea ?? `${o.name}: a web app that lets small teams track their work items.`,
    approvalMode: o.approvalMode ?? 'strict',
    complianceThreshold: 0.9,
    maxFilesPerStep: 20,
    maxReviewCycles: o.maxReviewCycles ?? 3,
    tokenBudget: null,
    source: 'scratch',
    initialVersion: o.initialVersion ?? '0.1.0',
    ...(o.stack ? { stack: o.stack } : {}),
    git: { mode: 'local', defaultBranch: 'main' },
    ...(o.vercelConnectionId
      ? {
          deployment: {
            provider: 'vercel',
            connectionId: o.vercelConnectionId,
            create: { name: o.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').slice(0, 90), rootDirectory: '.' },
          },
        }
      : {}),
  })
}

export interface DeploymentDto {
  id: string
  target: 'preview' | 'production'
  status: 'queued' | 'building' | 'ready' | 'error' | 'canceled'
  url: string | null
  gitSha: string
  gitRef: string
  releaseVersion: string
  releaseId: string | null
  buildLogTail: string | null
  readyAt: string | null
  createdAt: string
}

export async function deployments(context: BrowserContext, projectId: string, target?: 'preview' | 'production') {
  return apiJson<DeploymentDto[]>(context, 'GET', `/api/projects/${projectId}/deployments${target ? `?target=${target}` : ''}`)
}

/** Polls GET /deployments until the newest one of `target` (optionally of `version`) has `status`; returns it. */
export async function waitForDeployment(
  context: BrowserContext,
  projectId: string,
  target: 'preview' | 'production',
  status: DeploymentDto['status'],
  { version, timeout = 120_000, intervals = [250, 500, 1000] }: { version?: string; timeout?: number; intervals?: number[] } = {},
): Promise<DeploymentDto> {
  let found: DeploymentDto | undefined
  await expect
    .poll(
      async () => {
        const list = (await deployments(context, projectId, target)).filter((d) => !version || d.releaseVersion === version)
        found = list[0]
        return found?.status ?? 'none'
      },
      { timeout, intervals },
    )
    .toBe(status)
  return found as unknown as DeploymentDto
}

/** §4.15: the Fake provider's URL for a commit. */
export const fakeUrl = (sha: string) => `https://fake-${sha.slice(0, 8).toLowerCase()}.vercel.test`

export async function approveDraftViaApi(context: BrowserContext, projectId: string, type: string) {
  const draft = await waitForDraft(context, projectId, type, AGENT_TIMEOUT)
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${draft.id}/approve`, {})
}

export async function approveGateViaApi(context: BrowserContext, projectId: string, type: string, timeout = AGENT_TIMEOUT) {
  const gate = await waitForPendingGate(context, projectId, type, timeout)
  await apiJson(context, 'POST', `/api/projects/${projectId}/gates/${gate.id}/approve`, {})
}

/** Waits for the phase (state machine, via GET /state). */
export async function waitForPhase(context: BrowserContext, projectId: string, phase: string, timeout = 2 * AGENT_TIMEOUT) {
  await expect.poll(async () => (await projectState(context, projectId)).phase, { timeout, intervals: [500, 1000] }).toBe(phase)
}

/**
 * Strict mode, from Planning (plan draft or approved plan) to the pending pre-release gate: approve the plan (if it is
 * a draft), advance (plan_approval gate), start execution, approve the optimisation plan of cycle 1 (Fake: 0.86 → a
 * one-step plan; cycle 2 0.93 is release eligible) and wait for the pre-release gate.
 */
export async function drivePlanningToPreRelease(context: BrowserContext, projectId: string) {
  const plan = await apiJson<{ status: string }>(context, 'GET', `/api/projects/${projectId}/artifacts/implementation_plan/latest`)
  if (plan.status === 'draft') {
    await approveDraftViaApi(context, projectId, 'implementation_plan')
  }
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGateViaApi(context, projectId, 'plan_approval')
  await waitForPhase(context, projectId, 'execution')
  if ((await projectState(context, projectId)).executionStatus === 'completed') {
    // After a rollback to Planning the plan's steps are still completed (the plan wasn't invalidated): nothing to run,
    // so the way on is the next action "Advance to Review".
    await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  } else {
    const started = await api(context, 'POST', `/api/projects/${projectId}/execution/start`)
    expect([200, 202], await started.text()).toContain(started.status())
  }
  await waitForPhase(context, projectId, 'review', 3 * AGENT_TIMEOUT)
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT })
    .not.toBeNull()
  if ((await projectState(context, projectId)).review.latestDecision === 'review_required') {
    await approveDraftViaApi(context, projectId, 'optimization_plan')
  }
  await waitForPendingGate(context, projectId, 'pre_release', 4 * AGENT_TIMEOUT)
}

/** Strict mode, Refinement → the plan draft (Planning), through the API. */
export async function driveRefinementToPlanning(context: BrowserContext, projectId: string) {
  await apiJson(context, 'POST', `/api/projects/${projectId}/refinement/run`, { answers: [] })
  await approveDraftViaApi(context, projectId, 'project_request')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveDraftViaApi(context, projectId, 'technical_specification')
  await apiJson(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  await approveGateViaApi(context, projectId, 'spec_approval')
  await waitForDraft(context, projectId, 'implementation_plan', AGENT_TIMEOUT)
}

export async function releaseViaApi(context: BrowserContext, projectId: string) {
  await approveGateViaApi(context, projectId, 'pre_release')
  await expect
    .poll(async () => (await projectState(context, projectId)).status, { timeout: 2 * AGENT_TIMEOUT, intervals: [500, 1000] })
    .toBe('released')
}

// ---- UI helpers -----------------------------------------------------------------------------------------------------

export function projectNav(page: Page): Locator {
  return page.getByRole('navigation', { name: 'Project sections' })
}

/** FR-54: the stage stepper in the project header (each stage is a link). */
export async function openStage(page: Page, stage: 'Refinement' | 'Specification' | 'Planning' | 'Execution' | 'Review' | 'Release') {
  await page.getByRole('navigation', { name: 'Lifecycle stages' }).getByRole('link', { name: new RegExp(`^${stage}`) }).click()
  await expect(page.getByRole('heading', { level: 2, name: stage, exact: true })).toBeVisible()
}

export function currentPhase(page: Page): Locator {
  return page.getByRole('navigation', { name: 'Lifecycle stages' }).locator('[data-stage-state="current"]')
}

/** FR-55: approve the stage artifact's draft in place on its stage page (the "Approve <label> vN?" dialog). */
export async function approveInPlace(page: Page, label: string) {
  const section = page.getByRole('region', { name: label, exact: true })
  const approve = section.getByRole('button', { name: 'Approve', exact: true })
  await expect(approve).toBeVisible({ timeout: AGENT_TIMEOUT })
  await approve.click()
  const dialog = page.getByRole('dialog', { name: new RegExp(`^Approve ${label} v\\d+\\?$`, 'i') })
  const confirm = dialog.getByRole('button', { name: /^Approve v\d+$/ })
  const version = /v(\d+)$/.exec((await confirm.textContent()) ?? '')?.[1] ?? ''
  await confirm.click()
  await expect(dialog).toBeHidden()
  // Toasts of earlier approvals (another type at the same version number) may still be on screen.
  await expect(page.getByText(`v${version} approved and locked.`).last()).toBeVisible()
  await expect(section.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
}

/** FR-55: decide a stage gate in place (Approve → the "Approve <gate>?" dialog). */
export async function approveStageGate(page: Page, gateLabel: string) {
  const item = page.getByRole('list', { name: 'Pending stage gates' }).getByRole('listitem', { name: gateLabel })
  await expect(item).toBeVisible({ timeout: AGENT_TIMEOUT })
  await item.getByRole('button', { name: 'Approve', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `Approve ${gateLabel.toLowerCase()}?` })
  const decided = page.waitForResponse((r) => /\/gates\/[^/]+\/approve$/.test(r.url()) && r.request().method() === 'POST')
  await dialog.getByRole('button', { name: 'Approve', exact: true }).click()
  expect((await decided).status()).toBe(200)
}

/** FR-54: the header's primary next-action button when it is an "Advance to …" step. */
export async function advanceFromHeader(page: Page, to: string) {
  const button = page.getByTestId('next-action').getByRole('button', { name: `Advance to ${to}` })
  await expect(button).toBeEnabled({ timeout: AGENT_TIMEOUT })
  await button.click()
}

/** The project header's version badges (FR-62). */
export function headerBadges(page: Page, projectName: string): Locator {
  return page
    .getByRole('heading', { level: 1, name: projectName })
    .locator('xpath=ancestor::header[1]')
    .getByTestId('version-badges')
}
