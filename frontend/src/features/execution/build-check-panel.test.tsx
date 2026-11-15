import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { buildCheck, buildCheckSource, seedBuildCheck, seedExecution, syncExecutionState } from '../../mocks/handlers/execution'
import type { StoredExecutionStep, StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { axeViolations } from '../../test/axe'
import { projectEvent } from '../../test/fake-live'
import { renderRoute } from '../../test/render'

type BuildCheck = Schemas['BuildCheckDto']

const moduleNotFound = [
  '15:02:11.204 Running "npm run build"',
  "15:02:19.870 Failed to compile.",
  "15:02:19.871 ./app/page.tsx",
  "15:02:19.871 Module not found: Can't resolve '@/lib/db'",
].join('\n')

const vercelFailure = {
  failureId: 'failure:preview:vercel',
  source: 'preview',
  message: 'Vercel build failed',
  excerpt: moduleNotFound,
} satisfies Schemas['BuildCheckFailureDto']

/** A project in Execution with `count` completed plan steps, `maxBuildFixAttempts` 5 and the checks `setup` seeds. */
function seedWithChecks(
  setup: (p: StoredProject, steps: StoredExecutionStep[]) => void,
  role: 'owner' | 'viewer' = 'owner',
  count = 3,
) {
  return seedProjectAs(role, (p) => {
    p.maxBuildFixAttempts = 5
    const steps = seedExecution(p, count, (s) => {
      for (const step of s) {
        Object.assign(step, { status: 'completed', attempt: 1 })
      }
    })
    setup(p, steps)
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

/** Waits for the join refetch to settle, so only the live message changes the panel afterwards. */
async function settled(utils: Awaited<ReturnType<typeof openExecution>>, project: StoredProject) {
  await waitFor(() => {
    expect(utils.live.latest.joined(project.id)).toBe(1)
    expect(utils.queryClient.isFetching()).toBe(0)
  })
}

/** The item at `index` (negative from the end), failing the test when there is none. */
function only<T>(items: T[], index: number): T {
  const item = items.at(index)
  if (item === undefined) {
    throw new Error(`no item ${String(index)}`)
  }
  return item
}

const panel = () => screen.getByRole('region', { name: 'Build check' })
const findPanel = () => screen.findByRole('region', { name: 'Build check' })
const summary = () => within(panel()).getByTestId('build-check-summary')
const buildCards = () => within(panel()).getAllByTestId('build-check-build-card')
const fixCards = () => within(panel()).queryAllByTestId('build-check-fix-card')
const sourceRow = (name: string) => {
  // The newest card's row: the timeline reads oldest first.
  const row = within(panel())
    .getAllByTestId('build-check-source')
    .filter((r) => r.dataset.sourceName === name)
    .at(-1)
  if (!row) {
    throw new Error(`no source ${name}`)
  }
  return row
}

/** Attempt 0 failed (fix steps 4, 5), attempt 1 failed (fix step 6), attempt 2 waits for Vercel with CI partly done. */
function seedAttemptTwoWaiting(p: StoredProject) {
  seedBuildCheck(p, {
    attempt: 0,
    status: 'failed',
    headSha: 'aaaaaaa1111111111111111111111111111111111',
    startedAt: '2026-09-01T11:00:00Z',
    sources: [buildCheckSource({ name: 'Vercel preview', kind: 'preview', conclusion: 'failure', url: 'https://vercel.com/acme/inventory/a0' })],
    failures: [vercelFailure],
    fixStepNumbers: [4, 5],
    completedAt: '2026-09-01T11:05:00Z',
  })
  seedBuildCheck(p, {
    attempt: 1,
    status: 'failed',
    headSha: 'bbbbbbb2222222222222222222222222222222222',
    startedAt: '2026-09-01T11:10:00Z',
    sources: [buildCheckSource({ name: 'Vercel preview', kind: 'preview', conclusion: 'failure', url: 'https://vercel.com/acme/inventory/a1' })],
    failures: [
      {
        failureId: 'failure:preview:vercel',
        source: 'preview',
        message: 'Vercel build failed',
        excerpt: "./app/page.tsx:12:7\nType error: Property 'id' does not exist on type 'Item'.",
      },
    ],
    fixStepNumbers: [6],
    completedAt: '2026-09-01T11:20:00Z',
  })
  return seedBuildCheck(p, {
    attempt: 2,
    status: 'waiting',
    headSha: 'ccccccc3333333333333333333333333333333333',
    startedAt: '2026-09-01T11:25:00Z',
    sources: [
      buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'queued' }),
      buildCheckSource({ name: 'build', kind: 'check', conclusion: 'success', url: 'https://github.com/acme/inventory/actions/runs/1', status: 'completed' }),
      buildCheckSource({ name: 'lint', kind: 'check', conclusion: 'success', url: 'https://github.com/acme/inventory/actions/runs/2', status: 'completed' }),
    ],
  })
}

describe('Build check timeline (FR-259, D-0.6.1-23)', () => {
  it('FR259_attempt_2_of_5_waiting_for_vercel_shows_ci_so_far_and_the_previous_excerpt', async () => {
    const project = seedWithChecks((p) => {
      p.state.review.cycle = 1
      seedAttemptTwoWaiting(p)
    })
    await openExecution(project)

    const region = await findPanel()
    expect(summary()).toHaveTextContent('Attempt 2 of 5 · Waiting for Vercel preview')

    // build → fix → build → fix → build, oldest first; the newest build card is the current one, at the bottom.
    const timeline = within(region).getByRole('list', { name: 'Build check timeline' })
    const items = Array.from(timeline.children)
    expect(items.map((i) => i.getAttribute('aria-label'))).toEqual([
      'Build: Initial check',
      'Build fix · attempt 1',
      'Build: Attempt 1 of 5',
      'Build fix · attempt 2',
      'Build: Attempt 2 of 5',
    ])
    expect(items.at(-1)).toHaveAttribute('aria-current', 'step')
    expect(items.at(-1)).toHaveTextContent('(current)')

    // The sources so far: the preview queued, the CI checks `build` and `lint` passed (linked).
    const sources = within(region).getByRole('list', { name: 'Sources of the attempt 2 of 5' })
    const build = within(sources).getByRole('link', { name: /^build/ })
    expect(build).toHaveAttribute('href', 'https://github.com/acme/inventory/actions/runs/1')
    expect(within(sources).getAllByText('Passed')).toHaveLength(2)
    expect(within(sourceRow('Vercel preview')).getByText('Queued')).toBeInTheDocument()

    // The previous attempt's excerpt is open while the next check waits; the initial check stays collapsed.
    const excerpts = within(region).getAllByTestId('build-check-excerpt')
    const previous = excerpts.find((e) => e.textContent.includes("Property 'id' does not exist"))
    expect(previous).toBeVisible()
    expect(previous).toHaveAttribute('tabindex', '0')
    const first = excerpts.find((e) => e.textContent.includes("Can't resolve '@/lib/db'"))
    expect(first).not.toBeVisible()
  })

  it('FR259_build_card_while_vercel_is_building_then_failed_with_the_excerpt', async () => {
    let waiting: BuildCheck | undefined
    const project = seedWithChecks((p) => {
      waiting = seedBuildCheck(p, {
        status: 'waiting',
        sources: [buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'building', url: 'https://vercel.com/acme/inventory/b0' })],
      })
    })
    const utils = await openExecution(project)
    await findPanel()
    await settled(utils, project)
    if (!waiting) {
      throw new Error('not seeded')
    }

    const row = sourceRow('Vercel preview')
    expect(within(row).getByText('Building…')).toBeInTheDocument()
    // The spinner only spins when motion is allowed.
    expect(row.querySelector('svg')).toHaveClass('motion-safe:animate-spin')
    expect(summary()).toHaveTextContent('Initial check · Waiting for Vercel preview')
    expect(fixCards()).toHaveLength(0)

    utils.live.latest.emitBuildCheck({
      projectId: project.id,
      check: {
        ...waiting,
        status: 'failed',
        sources: [{ ...only(waiting.sources, 0), status: 'error', conclusion: 'failure' }],
        failures: [vercelFailure],
        completedAt: '2026-09-01T11:04:30Z',
        fixPlanning: 'planning',
      },
    })

    await waitFor(() => {
      expect(summary()).toHaveTextContent('Initial check · Build failed: planning a fix')
    })
    expect(within(sourceRow('Vercel preview')).getByText('Failed')).toBeInTheDocument()
    // The current failed attempt shows its excerpt open, and the times with the duration.
    const excerpt = within(panel()).getByTestId('build-check-excerpt')
    expect(excerpt).toBeVisible()
    expect(excerpt).toHaveTextContent("Module not found: Can't resolve '@/lib/db'")
    expect(within(only(buildCards(), 0)).getByText('4 min 30 s')).toBeInTheDocument()
    // The fix card follows: planning.
    expect(fixCards()).toHaveLength(1)
    expect(fixCards()[0]).toHaveTextContent('Build fix · attempt 1 · Planning a fix…')
    expect(fixCards()[0]).toHaveAttribute('aria-current', 'step')
  })

  it('FR259_fix_card_planning_then_steps_running_and_completed_then_the_next_build', async () => {
    let failed: BuildCheck | undefined
    let fix: StoredExecutionStep | undefined
    const project = seedWithChecks(
      (p, steps) => {
        fix = steps[3]
        Object.assign(fix ?? {}, {
          origin: 'build_fix',
          buildCheckAttempt: 0,
          title: 'Add the db module',
          status: 'pending',
          attempt: 0,
        })
        syncExecutionState(p)
        failed = seedBuildCheck(p, {
          status: 'failed',
          sources: [buildCheckSource({ name: 'Vercel preview', kind: 'preview', conclusion: 'failure', status: 'error' })],
          failures: [vercelFailure],
          completedAt: '2026-09-01T11:05:00Z',
        })
        // The fix step names its check (D-0.6.1-23: joined by `buildCheckId`).
        Object.assign(fix ?? {}, { buildCheckId: failed.id })
      },
      'owner',
      4,
    )
    const utils = await openExecution(project)
    await findPanel()
    await settled(utils, project)
    if (!failed || !fix) {
      throw new Error('not seeded')
    }
    expect(fixCards()[0]).toHaveTextContent('Planning a fix…')

    // The fix steps were stored (the API answers so from now on, as the step events refetch the checks too).
    Object.assign(failed, { fixStepNumbers: [4], fixPlanning: 'planned' })
    utils.live.latest.emitBuildCheck({ projectId: project.id, check: { ...failed } })
    const steps = await within(panel()).findByRole('list', { name: 'Steps of build fix · attempt 1' })
    expect(steps).toHaveTextContent('Step 4: Add the db module')
    expect(within(steps).getByText('Pending')).toBeInTheDocument()
    expect(summary()).toHaveTextContent('Build failed: fix step 4 planned')

    // The step runs: the step event refreshes the steps, and the fix card follows.
    Object.assign(fix, { status: 'in_progress', attempt: 1, startedAt: '2026-09-01T11:06:00Z' })
    syncExecutionState(project)
    utils.live.latest.emit(projectEvent(project.id, 'step', { type: 'STEP_STARTED' }))
    expect(await within(steps).findByText('In progress')).toBeInTheDocument()

    Object.assign(fix, { status: 'completed', completedAt: '2026-09-01T11:08:00Z' })
    syncExecutionState(project)
    utils.live.latest.emit(projectEvent(project.id, 'step', { type: 'STEP_COMPLETED' }))
    expect(await within(steps).findByText('Completed')).toBeInTheDocument()
    expect(fixCards()[0]).toHaveTextContent('1 of 1 completed')
    expect(fixCards()[0]).toHaveTextContent('the next build starts')

    // The next build starts and becomes the current card, after the fix.
    utils.live.latest.emitBuildCheck({
      projectId: project.id,
      check: buildCheck(project, {
        attempt: 1,
        status: 'waiting',
        startedAt: '2026-09-01T11:10:00Z',
        sources: [buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'queued' })],
      }),
    })
    await waitFor(() => {
      expect(buildCards()).toHaveLength(2)
    })
    expect(summary()).toHaveTextContent('Attempt 1 of 5 · Waiting for Vercel preview')
    const items = Array.from(within(panel()).getByRole('list', { name: 'Build check timeline' }).children)
    expect(items.map((i) => i.getAttribute('aria-label'))).toEqual(['Build: Initial check', 'Build fix · attempt 1', 'Build: Attempt 1 of 5'])
    expect(items[2]).toHaveAttribute('aria-current', 'step')
    expect(items[1]).not.toHaveAttribute('aria-current')
  })

  it('FR259_fix_card_says_when_no_valid_fix_could_be_planned', async () => {
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, {
        status: 'exhausted',
        endReason: 'fix_plan_invalid',
        failures: [vercelFailure],
        completedAt: '2026-09-01T11:05:00Z',
      })
    })
    await openExecution(project)
    await findPanel()

    expect(fixCards()[0]).toHaveTextContent("Couldn't plan a valid fix")
  })

  it('FR259_the_panel_has_no_axe_violations', async () => {
    const project = seedWithChecks((p) => {
      seedAttemptTwoWaiting(p)
      seedBuildCheck(p, {
        attempt: 3,
        status: 'waiting',
        startedAt: '2026-09-01T11:40:00Z',
        sources: [
          buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'building' }),
          buildCheckSource({
            name: 'branch rules',
            kind: 'check',
            conclusion: 'unavailable',
            message: "The branch rules couldn't be read (403), so no check is known to be required",
          }),
          buildCheckSource({ name: 'ci', kind: 'check', status: 'in_progress', informational: true, message: 'Shown for information: not waited for' }),
        ],
      })
    })
    await openExecution(project)

    expect(await axeViolations(await findPanel())).toEqual([])
  })

  it('FR259_the_panel_sits_above_the_step_list', async () => {
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, { status: 'waiting' })
    })
    await openExecution(project)

    const region = await findPanel()
    const steps = await screen.findByRole('list', { name: 'Execution steps' })
    expect(region.compareDocumentPosition(steps) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    // Attempt 0 checks the plan's own commit, before any fix; with no source known yet the copy says what it waits for.
    expect(summary()).toHaveTextContent('Initial check · Waiting for the preview build and CI checks')
  })

  it.each<[BuildCheck['status'], Partial<BuildCheck>, string, string]>([
    ['passed', {}, 'Passed', 'Attempt 2 of 5 · Build passed, review started'],
    ['failed', { fixStepNumbers: [7, 8] }, 'Failed', 'Attempt 2 of 5 · Build failed: fix steps 7 and 8 planned'],
    ['failed', {}, 'Failed', 'Attempt 2 of 5 · Build failed: planning a fix'],
    ['skipped', {}, 'Skipped', 'Attempt 2 of 5 · No preview target or required checks'],
    [
      'skipped',
      { sources: [buildCheckSource({ name: 'head checks', kind: 'check', conclusion: 'unavailable', message: "The checks couldn't be read within 15 minutes" })] },
      'Skipped',
      "Attempt 2 of 5 · No preview target, and the CI checks couldn't be read",
    ],
    ['cancelled', { endReason: 'cancelled_by_change' }, 'Cancelled', 'Attempt 2 of 5 · Cancelled: the plan or version changed, or execution was stopped'],
    ['cancelled', {}, 'Cancelled', 'Attempt 2 of 5 · Cancelled: the version changed or execution was stopped'],
  ])('FR259_status_copy_%s', async (status, extra, badge, text) => {
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, { attempt: 2, status, completedAt: '2026-09-01T12:00:00Z', ...extra })
    })
    await openExecution(project)

    const region = await findPanel()
    expect(within(region).getAllByText(badge, { selector: 'span' }).length).toBeGreaterThan(0)
    expect(summary()).toHaveTextContent(text)
  })

  it.each<[NonNullable<BuildCheck['endReason']>, string]>([
    ['fix_plan_invalid', "Tevrin couldn't plan a valid fix for the build failures. Review will include the failures."],
    ['escalation_continue', 'Fix step blocked; you chose to continue to review. Review will include the failures.'],
    ['job_failed', 'The build check stopped because of an error. Review will include any failures.'],
  ])('FR258_FR259_exhausted_wording_for_%s', async (endReason, text) => {
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, { attempt: 0, status: 'exhausted', endReason, completedAt: '2026-09-01T12:00:00Z' })
    })
    await openExecution(project)

    await findPanel()
    expect(summary()).toHaveTextContent(`Initial check · ${text}`)
    expect(panel()).not.toHaveTextContent('0 fix attempts')
  })

  it('FR258_FR259_exhausted_attempts_used_counts_the_fixes_actually_planned', async () => {
    const project = seedWithChecks((p) => {
      p.maxBuildFixAttempts = 2
      seedBuildCheck(p, { attempt: 0, status: 'failed', fixStepNumbers: [4], startedAt: '2026-09-01T11:00:00Z' })
      // A cancelled check (execution stopped) planned no fix: it doesn't count as a fix attempt.
      seedBuildCheck(p, { attempt: 1, status: 'cancelled', startedAt: '2026-09-01T11:10:00Z' })
      seedBuildCheck(p, { attempt: 1, status: 'failed', fixStepNumbers: [5], startedAt: '2026-09-01T11:20:00Z' })
      seedBuildCheck(p, {
        attempt: 2,
        startedAt: '2026-09-01T11:30:00Z',
        status: 'exhausted',
        endReason: 'attempts_used',
        sources: [buildCheckSource({ name: 'Vercel preview', kind: 'preview', conclusion: 'failure' })],
        failures: [vercelFailure],
        completedAt: '2026-09-01T12:00:00Z',
      })
    })
    await openExecution(project)

    await findPanel()
    expect(summary()).toHaveTextContent(
      'Attempt 2 of 2 · Build still failing after 2 fix attempts. Review will include the failures.',
    )
    // The current attempt's failure shows its excerpt in the scrolling evidence block.
    const excerpt = within(only(buildCards(), -1)).getByTestId('build-check-excerpt')
    expect(excerpt).toBeVisible()
    expect(excerpt).toHaveAccessibleName('Log excerpt for failure:preview:vercel')
    expect(excerpt).toHaveClass('overflow-auto')
  })

  it('FR258_FR259_never_says_after_0_fix_attempts', async () => {
    const project = seedWithChecks((p) => {
      // A legacy row: exhausted with no end reason and no fix planned.
      seedBuildCheck(p, { attempt: 0, status: 'exhausted', completedAt: '2026-09-01T12:00:00Z' })
    })
    await openExecution(project)

    await findPanel()
    expect(summary()).toHaveTextContent('Initial check · Build still failing. Review will include the failures.')
    expect(panel()).not.toHaveTextContent('0 fix attempts')
  })

  it('FR259_D_0_6_1_23_unavailable_branch_rules_and_informational_checks', async () => {
    const message = "The branch rules couldn't be read (403 Forbidden), so no check is known to be required; the build check waits for the preview only"
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, {
        status: 'waiting',
        sources: [
          buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'building' }),
          buildCheckSource({ name: 'branch rules', kind: 'check', conclusion: 'unavailable', message }),
          buildCheckSource({
            name: 'ci',
            kind: 'check',
            status: 'in_progress',
            url: 'https://github.com/acme/inventory/actions/runs/9',
            informational: true,
            message: "Shown for information: the branch rules couldn't be read, so this check isn't waited for",
          }),
          buildCheckSource({ name: 'lookalike', kind: 'check', status: 'queued', message: 'Shown for information, but counted' }),
        ],
      })
    })
    await openExecution(project)
    await findPanel()

    const rules = sourceRow('branch rules')
    expect(within(rules).getByText('Unavailable')).toBeInTheDocument()
    expect(rules).toHaveTextContent(message)
    const ci = sourceRow('ci')
    expect(within(ci).getByText('Running…')).toBeInTheDocument()
    expect(within(ci).getByText('Not counted')).toBeInTheDocument()
    expect(within(sourceRow('Vercel preview')).queryByText('Not counted')).not.toBeInTheDocument()
    // Only the `informational` flag marks a check, never its message.
    expect(within(sourceRow('lookalike')).queryByText('Not counted')).not.toBeInTheDocument()
    // The informational check isn't waited for; a counted one is.
    expect(summary()).toHaveTextContent('Initial check · Waiting for Vercel preview and lookalike')
  })

  it('FR259_live_update_moves_a_source_from_building_to_error', async () => {
    let waiting: BuildCheck | undefined
    const project = seedWithChecks((p) => {
      waiting = seedBuildCheck(p, {
        status: 'waiting',
        sources: [
          buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'building' }),
          buildCheckSource({ name: 'build', kind: 'check', status: 'queued' }),
        ],
      })
    })
    const utils = await openExecution(project)
    await findPanel()
    await settled(utils, project)
    if (!waiting) {
      throw new Error('not seeded')
    }
    expect(within(sourceRow('Vercel preview')).getByText('Building…')).toBeInTheDocument()
    expect(within(sourceRow('build')).getByText('Queued')).toBeInTheDocument()

    // Still waiting (the CI check runs on), but the preview ended in an error.
    utils.live.latest.emitBuildCheck({
      projectId: project.id,
      check: {
        ...waiting,
        sources: [
          buildCheckSource({ name: 'Vercel preview', kind: 'preview', status: 'error', conclusion: 'failure' }),
          buildCheckSource({ name: 'build', kind: 'check', status: 'in_progress' }),
        ],
      },
    })
    await waitFor(() => {
      expect(within(sourceRow('Vercel preview')).getByText('Failed')).toBeInTheDocument()
    })
    expect(within(sourceRow('build')).getByText('Running…')).toBeInTheDocument()
    expect(summary()).toHaveTextContent('Initial check · Waiting for build')
    // The summary sits in a polite live region.
    expect(summary().closest('[aria-live]')).toHaveAttribute('aria-live', 'polite')
  })

  it('FR259_after_a_rollback_the_newer_initial_check_stays_current_on_a_live_update', async () => {
    let superseded: BuildCheck | undefined
    const project = seedWithChecks((p) => {
      // Attempt 2 of the cycle started first; a rollback superseded it, and a new initial check started later.
      superseded = seedBuildCheck(p, { attempt: 2, status: 'cancelled', startedAt: '2026-09-01T11:00:00Z', completedAt: '2026-09-01T11:05:00Z' })
      seedBuildCheck(p, { attempt: 0, status: 'waiting', startedAt: '2026-09-01T12:00:00Z' })
    })
    const utils = await openExecution(project)
    await findPanel()
    await settled(utils, project)
    expect(summary()).toHaveTextContent(/^Initial check · Waiting/)
    if (!superseded) {
      throw new Error('not seeded')
    }

    // A live update of the older check keeps it earlier in the timeline: newest is by start time.
    utils.live.latest.emitBuildCheck({
      projectId: project.id,
      check: { ...superseded, endReason: 'cancelled_by_change', completedAt: '2026-09-01T11:06:00Z' },
    })
    await waitFor(() => {
      expect(buildCards()[0]).toHaveAttribute('aria-label', 'Build: Attempt 2 of 5')
    })
    expect(buildCards()[1]).toHaveAttribute('aria-current', 'step')
    expect(summary()).toHaveTextContent(/^Initial check · Waiting/)
  })

  it('FR259_hidden_when_the_cycle_has_no_build_check', async () => {
    const project = seedWithChecks((p) => {
      // A check of an earlier cycle only: the current cycle (2) has none yet.
      p.state.review.cycle = 2
      seedBuildCheck(p, { reviewCycle: 1, status: 'passed' })
    })
    await openExecution(project)

    await screen.findByRole('list', { name: 'Execution steps' })
    await waitFor(() => {
      expect(screen.queryByText('Loading execution runs…')).not.toBeInTheDocument()
    })
    expect(screen.queryByRole('region', { name: 'Build check' })).not.toBeInTheDocument()
  })

  it('FR259_after_execution_the_newest_check_stays_visible', async () => {
    // The check ran in cycle 0 and passed; review then started cycle 1, so the project is in Review.
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, { reviewCycle: 0, attempt: 1, status: 'passed', completedAt: '2026-09-01T12:00:00Z' })
      p.currentPhase = 'review'
      p.status = 'reviewing'
      p.state.review.cycle = 1
    })
    await openExecution(project)

    await findPanel()
    expect(summary()).toHaveTextContent('Attempt 1 of 5 · Build passed, review started')
    expect(within(panel()).getByText('Before review cycle 1')).toBeInTheDocument()
    expect(within(only(buildCards(), 0)).getByText('9b1c2d3')).toBeInTheDocument()
  })

  it('FR259_viewers_see_the_panel', async () => {
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, { attempt: 1, status: 'passed' })
    }, 'viewer')
    await openExecution(project)

    expect(await findPanel()).toBeInTheDocument()
  })

  it('FR259_live_buildCheckUpdated_updates_the_panel', async () => {
    let waiting: BuildCheck | undefined
    const project = seedWithChecks((p) => {
      p.state.review.cycle = 1
      waiting = seedAttemptTwoWaiting(p)
    })
    const utils = await openExecution(project)
    await findPanel()
    await settled(utils, project)
    if (!waiting) {
      throw new Error('not seeded')
    }

    // Only the live message carries the new state (the mock API still answers "waiting").
    utils.live.latest.emitBuildCheck({
      projectId: project.id,
      check: {
        ...waiting,
        status: 'passed',
        sources: waiting.sources.map((s) => ({ ...s, conclusion: 'success' })),
        completedAt: '2026-09-01T11:40:00Z',
      },
    })
    expect(await within(panel()).findByText('Attempt 2 of 5 · Build passed, review started')).toBeInTheDocument()

    // A message for another project is ignored.
    utils.live.latest.emitBuildCheck({ projectId: 'other', check: { ...waiting, status: 'cancelled' } })
    expect(summary()).toHaveTextContent('Build passed, review started')

    // A new attempt arrives live and becomes the current one.
    utils.live.latest.emitBuildCheck({
      projectId: project.id,
      check: buildCheck(project, { reviewCycle: 1, attempt: 3, status: 'waiting', sources: [], startedAt: '2026-09-01T11:45:00Z' }),
    })
    expect(await within(panel()).findByText(/^Attempt 3 of 5 ·/)).toBeInTheDocument()
    expect(buildCards()).toHaveLength(4)
  })
})

describe('Build fix badge (FR-259)', () => {
  it('FR259_build_fix_steps_carry_the_build_fix_attempt_badge', async () => {
    const project = seedWithChecks((p, steps) => {
      // Fix 2 is planned from attempt 1's failure, and attempt 2 checks it.
      const check = seedBuildCheck(p, { attempt: 1, status: 'failed', fixStepNumbers: [3] })
      Object.assign(steps[1] ?? {}, { origin: 'build_fix', buildCheckAttempt: 0, buildCheckId: 'an-older-check', title: 'Add the db module' })
      Object.assign(steps[2] ?? {}, { origin: 'build_fix', buildCheckAttempt: 1, buildCheckId: check.id, title: 'Fix the missing db module' })
    })
    await openExecution(project)

    const list = await screen.findByRole('list', { name: 'Execution steps' })
    const fix = within(list).getByRole('listitem', { name: /^Step 3:/ })
    expect(within(fix).getByTestId('build-fix-badge')).toHaveTextContent('Build fix · attempt 2')
    // The first fix, planned from the initial check (attempt 0), is fix 1.
    const firstFix = within(list).getByRole('listitem', { name: /^Step 2:/ })
    expect(within(firstFix).getByTestId('build-fix-badge')).toHaveTextContent('Build fix · attempt 1')
    const plan = within(list).getByRole('listitem', { name: /^Step 1:/ })
    expect(within(plan).queryByTestId('build-fix-badge')).not.toBeInTheDocument()

    // The build check's fix card joins the same step, with its status.
    const fixCard = only(await within(await findPanel()).findAllByTestId('build-check-fix-card'), 0)
    expect(fixCard).toHaveTextContent('Build fix · attempt 2')
    expect(fixCard).toHaveTextContent('Step 3: Fix the missing db module')
    expect(within(fixCard).getByText('Completed')).toBeInTheDocument()
  })

  it('FR259_D_0_6_1_23_fix_steps_join_their_check_by_buildCheckId_not_by_number', async () => {
    const project = seedWithChecks((p, steps) => {
      const check = seedBuildCheck(p, { attempt: 0, status: 'failed', fixStepNumbers: [2] })
      // Step 2 is a build-fix step of the same attempt and number, but of another (superseded) check.
      Object.assign(steps[1] ?? {}, { origin: 'build_fix', buildCheckAttempt: 0, buildCheckId: 'another-check', title: 'Old fix' })
      expect(check.fixPlanning).toBe('planned')
    })
    await openExecution(project)

    const fixCard = only(await within(await findPanel()).findAllByTestId('build-check-fix-card'), 0)
    expect(fixCard).toHaveTextContent('Step 2')
    expect(fixCard).not.toHaveTextContent('Old fix')
    expect(fixCard).toHaveTextContent('(status not loaded)')
  })
})
