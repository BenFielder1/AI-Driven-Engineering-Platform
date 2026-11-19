import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { nextId, signInAs } from '../../mocks/handlers/auth'
import { seedFixtureRun, seedSdlcRun } from '../../mocks/handlers/pipeline-seeds'
import { addTimelineEntries } from '../../mocks/handlers/pipeline-store'
import { problem } from '../../mocks/handlers/problem'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { currentHref, renderRoute } from '../../test/render'

async function openPipeline(projectId: string, query = '') {
  const utils = await renderRoute(`/projects/${projectId}/pipeline${query}`)
  await screen.findByRole('heading', { level: 2, name: 'Pipeline' })
  return { ...utils, user: userEvent.setup() }
}

const region = (name: string) => screen.getByRole('region', { name })

describe('Pipeline tab (FR-306)', () => {
  it('FR306_pipeline_tab: shows the newest run with its summary, stages, timeline, artifacts and prompt versions', async () => {
    signInAs()
    const project = seedProject({ name: 'Inventory tracker' })
    seedSdlcRun(project)
    await openPipeline(project.id)

    const summary = await screen.findByTestId('run-summary')
    expect(within(summary).getByRole('heading', { name: 'Software delivery' })).toBeInTheDocument()
    expect(within(summary).getByText('Waiting on a gate')).toBeInTheDocument()
    expect(within(summary).getByText('Specification')).toBeInTheDocument()
    expect(within(summary).getByText('a1b2c3d4e5f6')).toBeInTheDocument()
    expect(within(summary).getByText('$0.48')).toBeInTheDocument()

    const stages = within(region('Stages')).getByRole('table')
    const rows = within(stages).getAllByRole('row').slice(1)
    expect(rows.map((r) => within(r).getByRole('rowheader').textContent)).toEqual([
      'Analysis',
      'Refinement',
      'Specification',
      'Planning',
      'Execution',
      'Review',
      'Release',
    ])
    expect(rows[2]).toHaveAttribute('aria-current', 'step')
    expect(rows[1]).toHaveTextContent('Completed')
    expect(rows[3]).toHaveTextContent('Pending')

    const timeline = within(region('Timeline'))
    expect(await timeline.findByText('Refinement → Specification')).toBeInTheDocument()
    expect(timeline.getByText('Spec approval raised')).toBeInTheDocument()

    const artifacts = within(region('Artifacts'))
    expect(await artifacts.findByRole('button', { name: /Technical specification/ })).toBeInTheDocument()

    const prompts = within(region('Prompt versions'))
    expect(await prompts.findByRole('table', { name: 'spec_generator' })).toBeInTheDocument()
    expect(document.title).toBe('Pipeline · Inventory tracker · Tevrin')
  })

  it('FR306_run_picker: lists runs newest first and switching replaces the URL entry', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const older = seedFixtureRun(project)
    seedSdlcRun(project)
    const { user, router } = await openPipeline(project.id)

    const picker = await screen.findByRole('combobox', { name: 'Run' })
    const options = within(picker).getAllByRole('option').map((o) => o.textContent)
    expect(options[0]).toMatch(/^Software delivery/)
    expect(options[1]).toMatch(/^Fixture review/)

    await user.selectOptions(picker, older.run.id)
    await waitFor(() => {
      expect(currentHref(router)).toBe(`/projects/${project.id}/pipeline?run=${older.run.id}`)
    })
    expect(await within(await screen.findByTestId('run-summary')).findByRole('heading', { name: 'Fixture review' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Replay this run' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/pipeline/replay?runId=${older.run.id}`,
    )
  })

  it('FR306_artifacts: opens a version and its changes from the previous version', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    seedSdlcRun(project)
    const { user } = await openPipeline(project.id)

    const artifacts = within(region('Artifacts'))
    await user.click(await artifacts.findByRole('button', { name: /Technical specification/ }))
    expect(await artifacts.findByRole('article', { name: 'Technical specification v2' })).toHaveTextContent('Track stock levels per warehouse.')
    await user.click(artifacts.getByRole('button', { name: 'Changes from v1' }))
    expect(await artifacts.findByRole('table', { name: /Changes from v1 to v2/ })).toBeInTheDocument()
  })

  it('FR306_timeline_paging: "Load more" follows the cursor until every entry is shown once', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    addTimelineEntries(stored, 108, 'summarise')
    const { user } = await openPipeline(project.id)

    const timeline = within(region('Timeline'))
    await timeline.findByText('Entered Draft')
    expect(timeline.getAllByRole('listitem')).toHaveLength(50)
    await user.click(timeline.getByRole('button', { name: 'Load more' }))
    await waitFor(() => {
      expect(timeline.getAllByRole('listitem')).toHaveLength(100)
    })
    await user.click(timeline.getByRole('button', { name: 'Load more' }))
    await waitFor(() => {
      expect(timeline.getAllByRole('listitem')).toHaveLength(120)
    })
    expect(timeline.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
  })

  it('FR306_unknown_run: an unknown ?run= says "Run not found" and links to the latest run', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    seedFixtureRun(project)
    const latest = seedSdlcRun(project)
    const { user, router } = await openPipeline(project.id, `?run=${nextId()}`)

    expect(await screen.findByRole('heading', { level: 3, name: 'Run not found' })).toBeInTheDocument()
    expect(screen.queryByTestId('run-summary')).not.toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: 'Show the latest run' }))
    await waitFor(() => {
      expect(currentHref(router)).toBe(`/projects/${project.id}/pipeline?run=${latest.run.id}`)
    })
    const summary = await screen.findByTestId('run-summary')
    expect(within(summary).getByRole('heading', { level: 4, name: 'Software delivery' })).toBeInTheDocument()
  })

  it('FR306_timeline_live: entries a live message adds are announced in the timeline live region', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const { live } = await openPipeline(project.id)
    const timeline = within(region('Timeline'))
    await timeline.findByText('Entered Draft')
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })
    expect(screen.getByTestId('timeline-live-region')).toHaveTextContent('')

    stored.timeline.push({ id: nextId(), at: '2026-12-01T10:00:00.000Z', kind: 'stage_entered', stageId: 'summarise', iteration: 2, checkpoint: 3 })
    const stage = stored.stages[2]
    if (!stage) {
      throw new Error('no stage')
    }
    live.latest.emitPipeline({ type: 'stageUpdated', stage })
    await waitFor(() => {
      expect(screen.getByTestId('timeline-live-region')).toHaveTextContent('1 new timeline entry')
    })
    expect(timeline.getAllByRole('listitem')).toHaveLength(13)
  })

  it('FR306_states: empty ("No runs yet"), error and not found', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    await openPipeline(project.id)
    expect(await screen.findByRole('heading', { name: 'No runs yet' })).toBeInTheDocument()
  })

  it('FR306_states: a load error shows the shared error state with a retry', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    server.use(http.get('*/api/projects/:id/pipeline-runs', () => problem(500, 'UPSTREAM_ERROR', 'Boom')))
    await openPipeline(project.id)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("The pipeline runs couldn't load")
    expect(within(alert).getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('FR306_states: a 404 for the runs shows "Run not found"', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    server.use(http.get('*/api/projects/:id/pipeline-runs', () => problem(404, 'NOT_FOUND', 'Gone')))
    await openPipeline(project.id)
    expect(await screen.findByRole('heading', { name: 'Run not found' })).toBeInTheDocument()
  })

  it.each(THEMES)('FR306_a11y: the Pipeline tab has no axe violations (%s theme)', async (theme) => {
    setTheme(theme)
    signInAs()
    const project = seedProject({ name: 'P' })
    seedFixtureRun(project)
    const { container } = await openPipeline(project.id)
    await within(region('Prompt versions')).findByRole('table', { name: 'drafter' })
    await within(region('Artifacts')).findByRole('group', { name: /Draft v1/ })
    await within(region('Timeline')).findByText('Entered Draft')
    expect(await axeViolations(container)).toEqual([])
  })
})
