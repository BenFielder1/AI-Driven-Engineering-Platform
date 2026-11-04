import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { writerMutation } from './artifacts'
import { antiforgeryFailure, currentUser, nextId, unauthenticated } from './auth'
import { platformDb } from './platform-connections'
import { problem } from './problem'
import { access, currentVersionId, record } from './projects'

/**
 * Issue trackers (FR-165–FR-173): readiness, browsing and a project's imports. These handlers follow the contract
 * (backend/openapi/api.json) and its error cases.
 */

type TrackerStatus = Schemas['TrackerStatusDto']
type TrackerProvider = Schemas['TrackerProvider']
type TrackerItem = Schemas['TrackerItemSummaryDto']
type SourceImport = Schemas['SourceImportDto']
type ProjectSources = Schemas['ProjectSourcesDto']

const names: Record<TrackerProvider, string> = { jira: 'Jira', linear: 'Linear', github: 'GitHub Issues', gitlab: 'GitLab Issues' }

export function trackerItem(key: string, overrides: Partial<TrackerItem> = {}): TrackerItem {
  return {
    key,
    title: `Item ${key}`,
    url: `https://acme.atlassian.net/browse/${key}`,
    type: 'story',
    status: 'To Do',
    statusCategory: 'todo',
    labels: [],
    parentKey: null,
    updatedAt: '2026-10-01T09:00:00Z',
    ...overrides,
  }
}

interface StoredSources {
  imports: SourceImport[]
  currentSnapshot: Schemas['SourceSnapshotDto'] | null
  draft: Schemas['SourceDraftStateDto'] | null
  drift: Map<string, Schemas['SourceItemDrift']>
}

const emptyDb = () => ({
  /** GitHub and GitLab readiness (they reuse the Git connections); Jira and Linear come from the platform connections. */
  gitStatus: {} as Partial<Record<TrackerProvider, Partial<TrackerStatus>>>,
  containers: {
    jira: [{ id: 'PROJ', name: 'Project One', detail: 'PROJ', url: 'https://acme.atlassian.net/browse/PROJ' }],
    linear: [{ id: 'team:ENG', name: 'Engineering', detail: 'Team', url: null }],
    github: [{ id: 'acme/app', name: 'acme/app', detail: null, url: 'https://github.com/acme/app' }],
    gitlab: [{ id: 'acme/app', name: 'acme/app', detail: null, url: 'https://gitlab.com/acme/app' }],
  } as Record<TrackerProvider, Schemas['TrackerContainerDto'][]>,
  /** What a search can return, per tracker. */
  items: [
    trackerItem('PROJ-1', { title: 'Login' }),
    trackerItem('PROJ-2', { title: 'Sign up' }),
    trackerItem('PROJ-3', { title: 'Old work', status: 'Done', statusCategory: 'done' }),
  ],
  pageSize: 50,
  /** Search and containers answer 409 TRACKER_PERMISSION_MISSING with this link. */
  permissionMissing: null as string | null,
  /** Containers answer with this problem. */
  containersDown: false,
  sources: new Map<string, StoredSources>(),
})

export const trackersDb = emptyDb()

export function resetTrackersDb() {
  Object.assign(trackersDb, emptyDb())
}

export function seedTrackerStatus(provider: 'github' | 'gitlab', status: Partial<TrackerStatus>) {
  trackersDb.gitStatus[provider] = status
}

function sourcesOf(projectId: string): StoredSources {
  let s = trackersDb.sources.get(projectId)
  if (!s) {
    s = { imports: [], currentSnapshot: null, draft: null, drift: new Map() }
    trackersDb.sources.set(projectId, s)
  }
  return s
}

export function seedSourceImport(projectId: string, overrides: Partial<SourceImport> = {}): SourceImport {
  const items = overrides.items ?? [trackerItem('PROJ-1', { title: 'Login' })]
  const row: SourceImport = {
    id: nextId(),
    releaseVersionId: 'version',
    provider: 'jira',
    connectionId: null,
    status: 'completed',
    errorCode: null,
    errorMessage: null,
    selection: { keys: items.map((i) => i.key), containerId: 'PROJ' },
    options: { includeChildren: true, includeComments: false, includeAssignees: false },
    itemCount: items.length,
    renderedBytes: 1000,
    items,
    sourceArtifactId: null,
    createdBy: { id: 'someone', name: 'Ada Lovelace', isSystem: false },
    createdAt: '2026-10-01T09:00:00Z',
    completedAt: '2026-10-01T09:01:00Z',
    ...overrides,
  }
  sourcesOf(projectId).imports.unshift(row)
  return row
}

export function seedSourceState(projectId: string, state: Partial<Pick<StoredSources, 'currentSnapshot' | 'draft'>>) {
  Object.assign(sourcesOf(projectId), state)
}

export function seedDrift(projectId: string, importId: string, key: string, drift: Schemas['SourceItemDrift']) {
  sourcesOf(projectId).drift.set(`${importId}:${key}`, drift)
}

function statusOf(provider: TrackerProvider, userId: string): TrackerStatus {
  if (provider === 'jira' || provider === 'linear') {
    const own = platformDb.connections.filter((c) => c.userId === userId && c.provider === provider)
    const ready = own.some((c) => c.status === 'valid' || c.status === 'unchecked')
    return {
      provider,
      name: names[provider],
      ready,
      reason: ready ? null : 'not_connected',
      settingsUrl: null,
      connections: own.map((c) => ({ id: c.id, label: c.label, status: c.status, accountName: c.accountName, detail: null })),
    }
  }
  return { provider, name: names[provider], ready: false, reason: 'not_connected', settingsUrl: null, connections: [], ...trackersDb.gitStatus[provider] }
}

const permissionProblem = (link: string) =>
  problem(409, 'TRACKER_PERMISSION_MISSING', "Issues permission missing: update the App's permissions in GitHub, then accept them on each installation.", {
    installUrl: link,
  })

function page<T>(all: readonly T[], cursor: string | null | undefined) {
  const start = cursor ? Number(cursor) : 0
  const end = start + trackersDb.pageSize
  return { items: all.slice(start, end), nextCursor: end < all.length ? String(end) : null }
}

export const trackerHandlers = [
  http.get('*/api/me/trackers', () => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    return HttpResponse.json<TrackerStatus[]>((['jira', 'linear', 'github', 'gitlab'] as const).map((p) => statusOf(p, user.id)))
  }),

  http.get('*/api/me/trackers/:provider/containers', ({ params, request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const provider = params.provider as TrackerProvider
    if (trackersDb.permissionMissing) {
      return permissionProblem(trackersDb.permissionMissing)
    }
    if (trackersDb.containersDown) {
      return problem(502, 'UPSTREAM_ERROR', `${names[provider]} returned an error or couldn't be reached. Try again later.`)
    }
    const url = new URL(request.url)
    const search = url.searchParams.get('search')?.toLowerCase() ?? ''
    return HttpResponse.json<Schemas['TrackerContainerPageDto']>(
      page(trackersDb.containers[provider].filter((c) => c.name.toLowerCase().includes(search)), url.searchParams.get('cursor')),
    )
  }),

  http.post('*/api/me/trackers/:provider/search', async ({ params, request }) => {
    if (!currentUser()) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const provider = params.provider as TrackerProvider
    const body = (await record(request)) as Schemas['TrackerSearchRequest']
    if (trackersDb.permissionMissing) {
      return permissionProblem(trackersDb.permissionMissing)
    }
    if (body.query?.includes('invalid')) {
      return problem(400, 'TRACKER_QUERY_INVALID', `${names[provider]} couldn't run that query: Field 'nope' does not exist.`)
    }
    const statuses = body.filter?.statusCategories
    const text = body.filter?.text?.toLowerCase()
    const matching = trackersDb.items.filter(
      (i) => (statuses ? statuses.includes(i.statusCategory) : i.statusCategory !== 'done') && (!text || i.title.toLowerCase().includes(text)),
    )
    return HttpResponse.json<Schemas['TrackerSearchResultDto']>(page(matching, body.cursor))
  }),

  http.get('*/api/projects/:id/sources', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const s = sourcesOf(a.project.id)
    return HttpResponse.json<ProjectSources>({
      releaseVersionId: currentVersionId(a.project),
      imports: s.imports,
      currentSnapshot: s.currentSnapshot,
      draft: s.draft,
      canChange: a.project.currentPhase === 'refinement',
    })
  }),

  http.post('*/api/projects/:id/sources/check', async ({ params, request }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const s = sourcesOf(a.project.id)
    return HttpResponse.json<Schemas['SourcesCheckDto']>({
      items: s.imports
        .filter((i) => i.status === 'completed')
        .flatMap((i) =>
          i.items.map((item) => ({
            importId: i.id,
            provider: i.provider,
            key: item.key,
            title: item.title,
            url: item.url,
            drift: s.drift.get(`${i.id}:${item.key}`) ?? 'unchanged',
          })),
        ),
      snapshotContentHash: s.currentSnapshot?.contentHash ?? null,
      checkedAt: '2026-10-02T09:00:00Z',
    })
  }),

  http.post('*/api/projects/:id/sources', async ({ params, request }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['SourceImportRequest']
    if (a.project.currentPhase !== 'refinement') {
      return problem(409, 'WRONG_PHASE', "Tracker items can't be changed in this phase.")
    }
    const created = seedSourceImport(a.project.id, {
      provider: body.provider,
      connectionId: body.connectionId ?? null,
      selection: body.selection,
      status: 'queued',
      items: [],
      itemCount: 0,
      completedAt: null,
    })
    return HttpResponse.json<SourceImport>(created, { status: 202 })
  }),

  http.post('*/api/projects/:id/sources/:importId/reimport', async ({ params, request }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const previous = sourcesOf(a.project.id).imports.find((i) => i.id === params.importId)
    if (!previous) {
      return problem(404, 'NOT_FOUND', 'The import was not found.')
    }
    const created = seedSourceImport(a.project.id, { ...previous, id: nextId(), status: 'queued', errorCode: null, errorMessage: null, items: [], itemCount: 0, completedAt: null })
    return HttpResponse.json<SourceImport>(created, { status: 202 })
  }),

  http.delete('*/api/projects/:id/sources/:importId', async ({ params, request }) => {
    const a = writerMutation(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const row = sourcesOf(a.project.id).imports.find((i) => i.id === params.importId)
    if (!row) {
      return problem(404, 'NOT_FOUND', 'The import was not found.')
    }
    row.status = 'removed'
    return new HttpResponse(null, { status: 204 })
  }),
]
