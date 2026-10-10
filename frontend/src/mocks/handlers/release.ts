import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { authDb, nextId } from './auth'
import { addEvent } from './lifecycle'
import { problem } from './problem'
import { access, ownerMutation, record, type StoredProject } from './projects'

type Release = Schemas['ReleaseDto']
type Rollback = Schemas['RollbackDto']
type ProjectVersion = Schemas['ProjectVersionDto']
type Phase = Schemas['Phase']

/** One rollback per project per minute (FR-45). */
const ROLLBACK_WINDOW_MS = 60_000

// ---- Seeding helpers (tests) ------------------------------------------------------------------------------------

/** Puts the project in release / releasing, as after the pre-release gate is approved (FR-42). */
export function seedReleasePhase(p: StoredProject) {
  p.currentPhase = 'release'
  p.status = 'releasing'
  p.phaseAtVersion[p.projectVersion] = 'release'
}

/** Appends a release attempt (newest last). Defaults: a succeeded, merged and tagged release of the current version. */
export function seedRelease(p: StoredProject, overrides: Partial<Release> = {}): Release {
  const releases = (p.releases ??= [])
  const release: Release = {
    id: nextId(),
    projectVersion: p.projectVersion,
    status: 'succeeded',
    tag: `v${String(p.projectVersion)}-aide`,
    mergeCommitSha: '3f2a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f',
    pullRequestUrl: 'https://github.com/acme/inventory/pull/42',
    conflictingFiles: [],
    errorCode: null,
    errorMessage: null,
    attempt: releases.filter((r) => r.projectVersion === (overrides.projectVersion ?? p.projectVersion)).length + 1,
    startedAt: new Date(Date.parse('2026-09-05T10:00:00Z') + releases.length * 3_600_000).toISOString(),
    completedAt: new Date(Date.parse('2026-09-05T10:05:00Z') + releases.length * 3_600_000).toISOString(),
    ...overrides,
  }
  releases.push(release)
  return release
}

/** Records the phase entered at each version 1…N (FR-44 version picker), e.g. `['refinement', 'specification', …]`. */
export function seedVersions(p: StoredProject, phasesByVersion: readonly Phase[]) {
  phasesByVersion.forEach((phase, index) => {
    p.phaseAtVersion[index + 1] = phase
  })
  p.projectVersion = phasesByVersion.length
  const current = phasesByVersion.at(-1)
  if (current) {
    p.currentPhase = current
  }
}

// ---- Derivations --------------------------------------------------------------------------------------------------

function actorFor(userId: string): Schemas['ActorDto'] {
  const user = authDb.users.find((u) => u.id === userId)
  return { id: userId, name: user?.name ?? 'Unknown', isSystem: false }
}

/** §4.8: versions as /versions reports them, with their rollback eligibility. */
export function versionsOf(p: StoredProject): ProjectVersion[] {
  return Object.entries(p.phaseAtVersion)
    .map(([v, phase]) => ({ version: Number(v), phase }))
    .filter((v) => v.version <= p.projectVersion)
    .sort((a, b) => a.version - b.version)
    .map(({ version, phase }) => {
      const blocked =
        version >= p.projectVersion ? ('current_version' as const) : phase === 'release' ? ('release_phase' as const) : null
      return {
        version,
        phase,
        enteredAt: new Date(Date.parse('2026-09-01T10:00:00Z') + version * 3_600_000).toISOString(),
        rollbackAllowed: blocked === null,
        rollbackBlockedReason: blocked,
      }
    })
}

const notFound = () => problem(404, 'NOT_FOUND', 'No release has started yet.')

export const releaseHandlers = [
  // FR-42, FR-43: release attempts, newest first.
  http.get('*/api/projects/:id/releases', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Release[]>([...(a.project.releases ?? [])].reverse())
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
      payload: { releaseId: release.id, projectVersion: release.projectVersion, tag: release.tag, attempt: release.attempt },
      actor: actorFor(a.userId),
    })
    return HttpResponse.json<Release>(release, { status: 202 })
  }),

  // FR-44: project versions for the rollback picker.
  http.get('*/api/projects/:id/versions', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<ProjectVersion[]>(versionsOf(a.project))
  }),

  // FR-44: recorded rollbacks, newest first.
  http.get('*/api/projects/:id/rollbacks', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<Rollback[]>([...(a.project.rollbacks ?? [])].reverse())
  }),

  // FR-44, FR-45, §4.8: roll back (Owner). Checks in the API's order: VALIDATION, INVALID_TARGET_VERSION,
  // ROLLBACK_BLOCKED, RATE_LIMITED.
  http.post('*/api/projects/:id/rollback', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Partial<Schemas['RollbackRequest']> | null
    const p = a.project
    const target = body?.targetVersion
    const reason = typeof body?.reason === 'string' ? body.reason.trim() : ''
    const errors: Record<string, string[]> = {}
    if (typeof target !== 'number' || !Number.isInteger(target) || target < 1) {
      errors.targetVersion = ['The target version must be 1 or more.']
    }
    if (reason.length < 1 || reason.length > 500) {
      errors.reason = ['The reason must be 1 to 500 characters.']
    }
    if (Object.keys(errors).length > 0 || typeof target !== 'number') {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', { errors })
    }
    const version = versionsOf(p).find((v) => v.version === target)
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

    const fromVersion = p.projectVersion
    const invalidated = p.artifacts.filter((x) => x.projectVersionAtCreation > target && x.status !== 'invalidated')
    for (const artifact of invalidated) {
      artifact.status = 'invalidated'
    }
    const cancelledGates = p.gates.filter((g) => g.status === 'pending')
    for (const gate of cancelledGates) {
      gate.status = 'cancelled'
    }
    p.projectVersion = target
    p.currentPhase = version.phase
    p.status = 'rolled_back'
    p.lastRollbackAt = now
    p.updatedAt = new Date(now).toISOString()
    p.state.review = { ...p.state.review, cycle: 0, latestScore: null, latestDecision: null, driftLevel: null, status: 'idle' }
    const rollback: Rollback = {
      id: nextId(),
      fromVersion,
      toVersion: target,
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
      summary: `Rolled back from version ${String(fromVersion)} to version ${String(target)}`,
      payload: { rollbackId: rollback.id, fromVersion, targetVersion: target, reason },
      actor: rollback.createdBy,
    })
    return HttpResponse.json<Rollback>(rollback)
  }),
]
