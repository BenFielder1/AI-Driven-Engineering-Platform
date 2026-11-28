import { screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'

import { seedUser } from '../../mocks/handlers/auth'
import { runControlDb, seedAgentFeedback } from '../../mocks/handlers/engine-run-control'
import { seedCoachRun } from '../../mocks/handlers/pipeline-seeds'
import { seedPromptStats } from '../../mocks/handlers/pipeline-store'
import { installRunControlMocks, memberOf, renderPipeline } from '../run-control/pipeline-harness.test-utils'

beforeEach(() => {
  installRunControlMocks()
})

const feedbackRegion = () => screen.getByRole('region', { name: 'Feedback' })

/** The list item holding `text` (fails the test when there is none). */
function itemWith(items: readonly HTMLElement[], text: string): HTMLElement {
  const found = items.find((i) => i.textContent.includes(text))
  if (!found) {
    throw new Error(`No item with "${text}"`)
  }
  return found
}

function seedTwoEntries(projectId: string, mine: string) {
  const other = seedUser({ email: 'other@example.com', name: 'Other' })
  seedAgentFeedback(projectId, { pipelineId: 'impulse-coach', agentKey: 'performance-analyst', text: 'Weigh the long run more.', createdBy: other.id })
  seedAgentFeedback(projectId, { pipelineId: 'impulse-coach', agentKey: 'performance-analyst', text: 'Mention sleep.', createdBy: mine })
}

describe('FR-365: scores and feedback', () => {
  it('FR365_prompt_comparison_shows_the_mean_score_per_metric', async () => {
    const { project, access } = memberOf('viewer')
    seedCoachRun(project)
    seedPromptStats(project, 'impulse-coach', [
      {
        agentKey: 'performance-analyst',
        promptVersion: '1',
        runs: 4,
        avgInputTokens: 1000,
        avgOutputTokens: 300,
        avgCost: 0.02,
        avgLatencyMs: 800,
        avgDurationMs: 9000,
        failureRate: 0,
        outcomes: [
          { metric: 'adherence', meanScore: 0.8125, count: 4 },
          { metric: 'brier', meanScore: 0.12, count: 3 },
        ],
      },
      {
        agentKey: 'performance-analyst',
        promptVersion: '2',
        runs: 1,
        avgInputTokens: 1100,
        avgOutputTokens: 320,
        avgCost: 0.021,
        avgLatencyMs: 820,
        avgDurationMs: 9100,
        failureRate: 0,
        outcomes: [{ metric: 'adherence', meanScore: 0.9, count: 1 }],
      },
    ])
    await renderPipeline(project.id, access)

    const table = await within(await screen.findByRole('region', { name: 'Prompt versions' })).findByRole('table', { name: 'performance-analyst' })
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent)
    expect(headers).toContain('Mean adherence')
    expect(headers).toContain('Mean brier')
    const rows = within(table).getAllByRole('row').slice(1)
    const v1 = itemWith(rows, 'v1')
    const v2 = itemWith(rows, 'v2')
    expect(v1).toHaveTextContent('0.813 (4 scored)')
    expect(v1).toHaveTextContent('0.12 (3 scored)')
    expect(v2).toHaveTextContent('0.9 (1 scored)')
    // A version with no score for a metric shows a dash, not zero.
    expect(within(v2).getAllByRole('cell').at(-1)).toHaveTextContent('—')
  })

  it('FR365_contributor_adds_feedback_but_cannot_delete_another_users_entry', async () => {
    const { project, access, user: me } = memberOf('contributor')
    seedCoachRun(project)
    seedTwoEntries(project.id, me.id)
    const { user } = await renderPipeline(project.id, access)

    const region = within(await screen.findByRole('region', { name: 'Feedback' }))
    expect(region.getByRole('combobox', { name: 'Agent' })).toHaveDisplayValue('performance-analyst')
    expect(region.getAllByRole('option').map((o) => o.textContent)).toEqual(['performance-analyst', 'programme-planner', 'coach-voice'])
    const list = await region.findByRole('list', { name: 'Feedback for performance-analyst' })
    const items = within(list).getAllByRole('listitem')
    expect(items).toHaveLength(2)
    const theirs = itemWith(items, 'Weigh the long run more.')
    const mine = itemWith(items, 'Mention sleep.')
    expect(within(theirs).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
    expect(within(mine).getByRole('button', { name: 'Delete' })).toBeInTheDocument()

    await user.click(region.getByRole('button', { name: 'Add feedback' }))
    expect(await region.findByText('Enter the feedback.')).toBeInTheDocument()
    await user.type(region.getByRole('textbox', { name: 'New feedback' }), 'Flag missed sessions.')
    await user.click(region.getByRole('button', { name: 'Add feedback' }))
    expect(await within(list).findByText('Flag missed sessions.')).toBeInTheDocument()
    expect(runControlDb.feedback.at(-1)).toMatchObject({ agentKey: 'performance-analyst', createdBy: me.id, source: 'human' })
    expect(region.getByRole('textbox', { name: 'New feedback' })).toHaveValue('')
  })

  it('FR365_owner_deletes_any_entry_after_confirming', async () => {
    const { project, access, user: me } = memberOf('owner')
    seedCoachRun(project)
    seedTwoEntries(project.id, me.id)
    const { user } = await renderPipeline(project.id, access)

    const list = await within(await screen.findByRole('region', { name: 'Feedback' })).findByRole('list', { name: 'Feedback for performance-analyst' })
    const theirs = itemWith(within(list).getAllByRole('listitem'), 'Weigh the long run more.')
    await user.click(within(theirs).getByRole('button', { name: 'Delete' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete this feedback?' })
    await user.click(within(confirm).getByRole('button', { name: 'Delete' }))
    await waitFor(() => {
      expect(within(list).queryByText('Weigh the long run more.')).not.toBeInTheDocument()
    })
    expect(within(list).getByText('Mention sleep.')).toBeInTheDocument()
  })

  it('FR365_viewer_reads_feedback_without_add_or_delete', async () => {
    const { project, access, user: me } = memberOf('viewer')
    seedCoachRun(project)
    seedTwoEntries(project.id, me.id)
    await renderPipeline(project.id, access)
    const region = within(await screen.findByRole('region', { name: 'Feedback' }))
    expect(await region.findByText('Mention sleep.')).toBeInTheDocument()
    expect(region.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
    expect(region.queryByRole('textbox', { name: 'New feedback' })).not.toBeInTheDocument()
    expect(feedbackRegion()).toBeInTheDocument()
  })
})
