import { fireEvent, screen, within } from '@testing-library/react'
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../mocks/handlers/auth'
import { impulseCoachDefinition } from '../mocks/handlers/pipeline-definitions'
import { seedCoachRun } from '../mocks/handlers/pipeline-seeds'
import { seedProject } from '../mocks/handlers/projects'
import { renderRoute } from '../test/render'

const engineDir = import.meta.dirname

/** The engine's non-test sources (tests may name a pack; the UI code may not). */
function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) {
      return sources(path)
    }
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name) ? [path] : []
  })
}

describe('FR-339: the engine UI renders the Impulse coaching run from its definition', () => {
  it('FR339_no_coach_code_in_src_engine: no engine source names the coach pack', () => {
    const files = sources(engineDir)
    expect(files.length).toBeGreaterThan(10)
    const coachWords = /impulse|coach|athlete|velocity/i
    const found = files.filter((file) => coachWords.test(readFileSync(file, 'utf8'))).map((file) => relative(engineDir, file))
    expect(found).toEqual([])
  })

  it('FR339_coach_pipeline_tab: the coach run shows its four stages, its artifacts and the plan approval gate', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    seedCoachRun(project)
    await renderRoute(`/projects/${project.id}/pipeline`)

    const summary = await screen.findByTestId('run-summary')
    expect(within(summary).getByRole('heading', { name: 'Impulse coaching' })).toBeInTheDocument()
    expect(within(summary).getByText('Review')).toBeInTheDocument()

    const stages = screen.getByRole('region', { name: 'Stages' })
    const rows = (await within(stages).findAllByRole('row')).slice(1)
    expect(rows.map((r) => within(r).getByRole('rowheader').textContent)).toEqual(impulseCoachDefinition.stages.map((s) => s.title))
    expect(rows.map((r) => within(r).getByRole('rowheader').textContent)).toEqual(['Analyse', 'Plan', 'Review', 'Write'])
    expect(rows.map((r) => r.dataset.state)).toEqual(['completed', 'completed', 'active', 'pending'])

    const artifacts = within(screen.getByRole('region', { name: 'Artifacts' }))
    expect((await artifacts.findAllByRole('button', { name: /v\d/ })).map((b) => b.textContent)).toEqual([
      'Session logv1',
      'Performancev1',
      'Plan draftv2',
    ])
    expect(await artifacts.findByRole('group', { name: 'Session log v1 (JSON)' })).toHaveTextContent('"type": "tempo"')

    const timeline = within(screen.getByRole('region', { name: 'Timeline' }))
    expect(await timeline.findByText('Plan approval raised')).toBeInTheDocument()
    expect(timeline.getByText('Plan → Review')).toBeInTheDocument()
    expect(timeline.getByText('validation_rejected: /weeks/0: weekly load rises more than 10%')).toBeInTheDocument()
  })

  it('FR339_coach_replay: the replay steps through the coach run to its plan approval gate', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedCoachRun(project)
    await renderRoute(`/projects/${project.id}/pipeline/replay?runId=${stored.run.id}`)

    const slider = await screen.findByRole('slider', { name: 'Timeline position' })
    // Position 0 is the run's start; each timeline entry follows.
    expect(slider).toHaveAttribute('max', String(stored.timeline.length))
    fireEvent.change(slider, { target: { value: '3' } })
    expect(within(screen.getByRole('article', { name: 'Timeline entry' })).getByText('performance-analyst run in Analyse')).toBeInTheDocument()
    fireEvent.change(slider, { target: { value: '7' } })
    const rejected = screen.getByRole('article', { name: 'Timeline entry' })
    expect(within(rejected).getByText('programme-planner run in Plan')).toBeInTheDocument()
    expect(within(rejected).getByRole('group', { name: 'Output' })).toHaveTextContent('{"weeks":[{"loadKm":60}]}')
    fireEvent.change(slider, { target: { value: String(stored.timeline.length) } })
    expect(within(screen.getByRole('article', { name: 'Timeline entry' })).getByText('Plan approval raised')).toBeInTheDocument()
  })
})
