import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedFixtureRun } from '../../mocks/handlers/pipeline-seeds'
import { seedProject } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'
import { createPipelineLiveBus } from './live-bus'

describe('FR-304: live pipeline messages', () => {
  it('FR304_live_bus: delivers a project\'s messages to its subscribers only, until they unsubscribe', () => {
    const bus = createPipelineLiveBus()
    const mine = vi.fn()
    const other = vi.fn()
    const unsubscribe = bus.subscribe('p1', mine)
    bus.subscribe('p2', other)
    bus.resync('p1')
    expect(mine).toHaveBeenCalledWith({ kind: 'resync' })
    expect(other).not.toHaveBeenCalled()
    unsubscribe()
    bus.resync('p1')
    expect(mine).toHaveBeenCalledTimes(1)
  })

  it('FR304_live_pipeline_tab: stageUpdated and pipelineRunUpdated messages update the open Pipeline tab', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const { live } = await renderRoute(`/projects/${project.id}/pipeline`)
    const stages = await screen.findByRole('region', { name: 'Stages' })
    await within(stages).findByRole('table')
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })

    const summarise = stored.stages[2]
    if (!summarise) {
      throw new Error('no stage')
    }
    const exitedAt = '2026-12-01T10:00:00.000Z'
    // The server's state changes first (a refetch racing the message then reads the same), then the message arrives.
    Object.assign(summarise, { state: 'completed', exitedAt })
    live.latest.emitPipeline({ type: 'stageUpdated', stage: { ...summarise } })
    await waitFor(() => {
      const row = within(stages).getByRole('rowheader', { name: 'Summarise' }).closest('tr')
      expect(row).toHaveAttribute('data-state', 'completed')
    })

    Object.assign(stored.run, { status: 'completed', packStatus: 'published' })
    live.latest.emitPipeline({ type: 'pipelineRunUpdated', run: { ...stored.run } })
    await waitFor(() => {
      expect(within(screen.getByTestId('run-summary')).getByText('Completed')).toBeInTheDocument()
    })
  })

  it('FR304_live_other_project: messages for another project are ignored', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const other = seedProject({ name: 'Other' })
    const stored = seedFixtureRun(project)
    const { live } = await renderRoute(`/projects/${project.id}/pipeline`)
    await within(await screen.findByTestId('run-summary')).findByText('Waiting on a gate')
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })
    live.latest.emitPipeline({ type: 'pipelineRunUpdated', run: { ...stored.run, projectId: other.id, status: 'failed' } })
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(within(screen.getByTestId('run-summary')).getByText('Waiting on a gate')).toBeInTheDocument()
  })
})
