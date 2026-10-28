import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { checkNewVersion, normalizeVersion } from '../../features/versions/semver'
import { authDb } from './auth'
import { addEvent } from './lifecycle'
import { problem } from './problem'
import {
  access,
  developmentVersionOf,
  makeReleaseVersion,
  ownerMutation,
  record,
  releasedVersionOf,
  releaseVersionsOf,
  stateDto,
  type StoredProject,
} from './projects'

type ReleaseVersion = Schemas['ReleaseVersionDto']

// ---- Seeding helpers (tests) ------------------------------------------------------------------------------------

/**
 * FR-62 seed: a project released as `released` (merged) and, when `development` is given, developing that version
 * (refinement / active at checkpoint 1). Older released versions can be listed in `history` (oldest first).
 */
export function seedReleaseVersions(
  p: StoredProject,
  { released, development, history = [] }: { released?: string; development?: string; history?: string[] },
) {
  const rows: ReleaseVersion[] = [
    ...history.map((v) => makeReleaseVersion({ version: v, status: 'released' })),
    ...(released ? [makeReleaseVersion({ version: released, status: 'released', checkpoints: { current: 6, max: 6 } })] : []),
    ...(development ? [makeReleaseVersion({ version: development, status: 'in_development', baseTag: released ? `v${released}` : null })] : []),
  ]
  p.releaseVersions = rows
  if (!development && released) {
    p.status = 'released'
    p.currentPhase = 'release'
  }
  return rows
}

// ---- Handlers ---------------------------------------------------------------------------------------------------

export const versionHandlers = [
  // FR-62: the project's release versions, highest first.
  http.get('*/api/projects/:id/versions', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json<ReleaseVersion[]>(releaseVersionsOf(a.project))
  }),

  // FR-60, FR-61: start a new version (Owner). Checks in the domain's order (ReleaseVersionRules.CanStart), then TAG_EXISTS.
  http.post('*/api/projects/:id/versions', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Partial<Schemas['StartReleaseVersionRequest']> | null
    const p = a.project
    const requestText = typeof body?.request === 'string' ? body.request.trim() : ''
    if (!requestText) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { request: ['The request is required.'] },
      })
    }
    const version = normalizeVersion(body?.version)
    if (!version) {
      return problem(400, 'VERSION_INVALID', `'${body?.version ?? ''}' is not a valid version.`)
    }
    const development = developmentVersionOf(p)
    if (development?.status === 'awaiting_merge') {
      return problem(409, 'RELEASE_NOT_MERGED', `Version ${development.version} is waiting for its pull request to be merged.`)
    }
    if (development) {
      return problem(409, 'VERSION_IN_PROGRESS', `Version ${development.version} is already in development.`)
    }
    if (p.status !== 'released') {
      return problem(409, 'NOT_RELEASED', 'The project is not released.')
    }
    const released = releasedVersionOf(p)
    const check = checkNewVersion(version, released?.version ?? null, p.releaseVersions.map((v) => v.version))
    if (!check.ok) {
      return problem(409, 'VERSION_NOT_GREATER', check.message)
    }
    const tag = `${released?.tagPrefix ?? 'v'}${version}`
    if (p.repoTags?.includes(tag)) {
      return problem(409, 'TAG_EXISTS', `The tag ${tag} already exists.`)
    }

    const user = authDb.users.find((u) => u.id === a.userId)
    const started = makeReleaseVersion({
      version,
      // FR-129: no preset is ever pinned on a new version (a migrated one keeps its retired preset as history).
      status: 'in_development',
      baseTag: released?.tag ?? null,
      baseCommitSha: released?.mergeCommitSha ?? null,
      request: requestText,
      startedBy: { id: a.userId, name: user?.name ?? 'Unknown', isSystem: false },
      createdAt: new Date().toISOString(),
    })
    p.releaseVersions.push(started)
    p.currentPhase = 'refinement'
    p.status = 'active'
    p.checkpoint = 1
    p.idea = requestText
    p.phaseAtVersion = { 1: 'refinement' }
    p.updatedAt = new Date().toISOString()
    addEvent(p, {
      type: 'VERSION_STARTED',
      category: 'release',
      summary: `Version ${version} started`,
      payload: { releaseVersionId: started.id, version, baseTag: started.baseTag, request: requestText },
      actor: started.startedBy ?? undefined,
    })
    return HttpResponse.json<Schemas['StartReleaseVersionResultDto']>(
      { releaseVersion: started, state: stateDto(p) },
      { status: 201 },
    )
  }),
]
