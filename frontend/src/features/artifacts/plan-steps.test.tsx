import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../lib/errors'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import { createExecutionSteps, planMarkdown, planSteps, type MockPlanStep } from '../../mocks/handlers/execution'
import type { StoredProject } from '../../mocks/handlers/projects'
import { openArtifact, seedProjectAs } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { readPlanBlock } from './plan-block'

const PLAN = 'implementation_plan'

const steps: MockPlanStep[] = [
  { number: 1, title: 'Set up the schema', task: 'Add the tables.', files: [{ path: 'db/schema.sql', action: 'create' }], dependencies: [] },
  {
    number: 2,
    title: 'Items API',
    task: 'Expose the items.',
    files: [
      { path: 'src/items.ts', action: 'create' },
      { path: 'src/app.ts', action: 'modify' },
    ],
    dependencies: [1],
  },
]

function seedPlanning(role: 'owner' | 'viewer', setup: (p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    p.currentPhase = 'planning'
    setup(p)
  })
}

/** The body rows of the steps table (the first row is the header). */
function stepRows(name: RegExp) {
  const table = screen.getByRole('table', { name })
  return within(table).getAllByRole('row').slice(1)
}

describe('Plan steps (FR-29, FR-30)', () => {
  it('FR29_reads_the_structured_steps_from_the_aide_plan_block', () => {
    expect(readPlanBlock(planMarkdown(steps))).toEqual([
      // FR-76: `agent` and `covers` are absent from this v1-style block.
      { number: 1, title: 'Set up the schema', files: [{ path: 'db/schema.sql', action: 'create' }], dependencies: [], agent: null, covers: [] },
      {
        number: 2,
        title: 'Items API',
        files: [
          { path: 'src/items.ts', action: 'create' },
          { path: 'src/app.ts', action: 'modify' },
        ],
        dependencies: [1],
        agent: null,
        covers: [],
      },
    ])
    expect(readPlanBlock('# Plan\n\nNo block here.')).toBeNull()
    expect(readPlanBlock('```aide-plan\n{ not json\n```')).toBeNull()
    expect(readPlanBlock(`${planMarkdown(steps)}\n${planMarkdown(steps)}`)).toBeNull()
  })

  it('FR29_a_draft_plan_shows_a_preview_of_its_structured_steps', async () => {
    const project = seedPlanning('viewer', (p) => {
      seedArtifact(p, { type: PLAN, content: planMarkdown(steps) })
    })
    await openArtifact(project, PLAN)

    const section = await screen.findByRole('region', { name: 'Steps' })
    const rows = stepRows(/^Preview of the 2 steps in draft v1/)
    expect(rows).toHaveLength(2)
    const [first, second] = rows
    if (!first || !second) {
      throw new Error('missing step rows')
    }
    expect(within(first).getByRole('rowheader')).toHaveTextContent('1')
    expect(first).toHaveTextContent('Set up the schema')
    expect(first).toHaveTextContent('db/schema.sql (create)')
    expect(first).toHaveTextContent('None')
    expect(second).toHaveTextContent('src/app.ts (modify)')
    expect(within(second).getAllByRole('cell').at(2)).toHaveTextContent('1')
    // Drafts have no execution steps yet, so no status column.
    expect(within(section).queryByRole('columnheader', { name: 'Status' })).not.toBeInTheDocument()
  })

  it('FR29_an_unreadable_plan_block_says_there_is_no_preview', async () => {
    const project = seedPlanning('viewer', (p) => {
      seedArtifact(p, { type: PLAN, content: '# Plan\n\n```aide-plan\n{ broken\n```\n' })
    })
    await openArtifact(project, PLAN)

    expect(await screen.findByText(/block of this draft can.t be read, so there is no step preview/)).toBeInTheDocument()
  })

  it('FR30_an_approved_plan_shows_its_execution_steps_with_status', async () => {
    const project = seedPlanning('viewer', (p) => {
      const plan = seedArtifact(p, { type: PLAN, content: planMarkdown(steps), status: 'locked' })
      createExecutionSteps(p, plan, steps)
    })
    await openArtifact(project, PLAN)

    await screen.findByRole('table', { name: '2 execution steps of v1' })
    const rows = stepRows(/^2 execution steps of v1$/)
    expect(rows).toHaveLength(2)
    for (const row of rows) {
      expect(within(row).getByText('Pending')).toBeInTheDocument()
    }
    expect(within(screen.getByRole('table', { name: '2 execution steps of v1' })).getByRole('columnheader', { name: 'Status' })).toBeInTheDocument()
  })

  it('FR30_approving_a_plan_creates_one_pending_step_per_plan_step', async () => {
    const project = seedPlanning('owner', (p) => {
      seedArtifact(p, { type: PLAN, content: planMarkdown(planSteps(3)) })
    })
    const { user } = await openArtifact(project, PLAN)
    await screen.findByRole('table', { name: /^Preview of the 3 steps/ })

    await user.click(screen.getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Approve v1' }))

    await screen.findByRole('table', { name: '3 execution steps of v1' })
    expect(stepRows(/^3 execution steps of v1$/)).toHaveLength(3)
    expect(project.executionSteps.map((s) => s.status)).toEqual(['pending', 'pending', 'pending'])
    expect(requestsTo('POST', '/approve')).toHaveLength(1)
  })

  it('FR30_approving_an_invalid_plan_shows_INVALID_PLAN_with_every_reason', async () => {
    const broken = planMarkdown([
      { number: 1, title: 'First', task: 'Do it.', files: [], dependencies: [] },
      { number: 2, title: 'Second', task: 'Then this.', files: [{ path: 'a.ts', action: 'create' }], dependencies: [3] },
    ])
    const project = seedPlanning('owner', (p) => {
      seedArtifact(p, { type: PLAN, content: broken })
    })
    const { user } = await openArtifact(project, PLAN)
    await screen.findByRole('table', { name: /^Preview of the 2 steps/ })

    await user.click(screen.getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Approve v1' }))

    await waitFor(() => {
      expect(dialog).toHaveTextContent(contextualErrorMessages.decideArtifact.INVALID_PLAN)
    })
    expect(dialog).toHaveTextContent('Step 1 declares no files')
    expect(dialog).toHaveTextContent('Step 2 depends on step 3')
    expect(project.executionSteps).toHaveLength(0)
  })

  it('FR30_protected_declared_files_show_a_protected_badge', async () => {
    const withProtected: MockPlanStep[] = [
      {
        number: 1,
        title: 'CI and env',
        task: 'Wire CI.',
        files: [
          { path: '.github/workflows/ci.yml', action: 'create' },
          { path: 'src/app.ts', action: 'modify' },
        ],
        dependencies: [],
      },
    ]
    const project = seedPlanning('viewer', (p) => {
      const plan = seedArtifact(p, { type: PLAN, content: planMarkdown(withProtected), status: 'locked' })
      createExecutionSteps(p, plan, withProtected)
    })
    await openArtifact(project, PLAN)

    const table = await screen.findByRole('table', { name: '1 execution steps of v1' })
    const items = within(table).getAllByRole('listitem')
    expect(items).toHaveLength(2)
    const [ci, app] = items
    if (!ci || !app) {
      throw new Error('missing file items')
    }
    // Text, not colour only: the badge reads "Protected" next to the path.
    expect(ci).toHaveTextContent('.github/workflows/ci.yml (create) Protected')
    expect(within(app).queryByText('Protected')).not.toBeInTheDocument()
    expect(screen.getByText(/may be changed only by the step that declares them/)).toBeInTheDocument()
  })
})
