import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it, onTestFinished } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import {
  addTimelineItems,
  callsimTurns,
  constructDefinitions,
  seedCallsimRun,
  seedParallelRun,
  seedQaRun,
  seedVariantsRun,
} from '../../mocks/handlers/engine-constructs'
import { seedProject } from '../../mocks/handlers/projects'
import { axeViolations } from '../../test/axe'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { MAP_ITEMS_PAGE_SIZE } from '../api/hooks'
import type { TimelineItem } from '../api/types'
import { definitionLabels } from '../labels'
import { describeEntry } from '../timeline-text'

const engineDir = join(import.meta.dirname, '..')

/** The `i`th element, failing the test when there isn't one. */
function nth(elements: readonly HTMLElement[], i: number): HTMLElement {
  const el = elements[i]
  if (!el) {
    throw new Error(`No element ${String(i)}`)
  }
  return el
}

async function openPipeline(projectId: string, runId: string) {
  const utils = await renderRoute(`/projects/${projectId}/pipeline?run=${runId}`)
  const stages = await screen.findByRole('region', { name: 'Stages' })
  return { ...utils, stages: within(stages), user: userEvent.setup() }
}

function engineSources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) {
      return engineSources(path)
    }
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name) ? [path] : []
  })
}

describe('FR-363: new constructs in the stage list and timeline', () => {
  it('FR363_acceptance: fixture-parallel, fixture-qa, the variants fixture and fixture-callsim show lanes, map progress, variant tabs and the transcript', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const parallel = seedParallelRun(project)
    const qa = seedQaRun(project, { total: 200, failed: 3, open: 47 })
    const variants = seedVariantsRun(project)
    const callsim = seedCallsimRun(project)

    // Two lanes, labelled by branch, each with its stages and their states.
    const first = await openPipeline(project.id, parallel.run.id)
    const lanes = await first.stages.findByRole('list', { name: 'Lanes of Analysts' })
    const laneItems = within(lanes).getAllByRole('listitem', { name: /^Branch/ })
    expect(laneItems.map((l) => l.getAttribute('aria-label'))).toEqual(['Branch 1', 'Branch 2'])
    const bull = within(nth(laneItems, 0))
    expect(bull.getByText('Bull analyst')).toBeInTheDocument()
    expect(bull.getByText('Bull risks')).toBeInTheDocument()
    expect(await bull.findByText('Active')).toBeInTheDocument()
    expect(within(nth(laneItems, 1)).getByText('Completed')).toBeInTheDocument()
    first.unmount()

    // A map progress bar: 200 total, 3 failed.
    const second = await openPipeline(project.id, qa.run.id)
    const bar = await second.stages.findByRole('progressbar', { name: 'Items of Score the transcripts' })
    expect(bar).toHaveAttribute('aria-valuemax', '200')
    expect(bar).toHaveAttribute('aria-valuetext', '153 of 200 done, 3 failed')
    const progress = within(second.stages.getByTestId('map-progress'))
    expect(progress.getByText('Total 200')).toBeInTheDocument()
    expect(progress.getByText('Failed 3')).toBeInTheDocument()
    expect(progress.getByText('Done 150')).toBeInTheDocument()
    second.unmount()

    // v1–v3 tabs, the chosen v2 marked and open.
    const third = await openPipeline(project.id, variants.run.id)
    const tabs = await third.stages.findByRole('tablist', { name: 'Variants of Headline' })
    const tabNames = within(tabs).getAllByRole('tab').map((t) => t.textContent)
    expect(tabNames).toEqual(['v1', 'v2✓(chosen)', 'v3'])
    expect(within(tabs).getByRole('tab', { name: /v2.*chosen/ })).toHaveAttribute('aria-selected', 'true')
    const section = tabs.closest('section')
    expect(section).not.toBeNull()
    const headline = within(section ?? document.body)
    const panel = headline.getByRole('tabpanel')
    expect(within(panel).getByText('Chosen')).toBeInTheDocument()
    expect(await within(panel).findByText('Squeezed this morning')).toBeInTheDocument()
    await third.user.click(within(tabs).getByRole('tab', { name: 'v3' }))
    expect(await within(headline.getByRole('tabpanel')).findByText('Juice, done right')).toBeInTheDocument()
    third.unmount()

    // The transcript as a chat.
    const fourth = await openPipeline(project.id, callsim.run.id)
    const transcript = await fourth.stages.findByRole('list', { name: 'Transcript: Transcript v1' })
    const turns = within(transcript).getAllByRole('listitem')
    expect(turns).toHaveLength(callsimTurns.length)
    expect(turns[0]).toHaveTextContent('customer')
    expect(turns[0]).toHaveTextContent('My order never arrived.')
    expect(turns[1]).toHaveTextContent('agent')
    expect(turns[1]).toHaveTextContent('Sorry about that. I have sent a replacement.')
    expect(await axeViolations(document.body)).toEqual([])
  })

  it('FR363_no_fixture_names_in_src_engine: no stage, artifact or agent key of the construct fixtures appears in the engine sources', () => {
    const names = constructDefinitions.flatMap((d) => [
      d.pipelineId,
      ...d.stages.flatMap((s) => [s.id, s.agentKey, ...(s.branches ?? []).flatMap((b) => b.stages.map((x) => x.id))]),
      ...d.artifacts.map((a) => a.key),
    ])
    // `decision` and `transcript` are also plain words of the engine's own copy (an escalation decision, a conversation's
    // transcript), so they are left out, as FR-305 leaves out `draft`.
    const generic = new Set(['decision', 'transcript'])
    const distinct = [...new Set(names.filter((n): n is string => Boolean(n) && !generic.has(n ?? '')))]
    const found: string[] = []
    for (const file of engineSources(engineDir)) {
      const text = readFileSync(file, 'utf8')
      for (const name of distinct) {
        if (new RegExp(`['"\`]${name}['"\`]|\\b${name}\\s*:`).test(text)) {
          found.push(`${relative(engineDir, file)}: ${name}`)
        }
      }
    }
    expect(found).toEqual([])
  })

  it('FR363_map_drill_down_paging: Show items lists the items a page at a time, filtered by status', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const qa = seedQaRun(project, { total: 120, failed: 3, open: 7 })
    const cursors: (string | null)[] = []
    const record = ({ request }: { request: Request }) => {
      if (/stage-executions\/[^/]+\/items/.test(request.url)) {
        cursors.push(new URL(request.url).searchParams.get('cursor'))
      }
    }
    server.events.on('request:start', record)
    onTestFinished(() => {
      server.events.removeListener('request:start', record)
    })
    const { stages, user } = await openPipeline(project.id, qa.run.id)
    const toggle = await stages.findByRole('button', { name: 'Show items' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await user.click(toggle)
    expect(stages.getByRole('button', { name: 'Hide items' })).toHaveAttribute('aria-expanded', 'true')

    const list = await stages.findByRole('list', { name: 'Items of Score the transcripts' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(MAP_ITEMS_PAGE_SIZE)
    expect(within(list).getAllByRole('listitem')[0]).toHaveTextContent('Item 1')
    expect(within(list).getAllByRole('listitem')[0]).toHaveTextContent('Failed')
    expect(within(list).getAllByRole('listitem')[0]).toHaveTextContent('The transcript of item 0 was empty.')

    await user.click(stages.getByRole('button', { name: 'Load more items' }))
    await waitFor(() => {
      expect(within(list).getAllByRole('listitem')).toHaveLength(MAP_ITEMS_PAGE_SIZE * 2)
    })
    await user.click(stages.getByRole('button', { name: 'Load more items' }))
    await waitFor(() => {
      expect(within(list).getAllByRole('listitem')).toHaveLength(120)
    })
    expect(stages.queryByRole('button', { name: 'Load more items' })).not.toBeInTheDocument()
    expect(cursors).toEqual([null, '50', '100'])

    fireEvent.change(stages.getByRole('combobox', { name: 'Show items' }), { target: { value: 'failed' } })
    const failed = await stages.findByRole('list', { name: 'Items of Score the transcripts' })
    await waitFor(() => {
      expect(within(failed).getAllByRole('listitem')).toHaveLength(3)
    })
    expect(within(failed).getAllByRole('listitem').map((li) => within(li).getByText(/^Item \d+$/).textContent)).toEqual([
      'Item 1',
      'Item 2',
      'Item 3',
    ])
  })

  it('FR363_follow_ups_due_times: the run summary lists each follow-up with its due time, state and times', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const callsim = seedCallsimRun(project)
    await openPipeline(project.id, callsim.run.id)
    const section = within(await screen.findByTestId('run-follow-ups'))
    expect(section.getByRole('heading', { name: 'Follow-ups' })).toBeInTheDocument()
    const rows = section.getAllByRole('row').slice(1)
    expect(rows).toHaveLength(2)
    expect(within(nth(rows, 0)).getByRole('rowheader')).toHaveTextContent('Review the callFollow-up after 1d')
    expect(within(nth(rows, 0)).getAllByRole('cell')[1]).toHaveTextContent('Completed')
    expect(within(nth(rows, 1)).getByRole('rowheader')).toHaveTextContent('Follow-up after 7d')
    expect(within(nth(rows, 1)).getAllByRole('cell')[1]).toHaveTextContent('Scheduled')
    const due = within(nth(rows, 1)).getAllByRole('cell')[0]?.querySelector('time')
    expect(due).toHaveAttribute('dateTime', '2026-11-08T12:00:00.000Z')
  })

  it('FR363_follow_ups_unscheduled: before the run completes, follow-ups have no due time yet', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const callsim = seedCallsimRun(project)
    callsim.run.status = 'active'
    callsim.run.followUps = (callsim.run.followUps ?? []).map((f) => ({ ...f, dueAt: null, stageExecutionId: null, enteredAt: null, exitedAt: null, outcome: null }))
    await openPipeline(project.id, callsim.run.id)
    const section = within(await screen.findByTestId('run-follow-ups'))
    expect(section.getByText('Follow-ups are scheduled when the run completes.')).toBeInTheDocument()
    expect(section.getAllByText('When the run completes')).toHaveLength(2)
    expect(section.getAllByText('Not scheduled')).toHaveLength(2)
  })

  it('FR363_veto_and_route_entries: the timeline labels a vetoed stage entry with its findings, and a route back', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedVariantsRun(project)
    addTimelineItems(stored, [
      { kind: 'stage_entered', stageId: 'tagline', iteration: 1, checkpoint: 1, exitedAt: '2026-11-01T12:00:00.000Z', outcome: 'route' },
      {
        kind: 'stage_entered',
        stageId: 'tagline',
        iteration: 2,
        checkpoint: 1,
        exitedAt: '2026-11-01T12:00:00.000Z',
        outcome: 'vetoed',
        vetoFindings: ['brand-check: names a competitor', 'length: over 60 characters'],
      },
    ])
    await openPipeline(project.id, stored.run.id)
    const timeline = within(screen.getByRole('region', { name: 'Timeline' }))
    expect(await timeline.findByText('Routed back')).toBeInTheDocument()
    expect(timeline.getByText('Vetoed')).toBeInTheDocument()
    expect(timeline.getByText(/brand-check: names a competitor/)).toHaveTextContent(
      'Veto findings: brand-check: names a competitor length: over 60 characters',
    )
  })

  it('FR363_entry_labels: entries carry their branch, variant, map item, follow-up and turn as facts', () => {
    const labels = definitionLabels(constructDefinitions[0])
    const item = (extra: Partial<TimelineItem>): TimelineItem => ({ id: 'x', at: '2026-11-01T09:00:00Z', kind: 'stage_entered', stageId: 'bull', ...extra })
    expect(describeEntry(item({ branch: '0' }), labels).facts).toContain('Branch 1')
    expect(describeEntry(item({ branch: 'v2', variant: 'v2' }), labels).facts).toEqual(['variant v2'])
    expect(describeEntry(item({ itemIndex: 4 }), labels).facts).toContain('Item 5')
    expect(describeEntry(item({ followUp: 'followup:7d', variant: 'followup:7d' }), labels).facts).toEqual(['Follow-up after 7d'])
    const turn = describeEntry(item({ kind: 'agent_run', agentKey: 'customer-sim', turnIndex: 2, turnRole: 'customer' }), labels)
    expect(turn.title).toBe('customer-sim turn in Bull analyst')
    expect(turn.facts).toContain('turn 2 (customer)')
    expect(describeEntry(item({ kind: 'transition', fromStage: 'decide', toStage: 'analysts', transitionKind: 'route' }), labels).flag).toBe('routed')
  })

  it('FR363_replay_conversation: the replay shows each turn as a chat turn and the transcript artifact as a chat', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedCallsimRun(project)
    await renderRoute(`/projects/${project.id}/pipeline/replay?runId=${stored.run.id}`)
    const slider = await screen.findByRole('slider', { name: 'Timeline position' })
    fireEvent.change(slider, { target: { value: '3' } })
    const entry = within(screen.getByRole('article', { name: 'Timeline entry' }))
    expect(entry.getByText('support-sim turn in Simulate the call')).toBeInTheDocument()
    expect(entry.getByText(/turn 2 \(agent\)/)).toBeInTheDocument()
    expect(within(entry.getByRole('list', { name: 'Turn 2' })).getByText('Sorry about that. I have sent a replacement.')).toBeInTheDocument()

    fireEvent.change(slider, { target: { value: '5' } })
    const artifact = within(screen.getByRole('article', { name: 'Timeline entry' }))
    const chat = await artifact.findByRole('list', { name: 'Transcript: Transcript v1' })
    expect(within(chat).getAllByRole('listitem')).toHaveLength(3)
  })
})
