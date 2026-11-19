import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { nextId, signInAs } from '../../mocks/handlers/auth'
import { seedFixtureRun, seedSdlcRun } from '../../mocks/handlers/pipeline-seeds'
import { problem } from '../../mocks/handlers/problem'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { currentHref, renderRoute } from '../../test/render'

async function openReplay(projectId: string, runId?: string) {
  const utils = await renderRoute(`/projects/${projectId}/pipeline/replay${runId ? `?runId=${runId}` : ''}`)
  await screen.findByRole('heading', { level: 2, name: 'Replay' })
  return { ...utils, user: userEvent.setup() }
}

const scrubber = () => screen.findByRole('slider', { name: 'Timeline position' })
const entry = () => screen.getByRole('article', { name: 'Timeline entry' })

describe('Replay (FR-306)', () => {
  it('FR306_replay_stepper: Previous and Next step one entry at a time, by keyboard too', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const { user } = await openReplay(project.id, stored.run.id)

    const slider = await scrubber()
    expect(slider).toHaveValue('1')
    expect(slider).toHaveAttribute('aria-valuetext', 'Entry 1 of 12: Entered Draft')
    expect(screen.getByRole('button', { name: 'Previous' })).toHaveAttribute('aria-disabled', 'true')

    const next = screen.getByRole('button', { name: 'Next' })
    next.focus()
    await user.keyboard('{Enter}')
    expect(slider).toHaveValue('2')
    expect(within(entry()).getByText('drafter run in Draft')).toBeInTheDocument()
    await user.keyboard(' ')
    expect(slider).toHaveValue('3')
    expect(within(entry()).getByText('Draft v1')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Previous' }))
    expect(slider).toHaveValue('2')

    fireEvent.change(slider, { target: { value: '12' } })
    expect(within(entry()).getByText('Report approval raised')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Next' })).toHaveAttribute('aria-disabled', 'true')
  })

  it('FR306_replay_focus_at_ends: Previous and Next stay focusable (aria-disabled) and do nothing at the ends', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const { user } = await openReplay(project.id, stored.run.id)
    const slider = await scrubber()

    const previous = screen.getByRole('button', { name: 'Previous' })
    expect(previous).toHaveAttribute('aria-disabled', 'true')
    expect(previous).toBeEnabled()
    previous.focus()
    await user.keyboard('{Enter}')
    expect(slider).toHaveValue('1')
    expect(previous).toHaveFocus()

    fireEvent.change(slider, { target: { value: '11' } })
    const next = screen.getByRole('button', { name: 'Next' })
    next.focus()
    await user.keyboard('{Enter}')
    expect(slider).toHaveValue('12')
    expect(next).toHaveAttribute('aria-disabled', 'true')
    expect(next).toHaveFocus()
    await user.keyboard('{Enter}')
    expect(slider).toHaveValue('12')
    expect(next).toHaveFocus()
  })

  it('FR306_replay_artifact_states: the version list loads first, and a load error offers a retry (never "first version")', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    server.use(http.get('*/api/pipeline-runs/:runId/artifacts', () => problem(500, 'UPSTREAM_ERROR', 'Boom')))
    await openReplay(project.id, stored.run.id)
    fireEvent.change(await scrubber(), { target: { value: '9' } })

    expect(within(entry()).getByText('Loading the versions of Check report…')).toBeInTheDocument()
    const alert = await within(entry()).findByRole('alert')
    expect(alert).toHaveTextContent("The versions of Check report couldn't load")
    expect(within(alert).getByRole('button', { name: 'Try again' })).toBeInTheDocument()
    expect(within(entry()).queryByText(/The first version of/)).not.toBeInTheDocument()
  })

  it('FR306_replay_live_after_empty_page: a page load that brings nothing still lets the next live entry be announced', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    // Every entry on the first page, with a cursor to a second page that turns out empty.
    server.use(
      http.get('*/api/pipeline-runs/:runId/replay', ({ request }) =>
        new URL(request.url).searchParams.get('cursor')
          ? HttpResponse.json({ items: [], next: null })
          : HttpResponse.json({ items: stored.timeline, next: 'more' }),
      ),
    )
    const { live, user } = await openReplay(project.id, stored.run.id)
    const slider = await scrubber()
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })
    fireEvent.change(slider, { target: { value: '12' } })
    await user.click(screen.getByRole('button', { name: 'Next' }))
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Next' })).toHaveAttribute('aria-disabled', 'true')
    })

    stored.timeline.push({ id: nextId(), at: '2026-12-01T10:00:00.000Z', kind: 'gate_decided', gateType: 'report_approval', gateStatus: 'approved' })
    const gate = stored.gates[0]
    if (!gate) {
      throw new Error('no gate')
    }
    live.latest.emitPipeline({ type: 'gateUpdated', gate: { ...gate, status: 'approved' } })
    await waitFor(() => {
      expect(screen.getByTestId('replay-live-region')).toHaveTextContent('1 new timeline entry')
    })
  })

  it('FR306_replay_agent_entry: input and output side by side, with a truncation notice when cut', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const run = stored.timeline[1]
    if (run) {
      run.output = 'x'.repeat(200_050)
    }
    await openReplay(project.id, stored.run.id)
    fireEvent.change(await scrubber(), { target: { value: '2' } })

    const input = await within(entry()).findByRole('group', { name: 'Input' })
    expect(input).toHaveTextContent('You are the drafter.')
    expect(input).toHaveAttribute('tabindex', '0')
    const output = within(entry()).getByRole('group', { name: 'Output' })
    expect(output.textContent).toHaveLength(200_000)
    expect(within(entry()).getByText(/This output is cut short/)).toBeInTheDocument()
    expect(within(entry()).queryByText(/This input is cut short/)).not.toBeInTheDocument()
  })

  it('FR306_replay_artifact_entry: an artifact version shows its diff to the previous version', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    await openReplay(project.id, stored.run.id)
    fireEvent.change(await scrubber(), { target: { value: '9' } })

    expect(within(entry()).getByText('Check report v2')).toBeInTheDocument()
    expect(await within(entry()).findByRole('table', { name: /Changes from v1 to v2/ })).toBeInTheDocument()

    fireEvent.change(await scrubber(), { target: { value: '3' } })
    expect(await within(entry()).findByText('The first version of Draft.')).toBeInTheDocument()
  })

  it('FR306_replay_live: a new timeline entry from a live message is announced in the live region', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const { live } = await openReplay(project.id, stored.run.id)
    await scrubber()
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })

    stored.timeline.push({ id: nextId(), at: '2026-12-01T10:00:00.000Z', kind: 'gate_decided', gateType: 'report_approval', gateStatus: 'approved' })
    const gate = stored.gates[0]
    if (!gate) {
      throw new Error('no gate')
    }
    gate.status = 'approved'
    live.latest.emitPipeline({ type: 'gateUpdated', gate })

    await waitFor(() => {
      expect(screen.getByTestId('replay-live-region')).toHaveTextContent('1 new timeline entry')
    })
    expect(await scrubber()).toHaveAttribute('max', '13')
  })

  it('FR306_replay_states: no run id, an unknown run and a run of another project are not found', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const other = seedProject({ name: 'Other' })
    const elsewhere = seedSdlcRun(other)

    const first = await openReplay(project.id)
    expect(await screen.findByRole('heading', { name: 'Run not found' })).toBeInTheDocument()
    first.unmount()
    const { router, user, unmount } = await openReplay(project.id, nextId())
    expect(await screen.findByRole('heading', { name: 'Run not found' })).toBeInTheDocument()
    const [back] = screen.getAllByRole('link', { name: 'Back to the pipeline' })
    if (!back) {
      throw new Error('no back link')
    }
    await user.click(back)
    await waitFor(() => {
      expect(currentHref(router)).toMatch(new RegExp(`^/projects/${project.id}/pipeline`))
    })
    unmount()

    await openReplay(project.id, elsewhere.run.id)
    expect(await screen.findByRole('heading', { name: 'Run not found' })).toBeInTheDocument()
  })

  it.each(THEMES)('FR306_a11y: Replay has no axe violations (%s theme)', async (theme) => {
    setTheme(theme)
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    const { container } = await openReplay(project.id, stored.run.id)
    fireEvent.change(await scrubber(), { target: { value: '2' } })
    await within(entry()).findByRole('group', { name: 'Input' })
    expect(await axeViolations(container)).toEqual([])
  })
})
