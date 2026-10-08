import { QueryClientProvider } from '@tanstack/react-query'
import { renderHook, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it } from 'vitest'

import { seedGate } from '../../mocks/handlers/lifecycle'
import { FakeLiveHub, projectEvent } from '../../test/fake-live'
import { openOverviewAs } from '../../test/overview'
import { createTestQueryClient } from '../../test/render'
import { artifactKeys } from '../artifacts/artifacts-api'
import { projectKeys } from '../projects/projects-api'
import { LiveConnectionContext } from './live-connection'
import { useProjectLive } from './use-project-live'

const PROJECT = '00000000-0000-7000-8000-000000000001'
const OTHER = '00000000-0000-7000-8000-000000000002'

function setup(hub = new FakeLiveHub()) {
  const queryClient = createTestQueryClient()
  // Seed the caches a project page would hold.
  queryClient.setQueryData(projectKeys.detail(PROJECT), { id: PROJECT })
  queryClient.setQueryData(projectKeys.state(PROJECT), { projectId: PROJECT })
  queryClient.setQueryData(projectKeys.gates(PROJECT), [])
  queryClient.setQueryData(projectKeys.activity(PROJECT, null), { pages: [] })
  queryClient.setQueryData(artifactKeys.list(PROJECT), [])
  queryClient.setQueryData(projectKeys.gates(OTHER), [])
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <LiveConnectionContext value={hub.factory}>{children}</LiveConnectionContext>
    </QueryClientProvider>
  )
  const utils = renderHook(() => useProjectLive(PROJECT), { wrapper })
  const stale = (key: readonly unknown[]) => queryClient.getQueryState(key)?.isInvalidated ?? false
  return { ...utils, hub, queryClient, stale }
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

  it('FR-46: a gate event invalidates the project, state, gates and activity, but not artifacts', async () => {
    const { hub, result, stale } = setup()
    await waitFor(() => {
      expect(result.current).toBe('live')
    })

    hub.latest.emit(projectEvent(PROJECT, 'gate', { type: 'GATE_APPROVED' }))

    expect(stale(projectKeys.detail(PROJECT))).toBe(true)
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(true)
    expect(stale(projectKeys.activity(PROJECT, null))).toBe(true)
    expect(stale(artifactKeys.list(PROJECT))).toBe(false)
  })

  it('FR-46: an artifact event invalidates the artifact queries; other projects are ignored', async () => {
    const { hub, result, stale } = setup()
    await waitFor(() => {
      expect(result.current).toBe('live')
    })

    hub.latest.emit(projectEvent(OTHER, 'gate'))
    expect(stale(projectKeys.gates(OTHER))).toBe(false)
    expect(stale(projectKeys.gates(PROJECT))).toBe(false)

    hub.latest.emit(projectEvent(PROJECT, 'artifact', { type: 'ARTIFACT_CREATED' }))
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)
    expect(stale(projectKeys.state(PROJECT))).toBe(true)
    expect(stale(projectKeys.gates(PROJECT))).toBe(false)
  })

  it('FR-46: after a reconnect it rejoins and refreshes everything that may have been missed', async () => {
    const { hub, result, stale } = setup()
    await waitFor(() => {
      expect(result.current).toBe('live')
    })

    hub.latest.dropAndReconnect()

    await waitFor(() => {
      expect(hub.latest.joined(PROJECT)).toBe(2)
    })
    expect(stale(artifactKeys.list(PROJECT))).toBe(true)
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
