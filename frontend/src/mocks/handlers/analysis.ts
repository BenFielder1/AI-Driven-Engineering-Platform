import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import {
  optionsRequestOfProfile,
  parseStackProfile,
  profileLayers,
  renderStackProfile,
  unresolvedLayers,
  type StackProfileDocument,
} from '../../features/analysis/stack-profile-format'
import { seedArtifact, syncState, writerMutation } from './artifacts'
import { authDb, nextId } from './auth'
import { seedHostsFromComposition } from './deployments'
import { addEvent } from './lifecycle'
import { problem } from './problem'
import {
  access,
  currentVersionOf,
  makeReleaseVersion,
  ownerMutation,
  record,
  repoVcsOfProject,
  type StoredProject,
} from './projects'
import { codebaseSummaryOf } from './refinement'
import { packsOf, STACK_CATALOG_HASH, STACK_CATALOG_VERSION, stackCatalog, validateComposition } from './stack-catalog'

type Analysis = Schemas['AnalysisDto']
type Run = Schemas['StackAnalysisRunDto']
type Artifact = Schemas['ArtifactDto']
type Composition = Schemas['StackCompositionDto']
type Layer = Schemas['StackProfileLayerDto']

/** The Stack Analyst's state of a project's current inference version (the rest is derived from the artifacts). */
interface StoredAnalysis {
  run: Run | null
  /** FR-180 "Fill in manually": the detector's profile. */
  suggested: StackProfileDocument | null
  /** FR-186: the base version's composition. */
  previousComposition: Composition | null
}

const analysisDb = new WeakMap<StoredProject, StoredAnalysis>()

function stored(p: StoredProject): StoredAnalysis {
  let s = analysisDb.get(p)
  if (!s) {
    s = { run: null, suggested: null, previousComposition: null }
    analysisDb.set(p, s)
  }
  return s
}

// ---- Profiles for seeds (the §4.27 worked examples) ----------------------------------------------------------------

const layer = (overrides: Partial<Layer> & Pick<Layer, 'status'>): Layer => ({
  option: null,
  path: null,
  mode: null,
  confidence: 'high',
  technology: null,
  evidence: [],
  candidates: [],
  ...overrides,
})

/**
 * §4.27 worked example 1 (FR-180's Vue + Supabase repo): `fe-vue` at `frontend`, Backend none, `data-supabase` (BaaS)
 * and the repository's VCS, with Supabase as the detected database host. `layers`/`hosts` override parts of it.
 */
export function sampleProfile(
  overrides: { layers?: Partial<StackProfileDocument['layers']>; hosts?: Partial<StackProfileDocument['hosts']> } & Partial<
    Omit<StackProfileDocument, 'layers' | 'hosts'>
  > = {},
): StackProfileDocument {
  const { layers, hosts, ...rest } = overrides
  return {
    schemaVersion: 1,
    baseCommitSha: 'c0ffee1234567',
    generalist: false,
    structure: [
      { path: 'frontend', role: 'frontend', note: 'Vue SPA' },
      { path: 'supabase/migrations', role: 'migrations', note: '' },
    ],
    questions: [],
    ...rest,
    layers: {
      frontend: layer({
        option: 'fe-vue',
        path: 'frontend',
        status: 'inferred',
        evidence: [{ file: 'frontend/package.json', text: 'vue 3.5, vite 7' }],
      }),
      backend: layer({ status: 'none', evidence: [] }),
      data: layer({
        option: 'data-supabase',
        mode: 'baas',
        status: 'inferred',
        evidence: [{ file: 'supabase/migrations/0001_init.sql', text: 'Supabase migrations' }],
      }),
      vcs: layer({ option: 'vcs-github', status: 'fixed', confidence: null }),
      ...layers,
    },
    hosts: {
      frontendHost: null,
      backendHost: null,
      databaseHost: 'supabase',
      local: 'none',
      originModel: 'same-origin',
      separateParts: [],
      detected: ['databaseHost'],
      ...hosts,
    },
  }
}

/** §4.27 worked example 2: Backend `unknown`, with the agent's question. */
export function unknownBackendProfile(): StackProfileDocument {
  return sampleProfile({
    layers: { backend: layer({ status: 'unknown', confidence: 'low', evidence: [{ file: 'server/main.go', text: 'package main' }] }) },
    questions: [{ layer: 'backend', text: 'Is the server/ folder the backend, and which framework does it use?' }],
  })
}

/** §4.27 worked example 4 (FR-181's Django repo): Backend `unsupported`. */
export function unsupportedBackendProfile(): StackProfileDocument {
  return sampleProfile({
    layers: {
      backend: layer({
        status: 'unsupported',
        technology: 'Django',
        evidence: [{ file: 'backend/manage.py', text: 'django.core.management' }],
      }),
    },
  })
}

/** The detector's profile for "Fill in manually" (FR-180): what it found, no agent findings. */
export function detectorProfile(): StackProfileDocument {
  return sampleProfile({
    layers: {
      backend: layer({ status: 'unknown', confidence: null }),
      data: layer({ option: 'data-supabase', mode: 'baas', status: 'inferred', confidence: null, evidence: [{ file: 'supabase/config.toml', text: 'supabase' }] }),
    },
    questions: [{ layer: 'backend', text: 'The detector found no backend. Is there one?' }],
  })
}

let runClock = Date.parse('2026-11-01T09:00:00Z')

function makeRun(status: Run['status'], init: Partial<Run> = {}): Run {
  runClock += 60_000
  const finished = status === 'succeeded' || status === 'failed'
  return {
    jobId: nextId(),
    runId: status === 'queued' ? null : nextId(),
    status,
    trigger: 'phase_entered',
    errorCode: null,
    error: null,
    warnings: [],
    artifactId: null,
    artifactVersion: null,
    createdAt: new Date(runClock).toISOString(),
    completedAt: finished ? new Date(runClock + 30_000).toISOString() : null,
    ...init,
  }
}

/** Writes a profile version (default: an agent draft of the latest run). Earlier drafts are superseded (§4.3). */
function writeProfile(
  p: StoredProject,
  doc: StackProfileDocument,
  init: { status?: NonNullable<Artifact['status']>; authorUserId?: string; narrative?: string } = {},
): Artifact {
  for (const draft of p.artifacts.filter((a) => a.type === 'stack_profile' && a.status === 'draft')) {
    draft.status = 'superseded'
  }
  const artifact = seedArtifact(p, {
    type: 'stack_profile',
    content: renderStackProfile(init.narrative ?? 'A Vue single-page app on Supabase.', doc),
    status: init.status ?? 'draft',
    ...(init.authorUserId ? { authorUserId: init.authorUserId } : {}),
  })
  const run = stored(p).run
  if (!init.authorUserId && run) {
    artifact.author = { user: null, agentRunId: run.runId }
    run.artifactId = artifact.id
    run.artifactVersion = artifact.version
  }
  return artifact
}

/**
 * Seeds a project whose current version infers its stack (FR-178): base commit with code, `stackSource: infer`, phase
 * `analysis` (unless given), the Repository Analyst's summary state, the Stack Analyst's run and a profile.
 */
export function seedInference(
  p: StoredProject,
  init: {
    phase?: Schemas['Phase']
    run?: Run['status'] | null
    runError?: { code: string; message: string }
    profile?: StackProfileDocument | null
    profileStatus?: NonNullable<Artifact['status']>
    narrative?: string
    suggested?: StackProfileDocument | null
    summary?: Schemas['CodebaseSummaryState']
    previousComposition?: Composition | null
  } = {},
): StoredProject {
  const version = makeReleaseVersion({
    version: '1.1.0',
    baseTag: 'v1.0.0',
    baseCommitSha: 'c0ffee1234567',
    stackSource: 'infer',
    request: 'Add CSV export',
  })
  p.releaseVersions = [makeReleaseVersion({ version: '1.0.0', status: 'released', mergeCommitSha: 'c0ffee1234567' }), version]
  p.currentPhase = init.phase ?? 'analysis'
  p.status = 'active'
  const summary = init.summary ?? 'completed'
  const summaryRunId = nextId()
  p.analyst = { [version.id]: { status: summary, runId: summary === 'queued' ? null : summaryRunId, artifactId: null, artifactVersion: null } }
  if (summary === 'completed') {
    seedArtifact(p, {
      type: 'codebase_summary',
      status: 'locked',
      content: '# Codebase summary\n\n## Overview\n\nA Vue app on Supabase.',
      releaseVersionId: version.id,
    })
  }
  const s = stored(p)
  s.suggested = init.suggested === undefined ? detectorProfile() : init.suggested
  s.previousComposition = init.previousComposition ?? null
  const runStatus = init.run === undefined ? 'succeeded' : init.run
  s.run = runStatus === null
    ? null
    : makeRun(runStatus, init.runError ? { errorCode: init.runError.code, error: `${init.runError.code}: ${init.runError.message}` } : {})
  const profile = init.profile === undefined ? (runStatus === 'succeeded' ? sampleProfile() : null) : init.profile
  if (profile) {
    const status = init.profileStatus ?? 'draft'
    writeProfile(p, profile, { status, ...(init.narrative ? { narrative: init.narrative } : {}) })
    if (status === 'locked') {
      pin(p, profile)
    }
  }
  return p
}

/** The worker finishing the Stack Analyst (tests): a new draft, then the live events. */
export function finishStackAnalysis(p: StoredProject, doc: StackProfileDocument = sampleProfile()) {
  const s = stored(p)
  s.run = { ...(s.run ?? makeRun('running')), status: 'succeeded', runId: s.run?.runId ?? nextId(), completedAt: new Date().toISOString() }
  const artifact = writeProfile(p, doc)
  addEvent(p, {
    type: 'STACK_PROFILE_CREATED',
    category: 'artifact',
    summary: `stack_profile v${String(artifact.version)} created`,
    payload: { artifactId: artifact.id, version: artifact.version, unresolvedLayers: unresolvedLayers(doc) },
  })
  addEvent(p, { type: 'RUN_COMPLETED', category: 'run', summary: 'Stack analysis completed' })
  return artifact
}

/** The worker failing the Stack Analyst (tests) with a run code. */
export function failStackAnalysis(p: StoredProject, code = 'STACK_PROFILE_INVALID', message = 'The profile failed validation twice.') {
  const s = stored(p)
  s.run = { ...(s.run ?? makeRun('running')), status: 'failed', errorCode: code, error: `${code}: ${message}`, completedAt: new Date().toISOString() }
  addEvent(p, { type: 'RUN_FAILED', category: 'run', summary: 'Stack analysis failed' })
}

// ---- Domain simulation (§4.27) -------------------------------------------------------------------------------------

/** The profile as a full composition request (with paths and the repository's VCS, R5). */
function compositionRequest(p: StoredProject, doc: StackProfileDocument): Schemas['StackCompositionRequest'] {
  const request = optionsRequestOfProfile(doc)
  const repoVcs = repoVcsOfProject(p)
  return {
    ...request,
    ...(request.frontend && doc.layers.frontend.path ? { frontend: { ...request.frontend, path: doc.layers.frontend.path } } : {}),
    ...(request.backend && doc.layers.backend.path ? { backend: { ...request.backend, path: doc.layers.backend.path } } : {}),
    ...(repoVcs ? { vcs: { pack: repoVcs } } : {}),
  }
}

function pin(p: StoredProject, doc: StackProfileDocument) {
  if (doc.generalist) {
    p.stack = { ...p.stack, composition: null }
    return
  }
  const result = validateComposition(compositionRequest(p, doc))
  if (result.ok) {
    p.stack = { ...p.stack, composition: result.composition, catalogVersion: STACK_CATALOG_VERSION, catalogHash: STACK_CATALOG_HASH }
    p.stackPacks = packsOf(result.composition)
    // FR-184: Settings → Deployment (and the Analysis page's setup windows) now has the composition's hosts.
    seedHostsFromComposition(p, result.composition.deploy, result.composition.groups)
  }
}

const profilesOf = (p: StoredProject, releaseVersionId: string) =>
  p.artifacts.filter((a) => a.type === 'stack_profile' && a.releaseVersionId === releaseVersionId).sort((a, b) => b.version - a.version)

/** Mirrors AnalysisService.GetAsync. */
function analysisDto(p: StoredProject): Analysis | null {
  const version = currentVersionOf(p)
  if (!version) {
    return null
  }
  const s = stored(p)
  const hasCode = Boolean(version.baseCommitSha)
  const composition = p.stack?.composition ?? null
  const base = {
    releaseVersionId: version.id,
    releaseVersion: version.version,
    phase: p.currentPhase,
    stackSource: version.stackSource ?? 'manual',
    hasExistingCode: hasCode,
    baseCommitSha: version.baseCommitSha,
    summary: codebaseSummaryOf(p, version.id),
    composition,
    previousComposition: s.previousComposition,
  }
  if (version.stackSource !== 'infer') {
    return {
      ...base,
      run: null,
      profile: null,
      needsInput: [],
      unresolvedLayers: [],
      canApprove: false,
      approveBlockedReason: "This version's stack wasn't inferred.",
      canContinue: false,
      suggested: null,
      selection: null,
      deployment: deploymentState(null, composition),
    }
  }
  const versions = profilesOf(p, version.id)
  const latest = versions[0]
  const parsed = latest ? parseStackProfile(latest.content) : null
  const doc = parsed?.document ?? null
  const unresolved = doc ? unresolvedLayers(doc) : []
  const lockedExists = versions.some((a) => a.status === 'locked')
  let blocked: string | null = null
  if (!latest) {
    blocked = 'There is no stack profile yet.'
  } else if (latest.status !== 'draft') {
    blocked = `The latest profile (v${String(latest.version)}) is ${latest.status ?? 'unknown'}, not a draft.`
  } else if (p.currentPhase !== 'analysis') {
    blocked = `The project is in ${p.currentPhase}; the profile is decided during analysis.`
  } else if (!doc) {
    blocked = "The profile can't be read. Edit it or run the analysis again."
  } else if (unresolved.length > 0) {
    blocked = `Answer these before approving: ${unresolved.join(', ')}.`
  }
  return {
    ...base,
    run: s.run,
    profile: latest
      ? {
          artifactId: latest.id,
          version: latest.version,
          status: latest.status,
          integrity: latest.integrity,
          authoredByAgent: latest.author.user === null,
          agentRunId: latest.author.agentRunId,
          createdAt: latest.createdAt,
          narrative: parsed?.narrative ?? '',
          document: doc,
          errors: parsed ? [] : ['content: needs a fenced ```aide-stack-profile block holding the profile.'],
        }
      : null,
    needsInput: doc
      ? profileLayers
          .filter((l) => ['unknown', 'conflict', 'unsupported'].includes(doc.layers[l].status))
          .map((l) => ({
            layer: l,
            status: doc.layers[l].status,
            question: doc.questions.find((q) => q.layer === l)?.text ?? null,
            technology: doc.layers[l].technology,
            candidates: doc.layers[l].candidates,
          }))
      : [],
    unresolvedLayers: unresolved,
    canApprove: blocked === null,
    approveBlockedReason: blocked,
    canContinue: p.currentPhase === 'analysis' && lockedExists && (composition !== null || (latest?.status === 'locked' && doc?.generalist === true)),
    suggested: s.suggested,
    selection: doc ? { ...optionsRequestOfProfile(doc), ...(repoVcsOfProject(p) ? { vcs: { pack: repoVcsOfProject(p) ?? '' } } : {}) } : null,
    deployment: deploymentState(latest ?? null, composition),
  }
}

function deploymentState(latest: Artifact | null, composition: Composition | null): Schemas['AnalysisDeploymentDto'] {
  if (!composition) {
    return {
      available: false,
      reason: latest?.status === 'locked' ? "No stack was chosen, so there's nothing to deploy." : 'Approve the stack profile first.',
    }
  }
  const d = composition.deploy
  return d.frontendHost === null && d.backendHost === null && d.databaseHost === null
    ? { available: false, reason: 'The stack has no hosts. Choose them in Settings → Stack.' }
    : { available: true, reason: null }
}

const validation = (errors: Record<string, string[]>) => problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })

/** FR-183: the save checks the server runs on a profile (catalog options, paths); `null` when it's valid. */
function checkProfile(doc: StackProfileDocument): Record<string, string[]> | null {
  const errors: Record<string, string[]> = {}
  for (const l of ['frontend', 'backend', 'data'] as const) {
    const option = doc.layers[l].option
    const choice = stackCatalog.choices.find((c) => c.id === l)
    if (option && !choice?.options.some((o) => o.id === option && o.id !== 'none')) {
      errors[`layers.${l}.option`] = [`'${option}' is not an option of ${choice?.name ?? l}. Mark the layer 'unsupported' instead.`]
    }
  }
  for (const l of ['frontend', 'backend'] as const) {
    const path = doc.layers[l].path
    if (path && (path.startsWith('/') || path.split('/').includes('..'))) {
      errors[`layers.${l}.path`] = ["must be relative and stay inside the repository (no '..')."]
    }
  }
  return Object.keys(errors).length > 0 ? errors : null
}

/** A stack_profile artifact, or `undefined` to let the generic artifact handlers answer. */
function stackProfileOf(p: StoredProject, artifactId: unknown): Artifact | undefined {
  return p.artifacts.find((a) => a.id === artifactId && a.type === 'stack_profile')
}

const actorOf = (userId: string): Schemas['ActorDto'] => ({
  id: userId,
  name: authDb.users.find((u) => u.id === userId)?.name ?? 'Unknown',
  isSystem: false,
})

/**
 * FR-180–FR-185: GET /analysis, POST /analysis/run, and the stack_profile cases of the generic artifact routes (save
 * with validation, approve with `generalist`, STACK_PROFILE_INCOMPLETE and STACK_INCOMPATIBLE, reject re-running the
 * analyst). Other artifact types fall through to mocks/handlers/artifacts.ts, so these are registered before it.
 */
export const analysisHandlers = [
  http.get('*/api/projects/:id/analysis', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const dto = analysisDto(a.project)
    return dto ? HttpResponse.json<Analysis>(dto) : problem(404, 'NOT_FOUND', 'The project was not found.')
  }),

  http.post('*/api/projects/:id/analysis/run', async ({ request, params }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const p = a.project
    if (p.currentPhase !== 'analysis' || currentVersionOf(p)?.stackSource !== 'infer') {
      return problem(409, 'WRONG_PHASE', `The Stack Analyst runs only in the analysis phase; the project is in ${p.currentPhase}.`)
    }
    if (p.tokenBudget !== null && p.tokensUsed >= p.tokenBudget) {
      return problem(409, 'BUDGET_EXCEEDED', 'The project has used its token budget.')
    }
    const s = stored(p)
    if (s.run?.status === 'queued' || s.run?.status === 'running') {
      return problem(409, 'INVALID_STATE', 'The Stack Analyst is already queued or running. Wait for it to finish.')
    }
    s.run = makeRun('queued', { trigger: 'user_requested', createdAt: new Date().toISOString() })
    return HttpResponse.json<Schemas['StackAnalysisQueuedDto']>(
      { jobId: s.run.jobId, trigger: 'user_requested', queuedAt: s.run.createdAt },
      { status: 202 },
    )
  }),

  // FR-183: a form edit, validated before the generic draft handler saves it.
  http.post('*/api/projects/:id/artifacts/stack_profile', async ({ request, params }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await request.clone().json()) as Schemas['CreateArtifactDraftRequest'] | null
    if (a.project.currentPhase !== 'analysis') {
      return problem(409, 'WRONG_PHASE', 'The stack profile can only change during analysis.')
    }
    const parsed = parseStackProfile(body?.content ?? '')
    if (!parsed) {
      return validation({ content: ['needs a fenced ```aide-stack-profile block holding the profile.'] })
    }
    const errors = checkProfile(parsed.document)
    if (errors) {
      return validation(errors)
    }
    return undefined
  }),

  http.post('*/api/projects/:id/artifacts/:artifactId/approve', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return undefined
    }
    const p = a.project
    const artifact = stackProfileOf(p, params.artifactId)
    if (!artifact) {
      return undefined
    }
    const body = (await record(request)) as Schemas['DecideArtifactRequest'] | null
    if (artifact.status !== 'draft') {
      return problem(409, 'ARTIFACT_NOT_DRAFT', 'Only drafts can be approved.')
    }
    if (p.currentPhase !== 'analysis') {
      return problem(409, 'WRONG_PHASE', 'The stack profile is decided during analysis.')
    }
    const parsed = parseStackProfile(artifact.content)
    if (!parsed) {
      return validation({ content: ['The profile can’t be read.'] })
    }
    const actor = actorOf(a.userId)
    const generalist = body?.generalist === true
    let doc = generalist ? { ...parsed.document, generalist: true } : parsed.document
    const unresolved = unresolvedLayers(doc)
    if (unresolved.length > 0) {
      return problem(409, 'STACK_PROFILE_INCOMPLETE', `Answer these before approving: ${unresolved.join(', ')}.`, { details: unresolved })
    }
    if (!generalist) {
      const result = validateComposition(compositionRequest(p, doc))
      if (!result.ok) {
        return 'errors' in result
          ? validation(result.errors)
          : problem(400, 'STACK_INCOMPATIBLE', "These stack choices don't work together.", { violations: result.violations })
      }
    }
    let locked = artifact
    if (generalist) {
      // FR-181: the choice is recorded on a new version, which is locked with no composition.
      doc = { ...doc, generalist: true }
      locked = writeProfile(p, doc, { status: 'locked', authorUserId: a.userId, narrative: parsed.narrative })
    } else {
      artifact.status = 'locked'
    }
    locked.approvedBy = actor
    locked.approvedAt = new Date().toISOString()
    pin(p, doc)
    syncState(p, 'stack_profile')
    addEvent(p, {
      type: 'STACK_PROFILE_APPROVED',
      category: 'artifact',
      summary: `stack_profile v${String(locked.version)} approved`,
      payload: { artifactId: locked.id, version: locked.version, generalist, stackPacks: p.stackPacks },
      actor,
    })
    return HttpResponse.json<Artifact>(locked)
  }),

  http.post('*/api/projects/:id/artifacts/:artifactId/reject', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return undefined
    }
    const p = a.project
    const artifact = stackProfileOf(p, params.artifactId)
    if (!artifact) {
      return undefined
    }
    const body = (await record(request)) as Schemas['DecideArtifactRequest'] | null
    if (artifact.status !== 'draft') {
      return problem(409, 'ARTIFACT_NOT_DRAFT', 'Only drafts can be rejected.')
    }
    artifact.status = 'rejected'
    artifact.rejectionComment = body?.comment ?? null
    syncState(p, 'stack_profile')
    const rerun = artifact.author.user === null
    if (rerun) {
      stored(p).run = makeRun('queued', { trigger: 'artifact_rejected', createdAt: new Date().toISOString() })
    }
    addEvent(p, {
      type: 'STACK_PROFILE_REJECTED',
      category: 'artifact',
      summary: `stack_profile v${String(artifact.version)} rejected`,
      payload: { artifactId: artifact.id, version: artifact.version, rerun },
      actor: actorOf(a.userId),
    })
    return HttpResponse.json<Artifact>(artifact)
  }),
]
