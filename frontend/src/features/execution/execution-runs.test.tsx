import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedExecution, seedOptimizationPlan, seedStepAttempts, syncExecutionState } from '../../mocks/handlers/execution'
import type { StoredExecutionStep, StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { projectEvent } from '../../test/fake-live'
import { renderRoute } from '../../test/render'

async function openExecution(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/execution`)
  await screen.findByRole('heading', { level: 2, name: 'Execution' })
  await screen.findByRole('list', { name: 'Runs' })
  return { ...utils, user: userEvent.setup() }
}

const complete = (steps: StoredExecutionStep[]) => {
  for (const s of steps) {
    Object.assign(s, { status: 'completed', attempt: 1, startedAt: '2026-09-01T10:00:00Z', completedAt: '2026-09-01T10:05:00Z' })
  }
}

const runGroup = (name: RegExp) => screen.getByRole('listitem', { name })

/** FR-57 example: a development run of 8 steps and two review cycles with 3 and 2 fix steps. */
function seedThreeRuns() {
  const steps: Record<string, StoredExecutionStep[]> = {}
  const project = seedProjectAs('owner', (p) => {
    steps.dev = seedExecution(p, 8, (s) => {
      complete(s)
      Object.assign(s[0] ?? {}, { attempt: 2, agentKey: 'backend-engineer' })
    })
    steps.fix1 = seedOptimizationPlan(p, 3, 1, complete, 1)
    steps.fix2 = seedOptimizationPlan(p, 2, 2, undefined, 2)
    const [dev1] = steps.dev
    const [fix11] = steps.fix1
    if (!dev1 || !fix11) {
      throw new Error('seed')
    }
    seedStepAttempts(p, dev1, [
      {
        outcome: 'rejected',
        violations: [
          { check: 'scope_creep', code: 'SCOPE_CREEP', severity: 'error', message: 'Wrote an undeclared file.', path: 'src/extra.ts' },
        ],
        warnings: [{ check: 'file_limit', code: 'FILE_LIMIT', severity: 'warning', message: 'Close to the file limit.', path: null }],
      },
      {
        outcome: 'accepted',
        producedFiles: [{ path: 'src/part-1.ts', action: 'create', contentHash: 'sha256:aaa' }],
        toolCalls: [{ tool: 'read_file', arguments: { path: 'src/index.ts' }, bytes: 2048, truncated: false, error: null }],
      },
    ])
    seedStepAttempts(p, fix11, [{ outcome: 'accepted' }])
    p.executionStarted = false
    syncExecutionState(p)
  })
  return { project, steps }
}

describe('Execution runs (FR-57)', () => {
  it('FR57_lists_three_runs_of_8_3_and_2_steps_each_with_its_attempt_history', async () => {
    const { project } = seedThreeRuns()
    const { user } = await openExecution(project)

    const runs = screen.getByRole('list', { name: 'Runs' })
    expect(within(runs).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual([
      'Implementation plan v1 · Development run',
      'Optimisation plan v1 · Review-fix run, cycle 1',
      'Optimisation plan v2 · Review-fix run, cycle 2',
    ])
    expect(runGroup(/^Implementation plan v1/)).toHaveTextContent('8 / 8 completed')
    expect(runGroup(/^Optimisation plan v1/)).toHaveTextContent('3 / 3 completed')
    expect(runGroup(/^Optimisation plan v2/)).toHaveTextContent('0 / 2 completed')

    // The active run (the current plan) is expanded with its live, detailed steps.
    const active = runGroup(/^Optimisation plan v2/)
    expect(within(active).getByText('Active run')).toBeInTheDocument()
    expect(within(within(active).getByRole('list', { name: 'Execution steps' })).getAllByRole('heading', { level: 4 })).toHaveLength(2)

    // The others are collapsed until opened.
    const dev = runGroup(/^Implementation plan v1/)
    const devToggle = within(dev).getByRole('button', { name: /^Implementation plan v1/ })
    expect(devToggle).toHaveAttribute('aria-expanded', 'false')
    await user.click(devToggle)
    expect(devToggle).toHaveAttribute('aria-expanded', 'true')
    const devSteps = within(dev).getByRole('list', { name: 'Steps of Implementation plan v1' })
    expect(within(devSteps).getAllByRole('heading', { level: 4 })).toHaveLength(8)

    const fix1 = runGroup(/^Optimisation plan v1/)
    await user.click(within(fix1).getByRole('button', { name: /^Optimisation plan v1/ }))
    expect(within(within(fix1).getByRole('list', { name: 'Steps of Optimisation plan v1' })).getAllByRole('heading', { level: 4 })).toHaveLength(3)

    // Attempt history of step 1 of the development run: model, tokens, outcome, Guardian findings and tool calls.
    const step1 = within(devSteps).getByRole('listitem', { name: 'Step 1: Step 1 title' })
    expect(within(step1).getByText('backend-engineer')).toBeInTheDocument()
    await user.click(within(step1).getByRole('button', { name: 'Show attempt history (2) of step 1' }))
    const attempts = await within(step1).findByRole('list', { name: 'Attempts of step 1' })
    const first = within(attempts).getByRole('listitem', { name: 'Attempt 1' })
    expect(first).toHaveTextContent('Rejected by the Guardian')
    expect(first).toHaveTextContent('Wrote an undeclared file.')
    expect(first).toHaveTextContent('Close to the file limit.')
    expect(first).toHaveTextContent('claude-opus-5-5')
    expect(first).toHaveTextContent('1,200 / 3,400')
    const second = within(attempts).getByRole('listitem', { name: 'Attempt 2' })
    expect(second).toHaveTextContent('Accepted')
    const tools = within(second).getByRole('table', { name: 'Repository tool calls of attempt 2' })
    expect(tools).toHaveTextContent('read_file')
    expect(tools).toHaveTextContent('{"path":"src/index.ts"}')

    // The review-fix run's steps have their history too.
    const fixStep = within(fix1).getByRole('listitem', { name: 'Step 1: Optimisation 1 title' })
    await user.click(within(fixStep).getByRole('button', { name: 'Show attempt history (1) of step 1' }))
    expect(within(await within(fixStep).findByRole('list', { name: 'Attempts of step 1' })).getAllByRole('listitem')).toHaveLength(1)
  })

  it('FR129_FR121_step_planned_for_a_retired_preset_agent_shows_the_specialist_it_ran_as', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedExecution(p, 2, (s) => {
        complete(s)
        Object.assign(s[0] ?? {}, { agentKey: 'data-engineer', legacyAgentKey: 'db-engineer' })
        Object.assign(s[1] ?? {}, { agentKey: 'frontend-engineer', legacyAgentKey: null })
      })
      seedOptimizationPlan(p, 1, 1, undefined, 1)
      syncExecutionState(p)
    })
    const { user } = await openExecution(project)

    const dev = runGroup(/^Implementation plan v1/)
    await user.click(within(dev).getByRole('button', { name: /^Implementation plan v1/ }))
    const devSteps = within(dev).getByRole('list', { name: 'Steps of Implementation plan v1' })
    expect(within(devSteps).getByRole('listitem', { name: 'Step 1: Step 1 title' })).toHaveTextContent('data-engineer (was db-engineer)')
    const step2 = within(devSteps).getByRole('listitem', { name: 'Step 2: Step 2 title' })
    expect(step2).toHaveTextContent('frontend-engineer')
    expect(step2).not.toHaveTextContent('(was')
  })

  it('FR57_active_run_updates_live', async () => {
    let steps: StoredExecutionStep[] = []
    const project = seedProjectAs('owner', (p) => {
      steps = seedExecution(p, 2)
    })
    const { live } = await openExecution(project)
    expect(runGroup(/^Implementation plan v1/)).toHaveTextContent('0 / 2 completed')

    Object.assign(steps[0] ?? {}, { status: 'completed', attempt: 1, commitSha: 'abcdef1234567890' })
    live.latest.emit(projectEvent(project.id, 'step', { type: 'STEP_COMPLETED' }))
    await waitFor(() => {
      expect(runGroup(/^Implementation plan v1/)).toHaveTextContent('1 / 2 completed')
    })
  })

  it('FR57_runs_of_invalidated_plans_are_listed_under_superseded_with_the_reason', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedExecution(p, 2)
      const plan = p.artifacts.find((a) => a.type === 'implementation_plan')
      if (!plan) {
        throw new Error('seed')
      }
      plan.status = 'invalidated'
      p.supersededReasons = { [plan.id]: 'Specification v3 was approved, which invalidated this plan.' }
      seedOptimizationPlan(p, 1, 1, undefined, 1)
    })
    const { user } = await openExecution(project)

    const superseded = screen.getByRole('region', { name: 'Superseded' })
    const group = within(superseded).getByRole('listitem', { name: /^Implementation plan v1/ })
    expect(group).toHaveTextContent('Superseded: Specification v3 was approved, which invalidated this plan.')
    const toggle = within(group).getByRole('button', { name: /^Implementation plan v1/ })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await user.click(toggle)
    expect(within(group).getByRole('list', { name: 'Steps of Implementation plan v1' })).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Runs' })).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual([
      'Optimisation plan v1 · Review-fix run, cycle 1',
    ])
  })
})
