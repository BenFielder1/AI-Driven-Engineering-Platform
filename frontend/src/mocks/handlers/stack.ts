import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { addEvent } from './lifecycle'
import { currentUser, unauthenticated } from './auth'
import { problem } from './problem'
import {
  access,
  currentVersionOf,
  developmentVersionOf,
  ownerMutation,
  record,
  repoVcsOfProject,
  type StoredProject,
} from './projects'
import { evaluate, packsOf, selectionOfRequest, STACK_CATALOG_HASH, STACK_CATALOG_VERSION, stackCatalog, validateComposition } from './stack-catalog'

/** Test knobs for the stack endpoints (reset after each test by src/test/setup.ts through resetStackDb). */
export const stackDb = {
  /** POST /api/stack/detect answers this (default: nothing detected). */
  detection: null as Schemas['StackDetectionDto'] | null,
  /** …or this error response. */
  detectError: null as (() => Response) | null,
  /** GET /api/admin/stack. */
  admin: null as Schemas['AdminStackCatalogDto'] | null,
}

export function resetStackDb() {
  stackDb.detection = null
  stackDb.detectError = null
  stackDb.admin = null
}

export const adminStackCatalog = (): Schemas['AdminStackCatalogDto'] => ({
  version: STACK_CATALOG_VERSION,
  contentHash: STACK_CATALOG_HASH,
  status: 'loaded',
  errors: [],
  packs: [
    {
      id: 'fe-react-vite',
      layer: 'frontend',
      name: 'React + Vite',
      version: '1.0.0+aide.1',
      source: { plugin: 'fe-react-vite', version: '1.0.0', snapshot: '2026-10-20' },
      contentHash: 'sha256:aaa111',
      status: 'loaded',
      errors: [],
    },
    {
      id: 'be-aspnet',
      layer: 'backend',
      name: 'ASP.NET Core',
      version: '1.0.0+aide.1',
      source: { plugin: 'be-aspnet', version: '1.0.0', snapshot: '2026-10-20' },
      contentHash: 'sha256:bbb222',
      status: 'loaded',
      errors: [],
    },
    {
      id: 'data-broken',
      layer: null,
      name: null,
      version: null,
      source: null,
      contentHash: 'sha256:ccc333',
      status: 'failed',
      errors: ['pack.json: missing "layer".'],
    },
  ],
})

/** Whether the version's implementation plan was locked (or a seed says so): the stack is pinned then (FR-117). */
function planLocked(p: StoredProject, releaseVersionId: string): boolean {
  return (
    p.stack?.planLocked === true ||
    p.artifacts.some((a) => a.type === 'implementation_plan' && a.status === 'locked' && a.releaseVersionId === releaseVersionId)
  )
}

export function projectStackDto(p: StoredProject): Schemas['ProjectStackDto'] {
  const version = currentVersionOf(p)
  const developing = developmentVersionOf(p)
  const locked = developing?.status !== 'in_development' ? 'not_in_development' : planLocked(p, developing.id) ? 'plan_locked' : null
  const composition = p.stack?.composition ?? null
  return {
    releaseVersionId: version?.id ?? '',
    version: version?.version ?? '',
    versionStatus: version?.status ?? 'in_development',
    composition,
    catalogVersion: composition ? (p.stack?.catalogVersion ?? STACK_CATALOG_VERSION) : null,
    catalogHash: composition ? (p.stack?.catalogHash === undefined ? STACK_CATALOG_HASH : p.stack.catalogHash) : null,
    currentCatalogVersion: STACK_CATALOG_VERSION,
    currentCatalogHash: STACK_CATALOG_HASH,
    locked: locked !== null,
    lockedReason: locked,
    stackChange: {
      baseHasCode: p.stack?.baseHasCode ?? false,
      layersChanged: p.stack?.layersChanged ?? [],
      isStackChange: (p.stack?.baseHasCode ?? false) && (p.stack?.layersChanged ?? []).length > 0,
    },
    legacyPreset: p.stack?.legacyPreset ?? null,
    repoVcs: repoVcsOfProject(p),
  }
}

/**
 * FR-113–FR-119 stack endpoints: the catalog, option states and validation (from the trimmed engine in
 * stack-catalog.ts), import detection (stackDb knobs), Settings → Stack (GET any member, PUT Owner) and the admin view.
 */
export const stackHandlers = [
  http.get('*/api/stack/catalog', () => (currentUser() ? HttpResponse.json(stackCatalog) : unauthenticated())),

  http.post('*/api/stack/options', async ({ request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const body = (await record(request)) as Schemas['StackCompositionRequest']
    return HttpResponse.json<Schemas['StackOptionsDto']>(evaluate(selectionOfRequest(body)))
  }),

  http.post('*/api/stack/validate', async ({ request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const body = (await record(request)) as Schemas['StackCompositionRequest']
    const result = validateComposition(body)
    if (result.ok) {
      return HttpResponse.json<Schemas['StackCompositionDto']>(result.composition)
    }
    return 'errors' in result
      ? problem(400, 'VALIDATION', 'The stack choices are invalid.', { errors: result.errors })
      : problem(400, 'STACK_INCOMPATIBLE', "These stack choices don't work together.", { violations: result.violations })
  }),

  http.post('*/api/stack/detect', async ({ request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    await record(request)
    if (stackDb.detectError) {
      return stackDb.detectError()
    }
    return HttpResponse.json<Schemas['StackDetectionDto']>(
      stackDb.detection ?? { ref: 'main', commit: 'c0ffee1', suggestions: [], conflicts: [], truncated: false },
    )
  }),

  http.get('*/api/projects/:id/stack', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    return HttpResponse.json(projectStackDto(a.project))
  }),

  http.put('*/api/projects/:id/stack', async ({ request, params }) => {
    const a = ownerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['StackCompositionRequest']
    const p = a.project
    const before = projectStackDto(p)
    if (before.locked) {
      return problem(409, 'STACK_LOCKED', "The stack can't change after the implementation plan is locked.")
    }
    const repoVcs = repoVcsOfProject(p)
    if (repoVcs && body.vcs && body.vcs.pack !== repoVcs) {
      return problem(400, 'VCS_MISMATCH', "The VCS layer isn't the repository's host.")
    }
    const result = validateComposition({ ...body, vcs: body.vcs ?? (repoVcs ? { pack: repoVcs } : null) })
    if (!result.ok) {
      return 'errors' in result
        ? problem(400, 'VALIDATION', 'The stack choices are invalid.', { errors: result.errors })
        : problem(400, 'STACK_INCOMPATIBLE', "These stack choices don't work together.", { violations: result.violations })
    }
    const from = p.stack?.composition ?? null
    // Re-pinned to the current catalog; a migrated version keeps its retired preset as history (FR-129).
    p.stack = { ...p.stack, composition: result.composition, catalogVersion: STACK_CATALOG_VERSION, catalogHash: STACK_CATALOG_HASH }
    p.stackPacks = packsOf(result.composition)
    addEvent(p, {
      type: 'STACK_CHANGED',
      category: 'project',
      summary: 'Stack changed',
      payload: { releaseVersionId: before.releaseVersionId, version: before.version, from, to: result.composition, layersChanged: [] },
    })
    return HttpResponse.json(projectStackDto(p))
  }),

  http.get('*/api/admin/stack', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    if (user.systemRole !== 'admin') {
      return problem(403, 'FORBIDDEN', 'Admins only.')
    }
    return HttpResponse.json(stackDb.admin ?? adminStackCatalog())
  }),
]

