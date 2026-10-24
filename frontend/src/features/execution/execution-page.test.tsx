import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../lib/errors'
import type { ProjectRole } from '../../lib/permissions'
import { seedExecution, seedOptimizationPlan, syncExecutionState } from '../../mocks/handlers/execution'
import { seedGate } from '../../mocks/handlers/lifecycle'
import type { StoredExecutionStep, StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { projectEvent } from '../../test/fake-live'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'

const sha = '3f2a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a39'

function seedWithSteps(role: ProjectRole, count: number, setup?: (steps: StoredExecutionStep[], p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    seedExecution(p, count, (steps) => setup?.(steps, p))
  })
}

async function openExecution(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/execution`)
  await screen.findByRole('heading', { level: 2, name: 'Execution' })
  await waitFor(() => {
    expect(screen.queryByText('Loading execution…')).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

const stepItem = (n: number) => screen.getByRole('listitem', { name: new RegExp(`^Step ${String(n)}:`) })
const progress = () => screen.getByRole('region', { name: /Progress/ })

describe('Execution page (FR-31–FR-34, FR-37)', () => {
  it('FR31_lists_steps_in_order_with_status_attempt_and_current_step', async () => {
    const project = seedWithSteps('owner', 3, (steps) => {
      Object.assign(steps[0] ?? {}, {
        status: 'completed',
        attempt: 1,
        commitSha: sha,
        commitUrl: `https://github.com/acme/inventory/commit/${sha}`,
        branchName: 'aide/inventory-tracker-1a2b3c4d',
        startedAt: '2026-09-01T10:00:00Z',
        completedAt: '2026-09-01T10:05:00Z',
        fileManifest: [{ path: 'src/part-1.ts', action: 'create', contentHash: 'sha256:abcdef0123456789' }],
      })
      Object.assign(steps[1] ?? {}, { status: 'in_progress', attempt: 2, startedAt: '2026-09-01T10:06:00Z' })
    })
    await openExecution(project)

    const list = screen.getByRole('list', { name: 'Execution steps' })
    expect(within(list).getAllByRole('heading', { level: 4 }).map((h) => h.textContent)).toEqual([
      'Step 1: Step 1 title',
      'Step 2: Step 2 title',
      'Step 3: Step 3 title',
    ])
    expect(within(stepItem(1)).getByText('Completed', { selector: 'span' })).toBeInTheDocument()
    expect(within(stepItem(1)).getByText('Attempt 1 of 4')).toBeInTheDocument()
    expect(within(stepItem(2)).getByText('In progress')).toBeInTheDocument()
    expect(within(stepItem(2)).getByText('Attempt 2 of 4')).toBeInTheDocument()
    expect(stepItem(2)).toHaveAttribute('aria-current', 'step')
    expect(within(stepItem(3)).getByText('Pending')).toBeInTheDocument()
    expect(within(stepItem(3)).getByText('Not attempted yet')).toBeInTheDocument()

    expect(within(progress()).getByText('Running', { selector: 'span' })).toBeInTheDocument()
    expect(within(progress()).getByText('Running step 2: Step 2 title')).toBeInTheDocument()
    expect(within(progress()).getByText('1 of 3 steps completed')).toBeInTheDocument()

    // FR-34: the API gives a GitHub commit URL, so the short SHA links to it; the manifest lists the files.
    const commit = within(stepItem(1)).getByRole('link', { name: /3f2a9c1/ })
    expect(commit).toHaveAttribute('href', `https://github.com/acme/inventory/commit/${sha}`)
    expect(within(stepItem(1)).getByText('Files written (1 file)')).toBeInTheDocument()
    expect(within(stepItem(1)).getByText('Created')).toBeInTheDocument()
  })

  it('FR34_commit_without_a_commit_url_is_plain_text_with_copy', async () => {
    // A Local repository: the API sends no commitUrl (the client never builds one from the repo URL).
    const project = seedWithSteps('owner', 1, (steps) => {
      Object.assign(steps[0] ?? {}, { status: 'completed', attempt: 1, commitSha: sha, commitUrl: null })
    })
    await openExecution(project)

    expect(within(stepItem(1)).queryByRole('link', { name: /3f2a9c1/ })).not.toBeInTheDocument()
    expect(within(stepItem(1)).getByText('3f2a9c1')).toBeInTheDocument()
    expect(within(stepItem(1)).getByRole('button', { name: `Copy commit SHA ${sha}` })).toBeInTheDocument()
  })

  it('FR32_shows_violations_grouped_by_severity', async () => {
    const project = seedWithSteps('contributor', 2, (steps) => {
      Object.assign(steps[0] ?? {}, {
        status: 'rejected',
        attempt: 2,
        violations: [
          { check: 'scope_creep', code: 'UNDECLARED_FILE', severity: 'error', message: 'Wrote a file the step does not declare.', path: 'src/extra.ts' },
          { check: 'file_limit', code: 'NEAR_LIMIT', severity: 'warning', message: 'Close to the file limit.', path: null },
        ],
      })
    })
    const { user } = await openExecution(project)

    const item = stepItem(1)
    expect(within(item).getByText('Rejected')).toBeInTheDocument()
    await user.click(within(item).getByText('Guardian violations: 1 error, 1 warning'))
    const errors = within(item).getByRole('heading', { name: /^Errors \(1\)/ })
    const warnings = within(item).getByRole('heading', { name: /^Warnings \(1\)/ })
    expect(errors).toBeVisible()
    expect(warnings).toBeVisible()
    expect(within(item).getByText('Wrote a file the step does not declare.')).toBeInTheDocument()
    expect(within(item).getByText('Scope creep')).toBeInTheDocument()
    expect(within(item).getByText('src/extra.ts')).toBeInTheDocument()
    expect(within(item).getByText('Close to the file limit.')).toBeInTheDocument()
    expect(within(item).getByText('File limit')).toBeInTheDocument()
  })

  it('FR33_blocked_step_links_to_its_own_escalation_gate', async () => {
    let gateId = ''
    const project = seedWithSteps('contributor', 2, (steps, p) => {
      Object.assign(steps[0] ?? {}, {
        status: 'blocked',
        attempt: 4,
        violations: [{ check: 'secret_scan', code: 'SECRET_FOUND', severity: 'error', message: 'A secret was found.', path: 'src/part-1.ts' }],
      })
      gateId = seedGate(p, { type: 'escalation', escalationReason: 'step_blocked', executionStepId: steps[0]?.id ?? null, stepNumber: 1 }).id
      // Another step's escalation is listed first; the link must pick this step's own gate.
      seedGate(p, { type: 'escalation', escalationReason: 'step_blocked', executionStepId: 'other-step', stepNumber: 9 })
    })
    await openExecution(project)

    const item = stepItem(1)
    expect(within(item).getByText('Blocked')).toBeInTheDocument()
    expect(within(item).getByText('Attempt 4 of 4')).toBeInTheDocument()
    expect(within(item).getByText(/continue retries the step with fresh attempts/)).toBeInTheDocument()
    const link = await within(item).findByRole('link', { name: "Go to this step's escalation gate" })
    expect(link).toHaveAttribute('href', `/projects/${project.id}#pending-gate-${gateId}`)
    // Violations of a blocked step are open by default.
    expect(within(item).getByText('A secret was found.')).toBeVisible()

    expect(within(progress()).getByText('Blocked', { selector: 'span' })).toBeInTheDocument()
    const start = screen.getByRole('button', { name: 'Start execution' })
    expect(start).toBeDisabled()
    expect(start).toHaveAccessibleDescription(/A step is blocked/)
  })

  it('FR37_pause_then_resume', async () => {
    const project = seedWithSteps('contributor', 2, (steps) => {
      Object.assign(steps[0] ?? {}, { status: 'in_progress', attempt: 1 })
    })
    const { user } = await openExecution(project)

    expect(within(progress()).getByText('Running', { selector: 'span' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Pause execution' }))

    expect(await screen.findByRole('button', { name: 'Resume execution' })).toBeEnabled()
    expect(within(progress()).getByText('Paused', { selector: 'span' })).toBeInTheDocument()
    expect(within(progress()).getByText(/Pausing: step 1 \(Step 1 title\) finishes/)).toBeInTheDocument()
    expect(requestsTo('POST', '/execution/pause')).toHaveLength(1)
    expect(project.executionPaused).toBe(true)

    await user.click(screen.getByRole('button', { name: 'Resume execution' }))
    expect(await screen.findByRole('button', { name: 'Pause execution' })).toBeEnabled()
    expect(requestsTo('POST', '/execution/resume')).toHaveLength(1)
    expect(project.executionPaused).toBe(false)
  })

  it('FR31_start_shows_pending_until_the_live_step_event', async () => {
    const project = seedWithSteps('contributor', 2)
    const { user, live } = await openExecution(project)

    expect(within(progress()).getByText('Not running')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Start execution' }))

    expect(await within(progress()).findByText('Waiting for the next step to start…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pause execution' })).toBeInTheDocument()
    expect(requestsTo('POST', '/execution/start')).toHaveLength(1)

    const first = project.executionSteps[0]
    if (first) {
      Object.assign(first, { status: 'in_progress', attempt: 1, startedAt: '2026-09-01T11:00:00Z' })
    }
    syncExecutionState(project)
    live.latest.emit(projectEvent(project.id, 'step', { type: 'STEP_STARTED' }))

    expect(await within(progress()).findByText('Running step 1: Step 1 title')).toBeInTheDocument()
    expect(within(stepItem(1)).getByText('In progress')).toBeInTheDocument()
  })

  it('FR31_status_comes_from_state_not_the_step_counts', async () => {
    // Between two steps the counts alone look idle; /state's executionStatus says the runner is running.
    const project = seedWithSteps('contributor', 2, (steps, p) => {
      Object.assign(steps[0] ?? {}, { status: 'completed', attempt: 1 })
      p.executionStarted = true
    })
    await openExecution(project)

    expect(within(progress()).getByText('Running', { selector: 'span' })).toBeInTheDocument()
    expect(within(progress()).getByText('Waiting for the next step to start…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pause execution' })).toBeEnabled()
  })

  // rejected: a step waiting for its retry; in_progress: a step interrupted when the runner stopped.
  it.each(['rejected', 'in_progress'] as const)('FR31_start_enabled_with_only_a_rejected_or_interrupted_step (%s)', async (status) => {
    // Idle runner (no queued job) and no pending step: the backend still retries a rejected step first, and the next
    // start continues an interrupted in_progress one, so Start stays available.
    const project = seedWithSteps('contributor', 2, (steps, p) => {
      Object.assign(steps[0] ?? {}, { status: 'completed', attempt: 1 })
      Object.assign(steps[1] ?? {}, { status, attempt: 1 })
      p.executionStarted = false
    })
    const { user } = await openExecution(project)

    expect(within(progress()).getByText('Not running')).toBeInTheDocument()
    const start = screen.getByRole('button', { name: 'Start execution' })
    expect(start).toBeEnabled()
    expect(start).not.toHaveAccessibleDescription()
    await user.click(start)
    expect(await screen.findByRole('button', { name: 'Pause execution' })).toBeInTheDocument()
    expect(requestsTo('POST', '/execution/start')).toHaveLength(1)
  })

  it('FR31_start_disabled_with_a_reason_when_nothing_is_left_to_run', async () => {
    // No pending, rejected or in-progress step is left (here the only step was invalidated).
    const project = seedWithSteps('contributor', 1, (steps, p) => {
      Object.assign(steps[0] ?? {}, { status: 'invalidated' })
      p.executionStarted = false
    })
    await openExecution(project)

    const start = screen.getByRole('button', { name: 'Start execution' })
    expect(start).toBeDisabled()
    expect(start).toHaveAccessibleDescription(/Nothing is left to run: no step is pending, rejected .* or interrupted/)
  })

  it('FR31_lists_the_current_plan_which_can_be_an_optimisation_plan', async () => {
    const project = seedWithSteps('contributor', 3, (steps, p) => {
      for (const step of steps) {
        Object.assign(step, { status: 'completed', attempt: 1 })
      }
      seedOptimizationPlan(p, 2, 2)
      p.executionStarted = false
    })
    await openExecution(project)

    // FR-57: the optimisation plan's review-fix run is the active (expanded) run.
    expect(screen.getByRole('heading', { level: 3, name: /^Optimisation plan v2 · Review-fix run/ })).toBeInTheDocument()
    const list = screen.getByRole('list', { name: 'Execution steps' })
    expect(within(list).getAllByRole('heading', { level: 4 }).map((h) => h.textContent)).toEqual([
      'Step 1: Optimisation 1 title',
      'Step 2: Optimisation 2 title',
    ])
    // The counts in /state cover the same plan.
    expect(within(progress()).getByText('0 of 2 steps completed')).toBeInTheDocument()
  })

  it('FR37_controls_are_disabled_with_a_reason_outside_the_execution_phase', async () => {
    const project = seedWithSteps('owner', 1, (_, p) => {
      p.currentPhase = 'planning'
    })
    await openExecution(project)

    const start = screen.getByRole('button', { name: 'Start execution' })
    expect(start).toBeDisabled()
    expect(start).toHaveAccessibleDescription('Execution runs only in the Execution phase; the project is in Planning.')
  })

  it('FR31_start_refused_by_the_budget_shows_the_error_copy', async () => {
    const project = seedWithSteps('owner', 1, (_, p) => {
      p.tokenBudget = 1000
      p.tokensUsed = 1000
    })
    const { user } = await openExecution(project)

    await user.click(screen.getByRole('button', { name: 'Start execution' }))
    expect(await screen.findByText(contextualErrorMessages.executionControl.BUDGET_EXCEEDED)).toBeInTheDocument()
  })

  it('permissions_viewer_sees_progress_but_no_controls', async () => {
    const project = seedWithSteps('viewer', 2, (steps) => {
      Object.assign(steps[0] ?? {}, { status: 'in_progress', attempt: 1 })
    })
    await openExecution(project)

    expect(screen.getByRole('list', { name: 'Execution steps' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /execution$/ })).not.toBeInTheDocument()
    expect(screen.getByText(/Only Contributors and Owners can start, pause or resume it/)).toBeInTheDocument()
  })

  it('FR30_no_approved_plan_shows_the_empty_state', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'execution'
    })
    await openExecution(project)

    expect(screen.getByText('No execution steps yet')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start execution' })).toBeDisabled()
  })
})
