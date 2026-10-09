import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it, onTestFinished } from 'vitest'

import { errorMessages } from '../../lib/errors'
import { addEvent, seedActivity } from '../../mocks/handlers/lifecycle'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { openOverviewAs } from '../../test/overview'
import { formatDateTime } from '../projects/labels'

const feedItems = () => within(screen.getByRole('list', { name: 'Activity' })).getAllByRole('listitem')

describe('Overview → Activity (FR-50)', () => {
  it('FR-50: shows the latest 50 newest first, and "Load more" follows nextBefore until the end', async () => {
    const queries: URLSearchParams[] = []
    onTestFinished(() => {
      server.events.removeAllListeners()
    })
    server.events.on('request:start', ({ request }) => {
      const url = new URL(request.url)
      if (url.pathname.endsWith('/activity')) {
        queries.push(url.searchParams)
      }
    })
    const { user } = await openOverviewAs('viewer', {}, (p) => {
      seedActivity(p, 120)
    })

    await screen.findByRole('list', { name: 'Activity' })
    let items = feedItems()
    expect(items).toHaveLength(50)
    expect(items[0]).toHaveTextContent('Event 120')
    expect(items[49]).toHaveTextContent('Event 71')
    expect(queries[0]?.get('limit')).toBe('50')
    expect(queries[0]?.has('before')).toBe(false)

    await user.click(screen.getByRole('button', { name: 'Load more' }))
    await waitFor(() => {
      expect(feedItems()).toHaveLength(100)
    })
    expect(queries[1]?.get('before')).toBeTruthy()

    await user.click(screen.getByRole('button', { name: 'Load more' }))
    await waitFor(() => {
      expect(feedItems()).toHaveLength(120)
    })
    items = feedItems()
    expect(items[119]).toHaveTextContent('Event 1')
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
  })

  it('FR-50: the category filter shows only that type of event', async () => {
    const { user } = await openOverviewAs('owner', {}, (p) => {
      seedActivity(p, 3, { category: 'transition', label: 'Phase change' })
      seedActivity(p, 2, { category: 'gate', type: 'GATE_CREATED', label: 'Gate event' })
    })
    await screen.findByRole('list', { name: 'Activity' })
    expect(feedItems()).toHaveLength(5)

    const filter = screen.getByLabelText('Show')
    expect(within(filter).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'All activity',
      'Phase changes',
      'Gates',
      'Artifacts',
      'Agent runs',
      'Execution steps',
      'Reviews',
      'Releases',
      'Rollbacks',
      'Project and members',
      'Integrity alerts',
    ])
    await user.selectOptions(filter, 'Gates')

    await waitFor(() => {
      expect(feedItems()).toHaveLength(2)
    })
    expect(feedItems().every((li) => li.textContent.includes('Gate event'))).toBe(true)

    await user.selectOptions(filter, 'Rollbacks')
    expect(await screen.findByText('No rollbacks yet')).toBeInTheDocument()
  })

  it('FR-50: each item shows the actor, a relative time with the absolute time as its title, and the summary', async () => {
    const occurredAt = new Date(Date.now() - 3 * 3600 * 1000).toISOString()
    await openOverviewAs('owner', {}, (p) => {
      addEvent(p, {
        type: 'GATE_APPROVED',
        category: 'gate',
        summary: 'Specification approval approved',
        actor: { id: null, name: 'Grace Hopper', isSystem: false },
        occurredAt,
      })
      addEvent(p, { type: 'RUN_STARTED', category: 'run', summary: 'Spec generator started' })
    })

    await screen.findByRole('list', { name: 'Activity' })
    const [systemRow, userRow] = feedItems()
    expect(systemRow).toHaveTextContent('Spec generator started')
    expect(systemRow).toHaveTextContent('System')
    expect(userRow).toHaveTextContent('Specification approval approved')
    expect(userRow).toHaveTextContent('Grace Hopper')
    const time = userRow?.querySelector('time')
    expect(time).toHaveTextContent('3 hours ago')
    expect(time).toHaveAttribute('title', formatDateTime(occurredAt))
    expect(time).toHaveAttribute('dateTime', occurredAt)
  })

  it('FR-50: summaries use the UI labels for phases, gate types and escalation decisions, not raw wire values', async () => {
    await openOverviewAs('owner', {}, (p) => {
      addEvent(p, {
        type: 'PHASE_CHANGED',
        category: 'transition',
        summary: 'Phase changed from specification to planning (version 3)',
        payload: { from: 'specification', to: 'planning', projectVersion: 3 },
      })
      addEvent(p, {
        type: 'GATE_CREATED',
        category: 'gate',
        summary: 'spec_approval gate created',
        payload: { type: 'spec_approval', projectVersion: 3 },
      })
      addEvent(p, {
        type: 'GATE_APPROVED',
        category: 'gate',
        summary: 'escalation gate decided: rollback',
        payload: { type: 'escalation', decision: 'rollback', targetVersion: 2 },
      })
      addEvent(p, {
        type: 'GATE_APPROVED',
        category: 'gate',
        summary: 'escalation gate decided: accept',
        payload: { type: 'escalation', decision: 'accept', targetVersion: null },
      })
      addEvent(p, {
        type: 'GATE_APPROVED',
        category: 'gate',
        summary: 'pre_release gate approved',
        payload: { type: 'pre_release', decision: null },
      })
      addEvent(p, {
        type: 'ROLLBACK_COMPLETED',
        category: 'rollback',
        summary: 'Rolled back from version 4 to version 2',
        payload: { fromVersion: 4, targetVersion: 2 },
      })
      addEvent(p, {
        type: 'ARTIFACT_APPROVED',
        category: 'artifact',
        summary: 'Artifact approved: technical_specification v2',
        payload: { type: 'technical_specification', version: 2 },
      })
    })

    await screen.findByRole('list', { name: 'Activity' })
    const texts = feedItems().map((li) => li.querySelector('span')?.textContent)
    expect(texts).toEqual([
      'Artifact approved: Technical specification v2',
      'Rolled back from version 4 to version 2',
      'Release approval gate approved',
      'Escalation gate decided: Accept',
      'Escalation gate decided: Roll back to version 2',
      'Specification approval gate created',
      'Phase changed from Specification to Planning (version 3)',
    ])
    const list = screen.getByRole('list', { name: 'Activity' })
    expect(list).not.toHaveTextContent(/spec_approval|pre_release|technical_specification|from specification/)
  })

  it('FR-50: unknown event types and malformed payloads fall back to the server summary', async () => {
    await openOverviewAs('owner', {}, (p) => {
      addEvent(p, { type: 'SOMETHING_NEW', category: null, summary: 'Something new happened', payload: { type: 'x' } })
      addEvent(p, {
        type: 'PHASE_CHANGED',
        category: 'transition',
        summary: 'Phase changed from ? to planning (version ?)',
        payload: { from: 42, to: 'planning' },
      })
      addEvent(p, {
        type: 'GATE_APPROVED',
        category: 'gate',
        summary: 'escalation gate decided: maybe',
        payload: { type: 'escalation', decision: 'maybe' },
      })
      addEvent(p, { type: 'GATE_CREATED', category: 'gate', summary: 'Approval gate created', payload: {} })
    })

    await screen.findByRole('list', { name: 'Activity' })
    expect(feedItems().map((li) => li.querySelector('span')?.textContent)).toEqual([
      'Approval gate created',
      'escalation gate decided: maybe',
      'Phase changed from ? to planning (version ?)',
      'Something new happened',
    ])
  })

  it('FR23_ activity: agent run and budget events are described with agent labels, tokens and durations', async () => {
    await openOverviewAs('owner', {}, (p) => {
      addEvent(p, {
        type: 'RUN_STARTED',
        category: 'run',
        summary: 'spec_generator run started',
        payload: { runId: 'r1', agent: 'spec_generator', provider: 'anthropic', model: 'claude-opus-5-5', promptVersion: '1', stepId: null },
      })
      addEvent(p, {
        type: 'RUN_COMPLETED',
        category: 'run',
        summary: 'spec_generator run completed',
        payload: { runId: 'r1', agent: 'spec_generator', inputTokens: 3412, outputTokens: 9876, costEstimate: 0.211, durationMs: 48250 },
      })
      addEvent(p, {
        type: 'RUN_FAILED',
        category: 'run',
        summary: 'code_generator run failed',
        payload: { runId: 'r2', agent: 'code_generator', errorCode: 'PROVIDER_ERROR', error: 'x', attempts: 4, deadLettered: true },
      })
      addEvent(p, {
        type: 'BUDGET_EXCEEDED',
        category: 'run',
        summary: 'Token budget exceeded',
        payload: { agent: 'code_review', tokenBudget: 100000, tokensUsed: 100000, jobId: 'j1' },
      })
      addEvent(p, { type: 'RUN_FAILED', category: 'run', summary: 'A run failed', payload: { agent: 'nobody' } })
    })

    await screen.findByRole('list', { name: 'Activity' })
    expect(feedItems().map((li) => li.querySelector('span')?.textContent)).toEqual([
      'A run failed',
      'Code review run refused: the token budget is used up (100,000 of 100,000 tokens used)',
      'Code generator run failed after 4 attempts',
      'Spec generator run completed: 13,288 tokens in 48.3 s',
      'Spec generator run started on claude-opus-5-5',
    ])
  })

  it('FR31_ activity: execution and step events are described from their payloads', async () => {
    await openOverviewAs('owner', {}, (p) => {
      const step = (type: string, payload: Record<string, unknown>) => {
        addEvent(p, { type, category: 'step', summary: type, payload })
      }
      step('EXECUTION_STARTED', { branch: 'aide/x' })
      step('STEP_STARTED', { stepId: 's1', stepNumber: 1, title: 'T', attempt: 1 })
      step('STEP_REJECTED', { stepId: 's1', stepNumber: 1, attempt: 1, retry: true, codes: ['X'], errorCount: 2, warningCount: 0 })
      step('STEP_BLOCKED', { stepId: 's1', stepNumber: 1, attempt: 4 })
      step('STEP_UNBLOCKED', { stepId: 's1', stepNumber: 1, gateId: 'g1' })
      step('STEP_COMPLETED', { stepId: 's1', stepNumber: 1, attempt: 1, commitSha: '3f2a9c1d8e7b', fileCount: 1, warningCount: 0 })
      step('EXECUTION_PAUSED', { paused: true })
      step('EXECUTION_RESUMED', { paused: false })
      step('EXECUTION_COMPLETED', { stepCount: 3 })
      step('STEP_STARTED', { stepNumber: 'one' })
    })

    await screen.findByRole('list', { name: 'Activity' })
    expect(feedItems().map((li) => li.querySelector('span')?.textContent)).toEqual([
      'STEP_STARTED',
      'Execution completed: all 3 steps done',
      'Execution resumed',
      'Execution paused: no new step starts until it is resumed',
      'Step 1 completed: commit 3f2a9c1',
      'Step 1 unblocked: it will be retried with fresh attempts',
      'Step 1 blocked after 4 attempts: an Owner decides its escalation',
      'Step 1 rejected by the Execution Guardian with 2 errors (attempt 1), retrying',
      'Step 1 started (attempt 1)',
      'Execution started',
    ])
  })

  it('FR-50: empty and error states', async () => {
    server.use(
      http.get('*/api/projects/:id/activity', () => problem(404, 'NOT_FOUND', 'The project was not found.')),
    )
    await openOverviewAs('owner')

    expect(await screen.findByText("The activity couldn't load")).toBeInTheDocument()
    expect(screen.getByText(errorMessages.NOT_FOUND)).toBeInTheDocument()
    server.resetHandlers()
    const activity = screen.getByText("The activity couldn't load").closest<HTMLElement>('[role="alert"]')
    expect(activity).not.toBeNull()
    if (activity) {
      await userEvent.click(within(activity).getByRole('button', { name: 'Try again' }))
    }
    expect(await screen.findByText('No activity yet')).toBeInTheDocument()
  })
})
