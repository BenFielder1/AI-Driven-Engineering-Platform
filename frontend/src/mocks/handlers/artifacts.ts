import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, nextId } from './auth'
import { createExecutionSteps, invalidPlan, validatePlan } from './execution'
import { addEvent } from './lifecycle'
import { problem } from './problem'
import { access, ownerMutation, record, type Access, type StoredProject } from './projects'

type Artifact = Schemas['ArtifactDto']
type Summary = Schemas['ArtifactSummaryDto']
type ArtifactType = Schemas['ArtifactType']
type DiffLine = Schemas['DiffLineDto']
type Actor = Schemas['ActorDto']

const types: readonly ArtifactType[] = ['project_request', 'technical_specification', 'implementation_plan', 'optimization_plan']

/** SPEC §4.4: what approving a new version of each type invalidates. */
const cascade: Record<ArtifactType, readonly ArtifactType[]> = {
  project_request: ['technical_specification', 'implementation_plan', 'optimization_plan'],
  technical_specification: ['implementation_plan', 'optimization_plan'],
  implementation_plan: ['optimization_plan'],
  optimization_plan: [],
}

/** A deterministic 64-hex-digit stand-in for the SHA-256 content hash (the mock never verifies it). */
export function fakeHash(content: string): string {
  const normalised = content.replaceAll('\r\n', '\n')
  let out = ''
  for (let seed = 0; seed < 8; seed += 1) {
    let h = 0x811c9dc5 ^ seed
    for (let i = 0; i < normalised.length; i += 1) {
      h = Math.imul(h ^ normalised.charCodeAt(i), 0x01000193)
    }
    out += (h >>> 0).toString(16).padStart(8, '0')
  }
  return out
}

function userActor(userId: string): Actor {
  const user = authDb.users.find((u) => u.id === userId)
  return { id: userId, name: user?.name ?? 'Unknown', isSystem: false }
}

// ---- Seeding helpers (tests) ------------------------------------------------------------------------------------

let artifactClock = Date.parse('2026-09-02T09:00:00Z')

/**
 * Seeds the next version of `type` (version = latest + 1, parent = latest). Agent-authored unless `authorUserId` is
 * given. `integrity: 'corrupted'` simulates content altered in the DB (FR-20).
 */
export function seedArtifact(
  project: StoredProject,
  init: {
    type: ArtifactType
    content: string
    status?: NonNullable<Artifact['status']>
    integrity?: Artifact['integrity']
    authorUserId?: string
    approvedByUserId?: string
    rejectionComment?: string
  },
): Artifact {
  artifactClock += 3_600_000
  const previous = latestOf(project, init.type)
  const status = init.status ?? 'draft'
  const createdAt = new Date(artifactClock).toISOString()
  const artifact: Artifact = {
    id: nextId(),
    type: init.type,
    version: (previous?.version ?? 0) + 1,
    parentVersion: previous?.version ?? null,
    content: init.content,
    contentHash: fakeHash(init.content),
    status,
    projectVersionAtCreation: project.projectVersion,
    author: init.authorUserId
      ? { user: userActor(init.authorUserId), agentRunId: null }
      : { user: null, agentRunId: nextId() },
    approvedBy: status === 'locked' ? (init.approvedByUserId ? userActor(init.approvedByUserId) : userActor(authDb.currentUserId ?? '')) : null,
    approvedAt: status === 'locked' ? createdAt : null,
    rejectionComment: init.rejectionComment ?? null,
    createdAt,
    integrity: init.integrity ?? 'ok',
  }
  project.artifacts.push(artifact)
  syncState(project, init.type)
  return artifact
}

/** Seeds `contents.length` versions of `type`: every one locked except the last, which gets `lastStatus` (draft). */
export function seedArtifactHistory(
  project: StoredProject,
  type: ArtifactType,
  contents: readonly string[],
  lastStatus: NonNullable<Artifact['status']> = 'draft',
): Artifact[] {
  return contents.map((content, i) =>
    seedArtifact(project, { type, content, status: i === contents.length - 1 ? lastStatus : 'locked' }),
  )
}

// ---- Domain simulation (SPEC §4.3, §4.4) ------------------------------------------------------------------------

function versionsOf(p: StoredProject, type: ArtifactType): Artifact[] {
  return p.artifacts.filter((a) => a.type === type).sort((a, b) => b.version - a.version)
}

function latestOf(p: StoredProject, type: ArtifactType): Artifact | undefined {
  return versionsOf(p, type)[0]
}

/** Keeps GET /state's artifact summary (§6) in line with the stored versions. */
function syncState(p: StoredProject, type: ArtifactType) {
  const all = versionsOf(p, type)
  const latest = all[0]
  const summary = {
    type,
    latestVersion: latest?.version ?? null,
    latestStatus: latest?.status ?? null,
    currentLockedVersion: all.find((a) => a.status === 'locked')?.version ?? null,
  }
  const existing = p.state.artifacts.find((a) => a.type === type)
  if (existing) {
    Object.assign(existing, summary)
  } else {
    p.state.artifacts.push(summary)
  }
}

function toSummary(a: Artifact): Summary {
  const { id, type, version, status, integrity, createdAt, author } = a
  return { id, type, version, status, integrity, createdAt, author }
}

function parseType(value: unknown): ArtifactType | Response {
  const type = types.find((t) => t === value)
  return type ?? problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { type: ['Unknown artifact type.'] } })
}

/** FR-20: every read re-checks the hash; a corrupted read raises the integrity alert once. */
function read(p: StoredProject, a: Artifact): Artifact {
  if (a.integrity === 'corrupted' && a.status !== 'corrupted') {
    a.status = 'corrupted'
    addEvent(p, {
      type: 'INTEGRITY_ALERT',
      category: 'integrity',
      summary: `Integrity alert: ${a.type} v${String(a.version)}`,
      payload: { artifactId: a.id, type: a.type, version: a.version },
    })
  }
  return a
}

/** Line diff (longest common subsequence), in order: what the API's `lines` look like. */
export function lineDiff(from: string, to: string): DiffLine[] {
  const a = from.replaceAll('\r\n', '\n').split('\n')
  const b = to.replaceAll('\r\n', '\n').split('\n')
  const lcs: number[][] = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0))
  for (let i = a.length - 1; i >= 0; i -= 1) {
    for (let j = b.length - 1; j >= 0; j -= 1) {
      const row = lcs[i] ?? []
      row[j] = a[i] === b[j] ? (lcs[i + 1]?.[j + 1] ?? 0) + 1 : Math.max(lcs[i + 1]?.[j] ?? 0, row[j + 1] ?? 0)
    }
  }
  const lines: DiffLine[] = []
  let i = 0
  let j = 0
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) {
      lines.push({ kind: 'context', oldLine: i + 1, newLine: j + 1, text: a[i] ?? '', skipped: null })
      i += 1
      j += 1
    } else if (i < a.length && (j >= b.length || (lcs[i + 1]?.[j] ?? 0) >= (lcs[i]?.[j + 1] ?? 0))) {
      lines.push({ kind: 'removed', oldLine: i + 1, newLine: null, text: a[i] ?? '', skipped: null })
      i += 1
    } else {
      lines.push({ kind: 'added', oldLine: null, newLine: j + 1, text: b[j] ?? '', skipped: null })
      j += 1
    }
  }
  return lines
}

/** The API's diff limit (backend LineDiff.MaxTotalLines): larger pairs are 400 VALIDATION on `to`. */
export const maxDiffLines = 100_000

/**
 * Like the API's structured diff (backend LineDiff.Collapse): each change keeps `context` unchanged lines either side
 * (so hunks closer than 2 × context merge); every other run of unchanged lines becomes one `skipped` entry carrying
 * the first hidden line's numbers. Identical versions are a single `skipped` entry.
 */
export function collapseDiff(lines: readonly DiffLine[], context = 3): DiffLine[] {
  const keep = new Array<boolean>(lines.length).fill(false)
  lines.forEach((line, i) => {
    if (line.kind === 'added' || line.kind === 'removed') {
      keep.fill(true, Math.max(0, i - context), Math.min(lines.length, i + context + 1))
    }
  })
  const out: DiffLine[] = []
  let i = 0
  while (i < lines.length) {
    const line = lines[i]
    if (!line) {
      break
    }
    if (keep[i]) {
      out.push(line)
      i += 1
      continue
    }
    let end = i
    while (end < lines.length && !keep[end]) {
      end += 1
    }
    out.push({ kind: 'skipped', oldLine: line.oldLine, newLine: line.newLine, text: '', skipped: end - i })
    i = end
  }
  return out
}

function unifiedDiff(lines: DiffLine[]): string {
  const prefix = { context: ' ', added: '+', removed: '-', skipped: '' } as const
  return lines.map((l) => `${prefix[l.kind]}${l.text}`).join('\n')
}

/** Writer (Contributor/Owner/Admin) access, including antiforgery. */
export function writerMutation(request: Request, id: unknown): Access {
  const a = access(id)
  if (a instanceof Response) {
    return a
  }
  const rejected = antiforgeryFailure(request)
  if (rejected) {
    return rejected
  }
  return a.level === 'viewer' ? problem(403, 'FORBIDDEN', 'Viewers can’t change the project.') : a
}

const notFound = () => problem(404, 'NOT_FOUND', 'The artifact was not found.')

function findById(p: StoredProject, artifactId: unknown): Artifact | Response {
  return p.artifacts.find((a) => a.id === artifactId) ?? notFound()
}

export const artifactHandlers = [
  // FR-21: latest version of each type that has one.
  http.get('*/api/projects/:id/artifacts', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const latest = types.flatMap((t) => {
      const v = latestOf(a.project, t)
      return v ? [toSummary(read(a.project, v))] : []
    })
    return HttpResponse.json<Summary[]>(latest)
  }),

  http.get('*/api/projects/:id/artifacts/:type/latest', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const type = parseType(params.type)
    if (type instanceof Response) {
      return type
    }
    const latest = latestOf(a.project, type)
    return latest ? HttpResponse.json<Artifact>(read(a.project, latest)) : notFound()
  }),

  http.get('*/api/projects/:id/artifacts/:type/history', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const type = parseType(params.type)
    if (type instanceof Response) {
      return type
    }
    return HttpResponse.json<Summary[]>(versionsOf(a.project, type).map((v) => toSummary(read(a.project, v))))
  }),

  http.get('*/api/projects/:id/artifacts/:type/versions/:version', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const type = parseType(params.type)
    if (type instanceof Response) {
      return type
    }
    const found = versionsOf(a.project, type).find((v) => v.version === Number(params.version))
    return found ? HttpResponse.json<Artifact>(read(a.project, found)) : notFound()
  }),

  // FR-21: diff between any two versions.
  http.get('*/api/projects/:id/artifacts/:type/diff', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const type = parseType(params.type)
    if (type instanceof Response) {
      return type
    }
    const q = new URL(request.url).searchParams
    const from = Number(q.get('from'))
    const to = Number(q.get('to'))
    if (!Number.isInteger(from) || !Number.isInteger(to) || from < 1 || to < 1) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { from: ['Use version numbers from 1.'] },
      })
    }
    const all = versionsOf(a.project, type)
    const left = all.find((v) => v.version === from)
    const right = all.find((v) => v.version === to)
    if (!left || !right) {
      return notFound()
    }
    const total = left.content.split('\n').length + right.content.split('\n').length
    if (total > maxDiffLines) {
      const message = `The two versions have ${String(total)} lines together; diffs are limited to ${String(maxDiffLines)} lines.`
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors: { to: [message] } })
    }
    const lines = lineDiff(left.content, right.content)
    return HttpResponse.json<Schemas['ArtifactDiffDto']>({ from, to, unifiedDiff: unifiedDiff(lines), lines: collapseDiff(lines) })
  }),

  // FR-18: a new draft from edited Markdown (writers); the previous draft is superseded (§4.3).
  http.post('*/api/projects/:id/artifacts/:type', async ({ request, params }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const type = parseType(params.type)
    if (type instanceof Response) {
      return type
    }
    const body = (await record(request)) as Schemas['CreateArtifactDraftRequest'] | null
    const content = body?.content ?? ''
    if (!content.trim()) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { content: ['Content is required.'] },
      })
    }
    const p = a.project
    // FR-18, FR-29: an edited plan is validated from its aide-plan block (the backend also re-renders the prose).
    if (type === 'implementation_plan' || type === 'optimization_plan') {
      const plan = validatePlan(content)
      if ('reasons' in plan) {
        return invalidPlan(plan.reasons)
      }
    }
    // FR-18: an edit made from `baseVersion` may only be saved while that is still the latest version of the type.
    const latest = latestOf(p, type)?.version ?? null
    const base = body?.baseVersion ?? null
    if (base !== null && base !== latest) {
      return problem(409, 'CONFLICT', 'A newer version was saved since this edit started.', {
        details: [`latestVersion: ${latest === null ? 'none' : String(latest)}`],
      })
    }
    for (const draft of p.artifacts.filter((v) => v.type === type && v.status === 'draft')) {
      draft.status = 'superseded'
    }
    const created = seedArtifact(p, { type, content, authorUserId: a.userId })
    created.createdAt = new Date().toISOString()
    addEvent(p, {
      type: 'ARTIFACT_CREATED',
      category: 'artifact',
      summary: `${type} v${String(created.version)} created`,
      payload: { artifactId: created.id, type, version: created.version },
      actor: userActor(a.userId),
    })
    return HttpResponse.json<Artifact>(created, { status: 201 })
  }),

  // FR-17, FR-19: approve a draft (Owners). Approving a locked version is a no-op.
  http.post('*/api/projects/:id/artifacts/:artifactId/approve', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const p = a.project
    const artifact = findById(p, params.artifactId)
    if (artifact instanceof Response) {
      return artifact
    }
    if (artifact.status === 'locked') {
      return HttpResponse.json<Artifact>(artifact)
    }
    if (artifact.status !== 'draft') {
      return problem(409, 'ARTIFACT_NOT_DRAFT', 'Only drafts can be approved.')
    }
    // FR-30: approving a plan creates its execution steps from the aide-plan block.
    if (artifact.type === 'implementation_plan' || artifact.type === 'optimization_plan') {
      const plan = validatePlan(artifact.content)
      if ('reasons' in plan) {
        return invalidPlan(plan.reasons)
      }
      createExecutionSteps(p, artifact, plan.steps)
    }
    const actor = userActor(a.userId)
    artifact.status = 'locked'
    artifact.approvedBy = actor
    artifact.approvedAt = new Date().toISOString()
    addEvent(p, {
      type: 'ARTIFACT_APPROVED',
      category: 'artifact',
      summary: `${artifact.type} v${String(artifact.version)} approved`,
      payload: { artifactId: artifact.id, type: artifact.type, version: artifact.version },
      actor,
    })
    for (const downstream of cascade[artifact.type]) {
      for (const v of p.artifacts.filter((x) => x.type === downstream && x.status === 'locked')) {
        v.status = 'invalidated'
        addEvent(p, {
          type: 'ARTIFACT_INVALIDATED',
          category: 'artifact',
          summary: `${v.type} v${String(v.version)} invalidated`,
          payload: { artifactId: v.id, type: v.type, version: v.version },
          actor,
        })
      }
      syncState(p, downstream)
    }
    syncState(p, artifact.type)
    return HttpResponse.json<Artifact>(artifact)
  }),

  // FR-17: reject a draft (Owners); an agent-produced draft re-runs the agent with the comment.
  http.post('*/api/projects/:id/artifacts/:artifactId/reject', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['DecideArtifactRequest'] | null
    const p = a.project
    const artifact = findById(p, params.artifactId)
    if (artifact instanceof Response) {
      return artifact
    }
    if (artifact.status === 'locked') {
      return problem(409, 'ARTIFACT_LOCKED', 'The artifact is locked.')
    }
    if (artifact.status !== 'draft') {
      return problem(409, 'ARTIFACT_NOT_DRAFT', 'Only drafts can be rejected.')
    }
    const actor = userActor(a.userId)
    artifact.status = 'rejected'
    artifact.rejectionComment = body?.comment?.trim() ? body.comment : null
    addEvent(p, {
      type: 'ARTIFACT_REJECTED',
      category: 'artifact',
      summary: `${artifact.type} v${String(artifact.version)} rejected`,
      payload: { artifactId: artifact.id, type: artifact.type, version: artifact.version },
      actor,
    })
    if (artifact.author.agentRunId) {
      addEvent(p, { type: 'RUN_STARTED', category: 'run', summary: 'Agent re-running with feedback' })
    }
    syncState(p, artifact.type)
    return HttpResponse.json<Artifact>(artifact)
  }),
]
