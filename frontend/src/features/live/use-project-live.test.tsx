import { type QueryClient, QueryClientProvider, QueryObserver } from '@tanstack/react-query'
import { act, renderHook, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { seedGate } from '../../mocks/handlers/lifecycle'
import { FakeLiveConnection, FakeLiveHub, projectEvent } from '../../test/fake-live'
import { openOverviewAs } from '../../test/overview'
import { createTestQueryClient } from '../../test/render'
import { artifactKeys } from '../artifacts/artifacts-api'
import { notificationKeys } from '../notifications/notifications-api'
import { projectKeys } from '../projects/projects-api'
import { runKeys } from '../runs/runs-api'
import { LiveConnectionContext } from './live-connection'
import { NOTIFICATION_REFRESH_DEBOUNCE_MS, useProjectLive } from './use-project-live'

/** A connection whose first `Join` stays pending until the test releases it (the window between page open and join). */
class HeldJoinConnection extends FakeLiveConnection {
  private release: (() => void) | undefined

  override invoke(method: 'Join' | 'Leave', projectId: string) {
    const first = method === 'Join' && this.joined(projectId) === 0
    const call = super.invoke(method, projectId)
    return first ? new Promise<void>((resolve) => (this.release = resolve)).then(() => call) : call
  }

  releaseJoin() {
    this.release?.()
  }
}

class HeldJoinHub {
  readonly connections: HeldJoinConnection[] = []
  readonly factory = () => {
    const connection = new HeldJoinConnection()
    this.connections.push(connection)
    return connection
  }

  get latest(): HeldJoinConnection {
    const connection = this.connections.at(-1)
    if (!connection) {
      throw new Error('No live connection was opened')
    }
    return connection
  }
}

const PROJECT = '00000000-0000-7000-8000-000000000001'
const OTHER = '00000000-0000-7000-8000-000000000002'

function setup(hub = new FakeLiveHub()) {
  const queryClient = createTestQueryClient()
  // Seed the caches a project page would hold (again after the join, which refreshes them once, to start fresh). They
  // were fetched before the page opened (e.g. on an earlier visit), so the first join refetches them.
  const seed = () => {
    const updatedAt = Date.now() - 60_000
    queryClient.setQueryData(projectKeys.detail(PROJECT), { id: PROJECT }, { updatedAt })
    queryClient.setQueryData(projectKeys.state(PROJECT), { projectId: PROJECT }, { updatedAt })
    queryClient.setQueryData(projectKeys.gates(PROJECT), [], { updatedAt })
    queryClient.setQueryData(projectKeys.activity(PROJECT, null), { pages: [] }, { updatedAt })
    queryClient.setQueryData(artifactKeys.list(PROJECT), [], { updatedAt })
    queryClient.setQueryData(projectKeys.gates(OTHER), [], { updatedAt })
  }
  seed()
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <LiveConnectionContext value={hub.factory}>{children}</LiveConnectionContext>
    </QueryClientProvider>
  )
  const utils = renderHook(() => useProjectLive(PROJECT), { wrapper })
  const stale = (key: readonly unknown[]) => queryClient.getQueryState(key)?.isInvalidated ?? false
  /** Waits until joined, then re-seeds the caches so only what an event invalidates afterwards is stale. */
  const joined = async () => {
    await waitFor(() => {
      expect(utils.result.current).toBe('live')
    })
    seed()
  }
  return { ...utils, hub, queryClient, stale, joined }
}

interface InvalidateSpy {
  mock: { calls: Parameters<QueryClient['invalidateQueries']>[] }
}

/** The calls that refreshed the notification tree (FR-47). */
function notificationRefreshes(invalidate: InvalidateSpy) {
  return invalidate.mock.calls.filter(([filters]) => filters?.queryKey === notificationKeys.all)
}

describe('useProjectLive (FR-46)', () => {
  it('FR-46: joins the project on connect and reports live', async () => {
    const { hub, result } = setup()
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    expect(hub.latest.started).toBe(true)
    expect(hub.latest.calls).toEqual([{ method: 'Join', projectId: PROJECT }])
  })

  it('FR-46: a gate event invalidates the project, state, gates, activity and the artifacts (the draft it gates)', async () => {
    const { hub, stale, joined } = setup()
    await joined()

    hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_APPROVED' }))

    expect(stale(projectKeys.detail(PROJECT))).toBe(true)
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(true)
    expect(stale(projectKeys.activity(PROJECT, null))).toBe(true)
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(OTHER))).toBe(false)
  })

  it('FR-46: an artifact event invalidates the artifact queries; other projects are ignored', async () => {
    const { hub, stale, joined } = setup()
    await joined()

    hub.latest.emit(projectEvent(OTHER, 'gate'))
    expect(stale(projectKeys.gates(OTHER))).toBe(false)
    expect(stale(projectKeys.gates(PROJECT))).toBe(false)

    hub.latest.emit(projectEvent(PROJECT, 'artifact', { type: 'ARTIFACT_CREATED' }))
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(false)
  })

  it('FR23_ live: a run event invalidates the runs list and detail, the state (tokens used), the activity and the artifacts', async () => {
    const { hub, stale, queryClient, joined } = setup()
    await joined()
    queryClient.setQueryData(runKeys.list(PROJECT, null, 1), { items: [] })
    queryClient.setQueryData(runKeys.detail(PROJECT, 'run-1'), { id: 'run-1' })

    hub.latest.emit(projectEvent(PROJECT, 'run', { type: 'RUN_COMPLETED' }))

    expect(stale(runKeys.list(PROJECT, null, 1))).toBe(true)
    expect(stale(runKeys.detail(PROJECT, 'run-1'))).toBe(true)
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.activity(PROJECT, null))).toBe(true)
    // A completed run's draft must show even if its artifact event was missed.
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(false)
  })

  it('FR-46: an event sent before the join completed is covered: the project is refetched once after joining', async () => {
    // The artifact event happened while the page was opening (before Join): this client never receives it.
    const { stale, hub } = setup()
    expect(stale(artifactKeys.list(PROJECT))).toBe(false)
    await waitFor(() => {
      expect(hub.latest.joined(PROJECT)).toBe(1)
    })
    await waitFor(() => {
      expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    })
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(true)
    // Other projects are left alone.
    expect(stale(projectKeys.gates(OTHER))).toBe(false)
  })

  it('FR-46: data fetched after the page opened but before Join resolved is still refetched (no missed-event window)', async () => {
    const queryClient = createTestQueryClient()
    const hub = new HeldJoinHub()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        <LiveConnectionContext value={hub.factory}>{children}</LiveConnectionContext>
      </QueryClientProvider>
    )
    const { result } = renderHook(() => useProjectLive(PROJECT), { wrapper })
    await waitFor(() => {
      expect(hub.latest.joined(PROJECT)).toBe(1)
    })
    // The page's first load settles while Join is still pending; an event (a draft, a gate) lands in the same window
    // and is never delivered to this client.
    queryClient.setQueryData(projectKeys.state(PROJECT), { projectId: PROJECT }, { updatedAt: Date.now() + 1 })
    queryClient.setQueryData(artifactKeys.list(PROJECT), [], { updatedAt: Date.now() + 1 })
    queryClient.setQueryData(projectKeys.gates(PROJECT), [], { updatedAt: Date.now() + 1 })
    const stale = (key: readonly unknown[]) => queryClient.getQueryState(key)?.isInvalidated ?? false
    expect(stale(projectKeys.state(PROJECT))).toBe(false)

    act(() => {
      hub.latest.releaseJoin()
    })
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    await waitFor(() => {
      expect(stale(projectKeys.state(PROJECT))).toBe(true)
    })
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(true)
  })

  it('FR-46: a first-load fetch still in flight when Join resolves is refetched once it settles, so it cannot end on pre-join data', async () => {
    const queryClient = createTestQueryClient()
    const hub = new HeldJoinHub()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        <LiveConnectionContext value={hub.factory}>{children}</LiveConnectionContext>
      </QueryClientProvider>
    )
    const { result } = renderHook(() => useProjectLive(PROJECT), { wrapper })
    await waitFor(() => {
      expect(hub.latest.joined(PROJECT)).toBe(1)
    })
    // A first-load gates fetch whose server read happened before a GATE_CREATED sent ahead of the join.
    const responses: ((value: string[]) => void)[] = []
    const queryFn = vi.fn(() => new Promise<string[]>((resolve) => responses.push(resolve)))
    // Observed, as the page's own useQuery is (invalidation refetches active queries).
    const observer = new QueryObserver(queryClient, { queryKey: projectKeys.gates(PROJECT), queryFn })
    const unsubscribe = observer.subscribe(() => undefined)
    expect(queryFn).toHaveBeenCalledTimes(1)

    act(() => {
      hub.latest.releaseJoin()
    })
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    // The pre-join response settles first; it is not the last word.
    act(() => {
      responses[0]?.([])
    })
    await waitFor(() => {
      expect(queryFn).toHaveBeenCalledTimes(2)
    })
    act(() => {
      responses[1]?.(['gate-created-before-join'])
    })
    await waitFor(() => {
      expect(queryClient.getQueryData(projectKeys.gates(PROJECT))).toEqual(['gate-created-before-join'])
    })
    unsubscribe()
  })

  it('FR-46: a rejoin after a reconnect refetches everything, however fresh', async () => {
    const { hub, stale, queryClient, joined } = setup()
    await joined()
    queryClient.setQueryData(projectKeys.state(PROJECT), { projectId: PROJECT }, { updatedAt: Date.now() + 60_000 })
    queryClient.setQueryData(artifactKeys.list(PROJECT), [], { updatedAt: Date.now() + 60_000 })

    hub.latest.dropAndReconnect()
    await waitFor(() => {
      expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    })
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
  })

  it('FR-46: when the rejoin after a reconnect fails, the project is still refetched once and reports offline', async () => {
    const { hub, result, stale, joined } = setup()
    await joined()

    hub.latest.failJoin = true
    hub.latest.dropAndReconnect()

    await waitFor(() => {
      expect(result.current).toBe('offline')
    })
    expect(hub.latest.joined(PROJECT)).toBe(2)
    await waitFor(() => {
      expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    })
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(true)
  })

  it('FR-46: only run/gate events that can change an artifact refresh the artifacts; gate events reuse an in-flight fetch', async () => {
    const { hub, stale, queryClient, joined } = setup()
    await joined()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const artifactCalls = () =>
      invalidate.mock.calls.filter(([filters]) => JSON.stringify(filters?.queryKey) === JSON.stringify(projectKeys.artifacts(PROJECT)))

    for (const type of ['RUN_STARTED', 'RUN_FAILED', 'BUDGET_EXCEEDED', 'CONNECTION_INVALID']) {
      hub.latest.emit(projectEvent(PROJECT, 'run', { type }))
    }
    hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_OVERDUE' }))
    hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_CANCELLED' }))
    expect(artifactCalls()).toHaveLength(0)
    expect(stale(artifactKeys.list(PROJECT))).toBe(false)
    expect(stale(projectKeys.state(PROJECT))).toBe(true)

    // RUN_COMPLETED cancels and restarts an in-flight artifact fetch (one started before the draft was written would
    // settle on pre-draft data if the ARTIFACT_* event was missed); the gate that follows it reuses that fresh fetch.
    hub.latest.emit(projectEvent(PROJECT, 'run', { type: 'RUN_COMPLETED' }))
    hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_CREATED' }))
    expect(artifactCalls().map(([, options]) => options)).toEqual([undefined, { cancelRefetch: false }])
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)

    // The artifact's own event is authoritative: it cancels and refetches as usual.
    hub.latest.emit(projectEvent(PROJECT, 'artifact', { type: 'ARTIFACT_CREATED' }))
    expect(artifactCalls().at(-1)?.[1]).toBeUndefined()
  })

  it('FR-46: after a reconnect it rejoins and refreshes everything that may have been missed', async () => {
    const { hub, result, stale, joined } = setup()
    await joined()

    hub.latest.dropAndReconnect()

    await waitFor(() => {
      expect(hub.latest.joined(PROJECT)).toBe(2)
    })
    await waitFor(() => {
      expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    })
    expect(stale(projectKeys.gates(PROJECT))).toBe(true)
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
  })

  it('FR-46: leaves the project and stops the connection on unmount', async () => {
    const { hub, result, unmount } = setup()
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    unmount()
    await waitFor(() => {
      expect(hub.latest.stopped).toBe(true)
    })
    expect(hub.latest.calls.at(-1)).toEqual({ method: 'Leave', projectId: PROJECT })
  })

  it('FR47_live_event_refreshes_the_unread_count_and_notification_lists', async () => {
    const { hub, result, stale, queryClient } = setup()
    queryClient.setQueryData(notificationKeys.unreadCount(), 0)
    queryClient.setQueryData(notificationKeys.latest(), { items: [] })
    queryClient.setQueryData(notificationKeys.list(false), { pages: [] })
    queryClient.setQueryData(notificationKeys.preferences(), { mutedTypes: [] })
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      hub.latest.emit(projectEvent(OTHER, 'gate', { type: 'GATE_CREATED' }))
      // A burst (a draft emits two events, execution emits many) refreshes once, after the burst settles.
      hub.latest.emit(projectEvent(PROJECT, 'artifact', { type: 'ARTIFACT_CREATED' }))
      act(() => {
        vi.advanceTimersByTime(NOTIFICATION_REFRESH_DEBOUNCE_MS - 100)
      })
      hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_CREATED' }))
      hub.latest.emit(projectEvent(PROJECT, 'step', { type: 'STEP_BLOCKED' }))
      act(() => {
        vi.advanceTimersByTime(NOTIFICATION_REFRESH_DEBOUNCE_MS - 1)
      })
      expect(notificationRefreshes(invalidate)).toHaveLength(0)
      expect(stale(notificationKeys.unreadCount())).toBe(false)

      act(() => {
        vi.advanceTimersByTime(1)
      })
      expect(notificationRefreshes(invalidate)).toHaveLength(1)
      // An in-flight unread-count/latest fetch is reused, not aborted and restarted.
      expect(notificationRefreshes(invalidate)[0]?.[1]).toEqual({ cancelRefetch: false })
      expect(stale(notificationKeys.unreadCount())).toBe(true)
      expect(stale(notificationKeys.latest())).toBe(true)
      expect(stale(notificationKeys.list(false))).toBe(true)
      expect(stale(notificationKeys.preferences())).toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })

  it('FR47_live_event_that_cannot_notify_does_not_refresh_notifications', async () => {
    const { hub, result, stale, queryClient } = setup()
    queryClient.setQueryData(notificationKeys.unreadCount(), 0)
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_APPROVED' }))
      hub.latest.emit(projectEvent(PROJECT, 'run', { type: 'RUN_STARTED' }))
      hub.latest.emit(projectEvent(PROJECT, 'step', { type: 'STEP_COMPLETED' }))
      act(() => {
        vi.advanceTimersByTime(NOTIFICATION_REFRESH_DEBOUNCE_MS * 2)
      })
      expect(notificationRefreshes(invalidate)).toHaveLength(0)
      expect(stale(notificationKeys.unreadCount())).toBe(false)
      // The project's own queries still refresh.
      expect(stale(projectKeys.gates(PROJECT))).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  it('FR47_pending_notification_refresh_is_cancelled_on_unmount', async () => {
    const { hub, result, queryClient, unmount } = setup()
    await waitFor(() => {
      expect(result.current).toBe('live')
    })
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_CREATED' }))
      unmount()
      act(() => {
        vi.advanceTimersByTime(NOTIFICATION_REFRESH_DEBOUNCE_MS * 2)
      })
      expect(notificationRefreshes(invalidate)).toHaveLength(0)
    } finally {
      vi.useRealTimers()
    }
  })

  it('FR-46: a failed join (e.g. FORBIDDEN) reports offline instead of failing silently', async () => {
    const hub = new FakeLiveHub()
    hub.failJoins = true
    const { result } = setup(hub)
    await waitFor(() => {
      expect(result.current).toBe('offline')
    })
  })
})

describe('Project pages update live (FR-46)', () => {
  it('FR-46: a gate created elsewhere appears on the overview without reloading', async () => {
    const { live, project } = await openOverviewAs('owner', { currentPhase: 'specification' })
    expect(await screen.findByText('No gates are waiting for a decision')).toBeInTheDocument()
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })

    seedGate(project, { type: 'spec_approval' })
    live.latest.emit(projectEvent(project.id, 'gate', { type: 'GATE_CREATED' }))

    const pending = await screen.findByRole('list', { name: 'Pending gates' })
    expect(within(pending).getByRole('heading', { name: 'Specification approval' })).toBeInTheDocument()
  })

  it('FR-46: says so when live updates stop, so the page is never silently stale', async () => {
    const { live } = await openOverviewAs('viewer')
    expect(screen.queryByText(/Live updates/)).not.toBeInTheDocument()

    live.latest.close()

    expect(await screen.findByText('Live updates are off. Reload the page to see other people’s changes.')).toBeInTheDocument()
  })
})
