import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { authDb, nextId } from './auth'
import { addEvent } from './lifecycle'
import { problem } from './problem'
import { access, currentVersionId, currentVersionOf, developmentVersionOf, ownerMutation, record, type StoredProject } from './projects'

type Release = Schemas['ReleaseDto']
type MergeReadiness = Schemas['MergeReadinessDto']
type Rollback = Schemas['RollbackDto']
type Checkpoint = Schemas['CheckpointDto']
type Phase = Schemas['Phase']

/** One rollback per project per minute (FR-45). */
const ROLLBACK_WINDOW_MS = 60_000

// ---- Seeding helpers (tests) ------------------------------------------------------------------------------------

/** Puts the project in release / releasing, as after the pre-release gate is approved (FR-42). */
export function seedReleasePhase(p: StoredProject) {
  p.currentPhase = 'release'
  p.status = 'releasing'
  p.phaseAtVersion[p.checkpoint] = 'release'
}

/** Appends a release attempt (newest last). Defaults: a succeeded, merged and tagged release of the current version. */
export function seedRelease(p: StoredProject, overrides: Partial<Release> = {}): Release {
  const releases = (p.releases ??= [])
  const release: Release = {
    id: nextId(),
    releaseVersionId: currentVersionId(p),
    checkpoint: p.checkpoint,
    status: 'succeeded',
    tag: `v${currentVersionOf(p)?.version ?? '0.1.0'}`,
    mergeCommitSha: '3f2a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f',
    pullRequestUrl: 'https://github.com/acme/inventory/pull/42',
    conflictingFiles: [],
    errorCode: null,
    errorMessage: null,
    attempt: releases.filter((r) => r.checkpoint === (overrides.checkpoint ?? p.checkpoint)).length + 1,
    startedAt: new Date(Date.parse('2026-09-05T10:00:00Z') + releases.length * 3_600_000).toISOString(),
    completedAt: new Date(Date.parse('2026-09-05T10:05:00Z') + releases.length * 3_600_000).toISOString(),
    githubReleaseUrl: null,
    awaitingMergeSince: null,
    mergeReasons: null,
    mergeStrategy: p.git.mergeStrategy,
    ...overrides,
  }
  releases.push(release)
  return release
}

/** Records the phase entered at each checkpoint 1…N (FR-44, FR-63 picker), e.g. `['refinement', 'specification', …]`. */
export function seedVersions(p: StoredProject, phasesByVersion: readonly Phase[]) {
  phasesByVersion.forEach((phase, index) => {
    p.phaseAtVersion[index + 1] = phase
  })
  p.checkpoint = phasesByVersion.length
  const current = phasesByVersion.at(-1)
  if (current) {
    p.currentPhase = current
  }
}

/** FR-84: a merge-readiness result. Defaults: mergeable under `aide_merges`, with one passing required check. */
export function mergeReadiness(overrides: Partial<MergeReadiness> = {}): MergeReadiness {
  const reasons = overrides.reasons ?? []
  const canMerge = overrides.canMerge ?? reasons.every((r) => r.code === 'manual_strategy')
  const mergeStrategy = overrides.mergeStrategy ?? 'aide_merges'
  return {
    canMerge,
    willAutoMerge: canMerge && mergeStrategy === 'aide_merges',
    mergeStrategy,
    reasons,
    checks: [
      { name: 'ci / test', status: 'completed', conclusion: 'success', required: true, detailsUrl: 'https://github.com/acme/inventory/actions/runs/1' },
    ],
    mergeStateStatus: canMerge ? 'CLEAN' : 'BLOCKED',
    reviewDecision: canMerge ? 'APPROVED' : null,
    checkedAt: '2026-09-05T09:55:00Z',
    ...overrides,
  }
}

/** Puts the current release attempt into `awaiting_merge` (FR-65), with the version awaiting merge too. */
export function seedAwaitingMerge(p: StoredProject, overrides: Partial<Release> = {}): Release {
  seedReleasePhase(p)
  p.status = 'awaiting_merge'
  const development = developmentVersionOf(p)
  if (development) {
    development.status = 'awaiting_merge'
  }
  return seedRelease(p, {
    status: 'awaiting_merge',
    mergeCommitSha: null,
    completedAt: null,
    awaitingMergeSince: '2026-09-05T10:01:00Z',
    mergeReasons: [{ code: 'review_required', message: 'The default branch requires 1 approving review.' }],
    ...overrides,
  })
}

// ---- Derivations --------------------------------------------------------------------------------------------------

function actorFor(userId: string): Schemas['ActorDto'] {
  const user = authDb.users.find((u) => u.id === userId)
  return { id: userId, name: user?.name ?? 'Unknown', isSystem: false }
}

/** §4.8, §4.10: the current version's checkpoints as /checkpoints reports them, with their rollback eligibility. */
export function checkpointsOf(p: StoredProject): Checkpoint[] {
  const closed = developmentVersionOf(p) === undefined
  return Object.entries(p.phaseAtVersion)
    .map(([v, phase]) => ({ version: Number(v), phase }))
    .filter((v) => v.version <= p.checkpoint)
    .sort((a, b) => a.version - b.version)
    .map(({ version, phase }) => {
      const blocked = closed
        ? ('version_released' as const)
        : version >= p.checkpoint
          ? ('current_checkpoint' as const)
          : phase === 'release'
            ? ('release_phase' as const)
            : null
      return {
        checkpoint: version,
        phase,
        enteredAt: new Date(Date.parse('2026-09-01T10:00:00Z') + version * 3_600_000).toISOString(),
        rollbackAllowed: blocked === null,
        rollbackBlockedReason: blocked,
      }
    })
}

const notFound = () => problem(404, 'NOT_FOUND', 'No release has started yet.')

export const releaseHandlers = [
  // FR-84: merge readiness of the version's PR (members; review and release phases only, else 409 WRONG_PHASE).
  http.get('*/api/projects/:id/git/merge-readiness', async ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const p = a.project
    if (p.currentPhase !== 'review' && p.currentPhase !== 'release') {
      return problem(409, 'WRONG_PHASE', 'Merge readiness is checked in the review and release phases.')
    }
    const refresh = new URL(request.url).searchParams.get('refresh') === 'true'
    if (refresh) {
      await record(request)
      const next = p.mergeReadinessOnRefresh ?? p.mergeReadiness ?? mergeReadiness({ mergeStrategy: p.git.mergeStrategy })
      p.mergeReadiness = { ...next, checkedAt: new Date().toISOString() }
      p.mergeReadinessOnRefresh = undefined
    }
    p.mergeReadiness ??= mergeReadiness({
      mergeStrategy: p.git.mergeStrategy,
      reasons:
        p.git.mergeStrategy === 'manual'
          ? [{ code: 'manual_strategy', message: 'The project merges manually: merge the pull request on GitHub.' }]
          : [],
    })
    return HttpResponse.json<MergeReadiness>(p.mergeReadiness)
  }),

  // FR-42, FR-43, FR-64: release attempts of a release version (default: the current one), newest first.
  http.get('*/api/projects/:id/releases', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const versionId = new URL(request.url).searchParams.get('releaseVersionId') ?? currentVersionId(a.project)
    return HttpResponse.json<Release[]>(
      [...(a.project.releases ?? [])].filter((r) => r.releaseVersionId === versionId).reverse(),
    )
  }),

  http.get('*/api/projects/:id/releases/latest', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const latest = a.project.releases?.at(-1)
    return latest ? HttpResponse.json<Release>(latest) : notFound()
  }),

  // FR-43: retry a failed release (Owner).
  http.post('*/api/projects/:id/release', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const p = a.project
    if (p.currentPhase !== 'release' || p.status !== 'releasing') {
      return problem(409, 'NOT_RELEASING', 'The project is not waiting to be released.')
    }
    if (p.releases?.at(-1)?.status === 'in_progress') {
      return problem(409, 'RELEASE_IN_PROGRESS', 'A release job is queued or running.')
    }
    const release = seedRelease(p, {
      status: 'in_progress',
      mergeCommitSha: null,
      pullRequestUrl: null,
      completedAt: null,
      startedAt: new Date().toISOString(),
    })
    addEvent(p, {
      type: 'RELEASE_STARTED',
      category: 'release',
      summary: `Release ${release.tag} started`,
      payload: { releaseId: release.id, checkpoint: release.checkpoint, tag: release.tag, attempt: release.attempt },
      actor: actorFor(a.userId),
    })
    return HttpResponse.json<Release>(release, { status: 202 })
  }),

  // FR-44, FR-63: checkpoints of the current release version for the rollback picker (a past version has none here:
  // the mock only records the current version's checkpoints).
  http.get('*/api/projects/:id/checkpoints', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const requested = new URL(request.url).searchParams.get('releaseVersionId')
    if (requested && !a.project.releaseVersions.some((v) => v.id === requested)) {
      return problem(404, 'NOT_FOUND', 'The release version was not found.')
    }
    return HttpResponse.json<Checkpoint[]>(
      requested && requested !== currentVersionId(a.project) ? [] : [...checkpointsOf(a.project)].reverse(),
    )
  }),

  // FR-44: recorded rollbacks, newest first.
  http.get('*/api/projects/:id/rollbacks', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Rollback[]>([...(a.project.rollbacks ?? [])].reverse())
  }),

  // FR-44, FR-45, FR-63, §4.8: roll back (Owner). Checks in the API's order: VALIDATION, ROLLBACK_BLOCKED
  // (version_released / release_partially_applied), INVALID_TARGET_VERSION, ROLLBACK_BLOCKED (running work), RATE_LIMITED.
  http.post('*/api/projects/:id/rollback', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Partial<Schemas['RollbackRequest']> | null
    const p = a.project
    const target = body?.targetCheckpoint
    const reason = typeof body?.reason === 'string' ? body.reason.trim() : ''
    const errors: Record<string, string[]> = {}
    if (typeof target !== 'number' || !Number.isInteger(target) || target < 1) {
      errors.targetCheckpoint = ['The target version must be 1 or more.']
    }
    if (reason.length < 1 || reason.length > 500) {
      errors.reason = ['The reason must be 1 to 500 characters.']
    }
    if (Object.keys(errors).length > 0 || typeof target !== 'number') {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })
    }
    const development = developmentVersionOf(p)
    if (!development) {
      return problem(409, 'ROLLBACK_BLOCKED', 'The version is released.', { details: ['reason: version_released'] })
    }
    if (development.status === 'awaiting_merge') {
      return problem(409, 'ROLLBACK_BLOCKED', 'The release is awaiting merge.', {
        details: ['reason: release_partially_applied'],
      })
    }
    const version = checkpointsOf(p).find((v) => v.checkpoint === target)
    if (!version?.rollbackAllowed) {
      return problem(400, 'INVALID_TARGET_VERSION', 'The project cannot be rolled back to that version.')
    }
    if (p.rollbackBlocked) {
      return problem(409, 'ROLLBACK_BLOCKED', 'A step or agent run is in progress.')
    }
    const now = Date.now()
    if (p.lastRollbackAt !== undefined && now - p.lastRollbackAt < ROLLBACK_WINDOW_MS) {
      const seconds = Math.ceil((ROLLBACK_WINDOW_MS - (now - p.lastRollbackAt)) / 1000)
      return problem(429, 'RATE_LIMITED', 'Only one rollback per minute is allowed.', {
        headers: { 'Retry-After': String(seconds) },
      })
    }

    const fromCheckpoint = p.checkpoint
    const invalidated = p.artifacts.filter((x) => x.checkpointAtCreation > target && x.status !== 'invalidated')
    for (const artifact of invalidated) {
      artifact.status = 'invalidated'
    }
    const cancelledGates = p.gates.filter((g) => g.status === 'pending')
    for (const gate of cancelledGates) {
      gate.status = 'cancelled'
    }
    p.checkpoint = target
    p.currentPhase = version.phase
    p.status = 'rolled_back'
    p.lastRollbackAt = now
    p.updatedAt = new Date(now).toISOString()
    p.state.review = { ...p.state.review, cycle: 0, latestScore: null, latestDecision: null, driftLevel: null, status: 'idle' }
    const rollback: Rollback = {
      id: nextId(),
      releaseVersionId: development.id,
      fromCheckpoint,
      toCheckpoint: target,
      restoredPhase: version.phase,
      reason,
      invalidatedArtifactIds: invalidated.map((x) => x.id),
      resetStepIds: [],
      requeuedStepIds: [],
      cancelledGateIds: cancelledGates.map((g) => g.id),
      cancelledJobIds: [],
      createdAt: new Date(now).toISOString(),
      createdBy: actorFor(a.userId),
    }
    ;(p.rollbacks ??= []).push(rollback)
    addEvent(p, {
      type: 'ROLLBACK_COMPLETED',
      category: 'rollback',
      summary: `Rolled back from checkpoint ${String(fromCheckpoint)} to checkpoint ${String(target)}`,
      payload: { rollbackId: rollback.id, releaseVersionId: development.id, fromCheckpoint, targetCheckpoint: target, reason },
      actor: rollback.createdBy,
    })
    return HttpResponse.json<Rollback>(rollback)
  }),
]
