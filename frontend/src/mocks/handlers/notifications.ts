import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, authDb, currentUser, nextId, unauthenticated } from './auth'
import { problem } from './problem'
import { projectsDb, record, roleOf } from './projects'

type Notification = Schemas['NotificationDto']
type NotificationType = Schemas['NotificationType']
type Gate = Schemas['GateDto']

const allTypes: readonly NotificationType[] = [
  'gate_required',
  'gate_overdue',
  'gate_reminder',
  'artifact_ready',
  'step_blocked',
  'run_failed',
  'budget_exceeded',
  'review_completed',
  'release_completed',
  'release_failed',
  'rollback',
]

interface StoredNotification extends Notification {
  userId: string
}

export interface StoredApprovalLink {
  token: string
  gateId: string
  projectId: string
  decision: Schemas['ApprovalLinkDecision']
  expiresAt: string
  usedAt: string | null
}

/** In-memory notifications backend for tests (reset after each test by src/test/setup.ts). */
export const notificationsDb = {
  items: [] as StoredNotification[],
  muted: new Map<string, NotificationType[]>(),
  links: [] as StoredApprovalLink[],
}

export function resetNotificationsDb() {
  notificationsDb.items = []
  notificationsDb.muted = new Map()
  notificationsDb.links = []
}

let clock = Date.parse('2026-09-01T12:00:00Z')

/** Seeds a notification for the signed-in user (newer than every earlier seed unless `createdAt` is given). */
export function seedNotification(overrides: Partial<StoredNotification> = {}): StoredNotification {
  clock += 60_000
  const n: StoredNotification = {
    id: nextId(),
    userId: authDb.currentUserId ?? '',
    projectId: null,
    projectName: null,
    type: 'gate_required',
    title: 'Specification approval needs a decision',
    body: 'Inventory tracker is waiting for an Owner.',
    link: '/projects',
    readAt: null,
    createdAt: new Date(clock).toISOString(),
    ...overrides,
  }
  notificationsDb.items.push(n)
  return n
}

/** Seeds `count` notifications numbered from 1 (oldest) to `count` (newest). */
export function seedNotifications(count: number, overrides: Partial<StoredNotification> = {}) {
  return Array.from({ length: count }, (_, i) => seedNotification({ title: `Notification ${String(i + 1)}`, ...overrides }))
}

/** Seeds a one-click approval link for a gate of a project (valid for 72 hours unless overridden). */
export function seedApprovalLink(overrides: Partial<StoredApprovalLink> & Pick<StoredApprovalLink, 'gateId' | 'projectId'>) {
  const link: StoredApprovalLink = {
    token: `tok_${nextId().slice(-6)}`,
    decision: 'approve',
    expiresAt: new Date(Date.now() + 72 * 3600_000).toISOString(),
    usedAt: null,
    ...overrides,
  }
  notificationsDb.links.push(link)
  return link
}

const mine = (userId: string) =>
  notificationsDb.items
    .filter((n) => n.userId === userId)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt) || b.id.localeCompare(a.id))

const unreadCount = (userId: string) => mine(userId).filter((n) => n.readAt === null).length

const toDto = (n: StoredNotification): Notification => ({
  id: n.id,
  projectId: n.projectId,
  projectName: n.projectName,
  type: n.type,
  title: n.title,
  body: n.body,
  link: n.link,
  readAt: n.readAt,
  createdAt: n.createdAt,
})

/** FR-48: shared checks of GET and POST /api/approvals/:token (owner of the gate's project; used; expired). */
function linkAccess(token: unknown): { link: StoredApprovalLink; gate: Gate; projectName: string; userId: string } | Response {
  const user = currentUser()
  if (!user) {
    return unauthenticated()
  }
  const link = notificationsDb.links.find((l) => l.token === token)
  const project = link ? projectsDb.projects.find((p) => p.id === link.projectId) : undefined
  const gate = project?.gates.find((g) => g.id === link?.gateId)
  if (!link || !project || !gate) {
    return problem(404, 'NOT_FOUND', 'The approval link was not found.')
  }
  if (roleOf(project, user.id) !== 'owner' && user.systemRole !== 'admin') {
    return problem(403, 'FORBIDDEN', 'Only Owners of the project can use this link.')
  }
  if (link.usedAt) {
    return problem(409, 'LINK_USED', 'The approval link was already used.')
  }
  if (Date.parse(link.expiresAt) <= Date.now()) {
    return problem(410, 'LINK_EXPIRED', 'The approval link has expired.')
  }
  return { link, gate, projectName: project.name, userId: user.id }
}

export const notificationHandlers = [
  // FR-47: the caller's notifications, newest first; `before` is the previous page's `nextBefore` (an id here).
  http.get('*/api/me/notifications', ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const url = new URL(request.url)
    const limit = Number(url.searchParams.get('limit') ?? '50')
    if (!Number.isInteger(limit) || limit < 1 || limit > 100) {
      return problem(400, 'VALIDATION', 'Invalid paging.', { errors: { limit: ['Use a limit from 1 to 100.'] } })
    }
    let items = mine(user.id)
    if (url.searchParams.get('unreadOnly') === 'true') {
      items = items.filter((n) => n.readAt === null)
    }
    const before = url.searchParams.get('before')
    if (before) {
      const index = items.findIndex((n) => n.id === before)
      if (index < 0) {
        return problem(400, 'VALIDATION', 'Invalid cursor.', { errors: { before: ['Not a nextBefore value.'] } })
      }
      items = items.slice(index + 1)
    }
    const page = items.slice(0, limit)
    const last = page.at(-1)
    return HttpResponse.json<Schemas['NotificationPageDto']>({
      items: page.map(toDto),
      unreadCount: unreadCount(user.id),
      nextBefore: items.length > limit && last ? last.id : null,
    })
  }),

  http.get('*/api/me/notifications/unread-count', () => {
    const user = currentUser()
    return user
      ? HttpResponse.json<Schemas['UnreadNotificationCountDto']>({ unreadCount: unreadCount(user.id) })
      : unauthenticated()
  }),

  http.patch('*/api/me/notifications/:id/read', async ({ request, params }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    await record(request)
    const n = mine(user.id).find((x) => x.id === params.id)
    if (!n) {
      return problem(404, 'NOT_FOUND', 'The notification was not found.')
    }
    n.readAt ??= new Date().toISOString()
    return new HttpResponse(null, { status: 204 })
  }),

  http.post('*/api/me/notifications/read-all', async ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    await record(request)
    const now = new Date().toISOString()
    for (const n of mine(user.id)) {
      n.readAt ??= now
    }
    return new HttpResponse(null, { status: 204 })
  }),

  // §2: in-app preferences (muted types).
  http.get('*/api/me/notification-preferences', () => {
    const user = currentUser()
    return user
      ? HttpResponse.json<Schemas['NotificationPreferencesDto']>({ mutedTypes: notificationsDb.muted.get(user.id) ?? [] })
      : unauthenticated()
  }),

  http.put('*/api/me/notification-preferences', async ({ request }) => {
    const user = currentUser()
    if (!user) {
      return unauthenticated()
    }
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const body = (await record(request)) as Schemas['UpdateNotificationPreferencesRequest'] | null
    const muted = body?.mutedTypes ?? []
    if (muted.some((t) => !allTypes.includes(t))) {
      return problem(400, 'VALIDATION', 'Unknown type.', { errors: { mutedTypes: ['Unknown notification type.'] } })
    }
    notificationsDb.muted.set(user.id, [...new Set(muted)])
    return HttpResponse.json<Schemas['NotificationPreferencesDto']>({ mutedTypes: notificationsDb.muted.get(user.id) ?? [] })
  }),

  // FR-48: the confirmation view of a one-click approval link.
  http.get('*/api/approvals/:token', ({ params }) => {
    const a = linkAccess(params.token)
    if (a instanceof Response) {
      return a
    }
    const { link, gate, projectName } = a
    return HttpResponse.json<Schemas['ApprovalLinkViewDto']>({
      gateId: gate.id,
      projectId: link.projectId,
      projectName,
      gateType: gate.type,
      decision: link.decision,
      artifactSummary:
        gate.artifactType && gate.artifactVersion !== null
          ? { type: gate.artifactType, version: gate.artifactVersion, title: 'Inventory tracker specification' }
          : null,
      expiresAt: link.expiresAt,
      status: gate.status === 'pending' ? 'valid' : 'gate_closed',
    })
  }),

  // FR-48: confirm the link's decision (uses up the link).
  http.post('*/api/approvals/:token', async ({ request, params }) => {
    const rejected = antiforgeryFailure(request)
    if (rejected) {
      return rejected
    }
    const a = linkAccess(params.token)
    if (a instanceof Response) {
      return a
    }
    const body = (await record(request)) as Schemas['ConfirmApprovalLinkRequest'] | null
    const { link, gate, userId } = a
    if (gate.status !== 'pending') {
      return problem(409, 'GATE_NOT_PENDING', 'Only pending gates can be decided.')
    }
    const user = authDb.users.find((u) => u.id === userId)
    const now = new Date().toISOString()
    gate.status = link.decision === 'approve' ? 'approved' : 'rejected'
    gate.decidedAt = now
    gate.decidedBy = { id: userId, name: user?.name ?? 'Unknown', isSystem: false }
    gate.comment = body?.comment?.trim() ? body.comment : null
    gate.overdue = false
    for (const l of notificationsDb.links.filter((x) => x.gateId === gate.id)) {
      l.usedAt = now
    }
    return HttpResponse.json<Gate>(gate)
  }),
]
