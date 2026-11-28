import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedParallelRun, seedQaRun } from '../../mocks/handlers/engine-constructs'
import { seedProject } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'
import { isNewerMapProgress } from './pipeline-live'

async function openPipeline(projectId: string, runId: string) {
  const utils = await renderRoute(`/projects/${projectId}/pipeline?run=${runId}`)
  const stages = within(await screen.findByRole('region', { name: 'Stages' }))
  await waitFor(() => {
    expect(utils.live.latest.joined(projectId)).toBe(1)
  })
  return { ...utils, stages }
}

describe('FR-363: live branch and map updates', () => {
  it('FR363_live_map_progress: a mapProgress message updates the map progress bar', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const qa = seedQaRun(project, { total: 200, failed: 3, open: 47 })
    const { live, stages } = await openPipeline(project.id, qa.run.id)
    const bar = await stages.findByRole('progressbar', { name: 'Items of Score the transcripts' })
    expect(bar).toHaveAttribute('aria-valuetext', '153 of 200 done, 3 failed')
    const score = qa.stages.find((s) => s.id === 'score')
    const executionId = score?.map?.executionId
    if (!score || !executionId) {
      throw new Error('no map')
    }

    // The server's state changes first (a refetch racing the message then reads the same), then the message arrives.
    const progress = { executionId, total: 200, completed: 195, failed: 5 }
    score.map = progress
    live.latest.emitPipeline({ type: 'mapProgress', update: { projectId: project.id, runId: qa.run.id, stageId: 'score', progress } })
    await waitFor(() => {
      expect(stages.getByRole('progressbar', { name: 'Items of Score the transcripts' })).toHaveAttribute(
        'aria-valuetext',
        '200 of 200 done, 5 failed',
      )
    })
    expect(within(stages.getByTestId('map-progress')).getByText('Failed 5')).toBeInTheDocument()
  })

  it('FR363_live_map_progress_stale: a stale mapProgress arriving after the final one is ignored', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const qa = seedQaRun(project, { total: 200, failed: 3, open: 47 })
    const { live, stages } = await openPipeline(project.id, qa.run.id)
    await stages.findByRole('progressbar', { name: 'Items of Score the transcripts' })
    const score = qa.stages.find((s) => s.id === 'score')
    const executionId = score?.map?.executionId
    if (!score || !executionId) {
      throw new Error('no map')
    }

    const final = { executionId, total: 200, completed: 197, failed: 3 }
    score.map = final
    live.latest.emitPipeline({ type: 'mapProgress', update: { projectId: project.id, runId: qa.run.id, stageId: 'score', progress: final } })
    await waitFor(() => {
      expect(stages.getByRole('progressbar', { name: 'Items of Score the transcripts' })).toHaveAttribute(
        'aria-valuetext',
        '200 of 200 done, 3 failed',
      )
    })

    // An earlier read delivered late: fewer ended items of the same map execution.
    live.latest.emitPipeline({
      type: 'mapProgress',
      update: { projectId: project.id, runId: qa.run.id, stageId: 'score', progress: { executionId, total: 200, completed: 160, failed: 3 } },
    })
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(stages.getByRole('progressbar', { name: 'Items of Score the transcripts' })).toHaveAttribute(
      'aria-valuetext',
      '200 of 200 done, 3 failed',
    )
  })

  it('FR363_live_map_progress_monotonic: progress only replaces the cached one when it counts no fewer ended items', () => {
    const current = { executionId: 'm1', total: 10, completed: 6, failed: 1 }
    expect(isNewerMapProgress(undefined, current)).toBe(true)
    expect(isNewerMapProgress(current, { ...current, completed: 7 })).toBe(true)
    expect(isNewerMapProgress(current, { ...current })).toBe(true)
    expect(isNewerMapProgress(current, { ...current, completed: 5 })).toBe(false)
    expect(isNewerMapProgress(current, { ...current, total: 12, completed: 0, failed: 0 })).toBe(true)
    expect(isNewerMapProgress(current, { executionId: 'm2', total: 10, completed: 0, failed: 0 })).toBe(true)
  })

  it('FR363_live_lanes: a stageBranchUpdated message refreshes the lane states', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const parallel = seedParallelRun(project)
    const { live, stages } = await openPipeline(project.id, parallel.run.id)
    const lanes = await stages.findByRole('list', { name: 'Lanes of Analysts' })
    const bull = within(within(lanes).getByRole('listitem', { name: 'Branch 1' }))
    expect(await bull.findByText('Active')).toBeInTheDocument()

    // The server's state changes first (a refetch racing the message then reads the same), then the message arrives.
    const review = parallel.timeline.find((i) => i.kind === 'stage_entered' && i.stageId === 'bull_review')
    if (!review) {
      throw new Error('no bull_review entry')
    }
    const exitedAt = '2026-12-01T10:00:00.000Z'
    Object.assign(review, { exitedAt, outcome: 'completed' })
    live.latest.emitPipeline({
      type: 'stageBranchUpdated',
      update: {
        projectId: project.id,
        runId: parallel.run.id,
        executionId: review.id,
        stageId: 'bull_review',
        branch: '0',
        exitedAt,
        outcome: 'completed',
      },
    })
    await waitFor(() => {
      expect(bull.queryByText('Active')).not.toBeInTheDocument()
    })
    expect(bull.getAllByText('Completed')).toHaveLength(2)
  })

  it('FR363_live_other_project: branch and map messages for another project are ignored', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const other = seedProject({ name: 'Other' })
    const qa = seedQaRun(project, { total: 200, failed: 3, open: 47 })
    const { live, stages } = await openPipeline(project.id, qa.run.id)
    const bar = await stages.findByRole('progressbar', { name: 'Items of Score the transcripts' })
    const executionId = qa.stages.find((s) => s.id === 'score')?.map?.executionId ?? ''
    live.latest.emitPipeline({
      type: 'mapProgress',
      update: { projectId: other.id, runId: qa.run.id, stageId: 'score', progress: { executionId, total: 200, completed: 197, failed: 3 } },
    })
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(bar).toHaveAttribute('aria-valuetext', '153 of 200 done, 3 failed')
  })
})
