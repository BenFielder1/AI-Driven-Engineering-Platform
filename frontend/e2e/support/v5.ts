import { expect, type BrowserContext, type Locator, type Page } from '@playwright/test'

import { apiJson } from './api.ts'
import { AGENT_TIMEOUT } from './v2.ts'

/**
 * SPEC v5 §8 E2E helpers on the E2E deployment: the Fake issue tracker (`Trackers:Provider=Fake`: container FAKE with
 * items FAKE-1 (an epic) to FAKE-12, FAKE-11 done), the Fake Stack Analyst (a `[fake-stack:unknown]` marker in the
 * version request leaves the backend `unknown`) and the seeded Local repository under the import root.
 */

/** The seeded "existing" repository (tags v0.1.0 and v0.2.0) under Git:Local:ImportRoot. */
export const SEEDED_REPO = '/data/import/existing-app'

/** The Fake Stack Analyst's marker for a profile whose backend is `unknown`, with a question (FakeStackAnalystResponder). */
export const UNKNOWN_BACKEND_MARKER = '[fake-stack:unknown]'

/** The Fake tracker's default container as the browse dialog lists it for Linear (`team:FAKE`, "Fake project", detail "fake"). */
export const FAKE_CONTAINER_LABEL = 'Fake project (fake)'

/** FR-165, FR-166: a Linear connection of the context's user (the Fake tracker accepts any key not starting `fake_invalid`). */
export async function createLinearConnection(context: BrowserContext, label = 'Fake Linear'): Promise<string> {
  const connection = await apiJson<{ id: string; status: string }>(context, 'POST', '/api/me/connections/platforms', {
    provider: 'linear',
    label,
    token: `lin_api_e2e_${String(Date.now())}`,
  })
  expect(connection.status).toBe('valid')
  return connection.id
}

/** FR-142: a Railway connection of the context's user (the Fake platform validator accepts any token not starting `fake_invalid`). */
export async function createRailwayConnection(context: BrowserContext, label = 'Fake Railway'): Promise<string> {
  const connection = await apiJson<{ id: string; status: string }>(context, 'POST', '/api/me/connections/platforms', {
    provider: 'railway',
    label,
    token: `fake_e2e_railway_${String(Date.now())}`,
  })
  expect(connection.status).toBe('valid')
  return connection.id
}

/**
 * FR-178: a Local import of the seeded repository at v0.2.0 that infers its stack (`stackSource: infer`), so version
 * 0.3.0 starts in `analysis`. `request` is the version's request (put the Fake Stack Analyst's markers in it).
 */
export async function createInferProjectViaApi(context: BrowserContext, name: string, request: string) {
  return apiJson<{ id: string; name: string; slug: string }>(context, 'POST', '/api/projects', {
    name,
    idea: request,
    approvalMode: 'strict',
    complianceThreshold: 0.9,
    maxFilesPerStep: 20,
    maxReviewCycles: 3,
    tokenBudget: null,
    stackSource: 'infer',
    git: { mode: 'local', defaultBranch: 'main', localSourcePath: SEEDED_REPO },
    source: 'import',
    import: { releasedTag: 'v0.2.0', developmentVersion: '0.3.0' },
  })
}

type LayerStatus = 'inferred' | 'user' | 'none' | 'fixed' | 'unknown' | 'conflict' | 'unsupported'

export interface ProfileLayer {
  option: string | null
  path: string | null
  mode: string | null
  status: LayerStatus
  confidence: string | null
  technology: string | null
  evidence: { file: string; text: string }[]
  candidates: unknown[]
}

export interface ProfileDocument {
  schemaVersion: number
  baseCommitSha: string | null
  generalist: boolean
  layers: { frontend: ProfileLayer; backend: ProfileLayer; data: ProfileLayer; vcs: ProfileLayer }
  hosts: {
    frontendHost: string | null
    backendHost: string | null
    databaseHost: string | null
    local: string | null
    originModel: string | null
    separateParts: string[]
    detected: string[]
  }
  structure: { path: string; role: string; note: string }[]
  questions: { layer: string; text: string }[]
}

export interface AnalysisDto {
  phase: string
  stackSource: string
  run: { status: string; trigger: string; artifactVersion: number | null } | null
  profile: {
    artifactId: string
    version: number
    status: string
    authoredByAgent: boolean
    narrative: string
    document: ProfileDocument | null
  } | null
  needsInput: { layer: string }[]
  canApprove: boolean
  canContinue: boolean
}

export const analysisOf = (context: BrowserContext, projectId: string) =>
  apiJson<AnalysisDto>(context, 'GET', `/api/projects/${projectId}/analysis`)

/** FR-180: waits until the Stack Analyst's draft profile is there (the Repository Analyst runs first); returns the analysis. */
export async function waitForProfileDraft(context: BrowserContext, projectId: string, timeout = 3 * AGENT_TIMEOUT): Promise<AnalysisDto> {
  let analysis: AnalysisDto | null = null
  await expect
    .poll(
      async () => {
        analysis = await analysisOf(context, projectId)
        return analysis.profile?.document && analysis.profile.status === 'draft' ? 'draft' : (analysis.run?.status ?? 'none')
      },
      { timeout, intervals: [500, 1000, 2000] },
    )
    .toBe('draft')
  return analysis as unknown as AnalysisDto
}

/** §4.27, FR-183: the Markdown the profile editor saves (frontend `renderStackProfile`): the narrative, then the fenced block. */
function renderProfile(narrative: string, doc: ProfileDocument): string {
  const block = `\`\`\`aide-stack-profile\n${JSON.stringify(doc, null, 2)}\n\`\`\`\n`
  const text = narrative.trim()
  return text ? `${text}\n\n${block}` : block
}

const userLayer = (current: ProfileLayer, option: string, path: string | null): ProfileLayer => ({
  ...current,
  option,
  path,
  mode: null,
  status: 'user',
  technology: null,
  candidates: [],
})

const noLayer = (current: ProfileLayer): ProfileLayer => ({
  ...current,
  option: null,
  path: null,
  mode: null,
  status: 'none',
  technology: null,
  candidates: [],
})

/**
 * FR-181, FR-183, FR-184 through the API (as the profile editor does): the seeded repository has only its root folder, so
 * the profile becomes an Express API at `.` with Prisma, no frontend, and the backend and database on Railway (§4.28 #3's
 * shape without a frontend: one Railway group of the backend and database). Saved as a new draft; returns its id.
 */
export async function saveRailwayProfileViaApi(context: BrowserContext, projectId: string, analysis: AnalysisDto): Promise<string> {
  const profile = analysis.profile
  const doc = profile?.document
  expect(doc, 'the analysis has a parsed profile').toBeTruthy()
  if (!profile || !doc) {
    throw new Error('no profile')
  }
  const next: ProfileDocument = {
    ...doc,
    layers: {
      ...doc.layers,
      frontend: noLayer(doc.layers.frontend),
      backend: userLayer(doc.layers.backend, 'be-express', '.'),
      data: userLayer(doc.layers.data, 'data-prisma', null),
    },
    hosts: { ...doc.hosts, frontendHost: null, backendHost: 'deploy-railway', databaseHost: 'railway-postgres', separateParts: [], detected: [] },
    questions: [],
  }
  const saved = await apiJson<{ id: string; status: string }>(context, 'POST', `/api/projects/${projectId}/artifacts/stack_profile`, {
    content: renderProfile(profile.narrative, next),
    baseVersion: profile.version,
  })
  expect(saved.status).toBe('draft')
  return saved.id
}

/** FR-183: approves the profile draft (Owner); the stack is pinned and the artifact locked. */
export async function approveProfileViaApi(context: BrowserContext, projectId: string, artifactId: string) {
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${artifactId}/approve`, {})
  await expect.poll(async () => (await analysisOf(context, projectId)).profile?.status, { timeout: AGENT_TIMEOUT }).toBe('locked')
}

export interface DeploymentGroupDto {
  id: string | null
  provider: string
  parts: string[]
  status: string
}

export const deploymentGroups = async (context: BrowserContext, projectId: string) =>
  (await apiJson<{ groups: DeploymentGroupDto[] }>(context, 'GET', `/api/projects/${projectId}/settings/deployment`)).groups

/**
 * FR-169, FR-171: in the open "Import from tracker" dialog (Linear is the only ready tracker), picks the Fake container and
 * waits for its items.
 */
export async function browseFakeTracker(page: Page) {
  const dialog = page.getByRole('dialog', { name: 'Import from tracker' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByLabel('Tracker', { exact: true })).toHaveValue('linear')
  await dialog.getByLabel('Choose a team or project').selectOption({ label: FAKE_CONTAINER_LABEL })
  await expect(dialog.getByRole('list', { name: 'Matching items' })).toBeVisible()
  return dialog
}

/** A Fake tracker item's checkbox in the browse dialog (its name starts with the key and title, then the type and status). */
export const fakeItemCheckbox = (scope: Locator, n: number) =>
  scope.getByRole('checkbox', { name: new RegExp(`^FAKE-${String(n)} Fake item ${String(n)} `) })
