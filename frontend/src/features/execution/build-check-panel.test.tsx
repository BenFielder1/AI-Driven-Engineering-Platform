import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { buildCheck, seedBuildCheck, seedExecution } from '../../mocks/handlers/execution'
import type { StoredExecutionStep, StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { axeViolations } from '../../test/axe'
import { renderRoute } from '../../test/render'

type BuildCheck = Schemas['BuildCheckDto']

const moduleNotFound = [
  '15:02:11.204 Running "npm run build"',
  "15:02:19.870 Failed to compile.",
  "15:02:19.871 ./app/page.tsx",
  "15:02:19.871 Module not found: Can't resolve '@/lib/db'",
].join('\n')

/** A project in Execution with 3 plan steps, `maxBuildFixAttempts` 5 and the checks `setup` seeds. */
function seedWithChecks(setup: (p: StoredProject, steps: StoredExecutionStep[]) => void, role: 'owner' | 'viewer' = 'owner') {
  return seedProjectAs(role, (p) => {
    p.maxBuildFixAttempts = 5
    const steps = seedExecution(p, 3, (s) => {
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

const panel = () => screen.getByRole('region', { name: 'Build check' })
const findPanel = () => screen.findByRole('region', { name: 'Build check' })

/** Attempt 0 failed (fix steps 4, 5), attempt 1 failed (fix step 6), attempt 2 waits for Vercel with CI partly done. */
function seedAttemptTwoWaiting(p: StoredProject) {
  seedBuildCheck(p, {
    attempt: 0,
    status: 'failed',
    headSha: 'aaaaaaa1111111111111111111111111111111111',
    startedAt: '2026-09-01T11:00:00Z',
    sources: [{ name: 'Vercel preview', kind: 'preview', conclusion: 'failure', url: 'https://vercel.com/acme/inventory/a0' }],
    failures: [{ failureId: 'failure:preview:vercel', source: 'preview', message: 'Vercel build failed', excerpt: moduleNotFound }],
    fixStepNumbers: [4, 5],
    completedAt: '2026-09-01T11:05:00Z',
  })
  seedBuildCheck(p, {
    attempt: 1,
    status: 'failed',
    headSha: 'bbbbbbb2222222222222222222222222222222222',
    startedAt: '2026-09-01T11:10:00Z',
    sources: [{ name: 'Vercel preview', kind: 'preview', conclusion: 'failure', url: 'https://vercel.com/acme/inventory/a1' }],
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
      { name: 'Vercel preview', kind: 'preview', conclusion: null, url: null },
      { name: 'build', kind: 'check', conclusion: 'success', url: 'https://github.com/acme/inventory/actions/runs/1' },
      { name: 'lint', kind: 'check', conclusion: 'success', url: 'https://github.com/acme/inventory/actions/runs/2' },
    ],
  })
}

describe('Build check panel (FR-259)', () => {
  it('FR259_attempt_2_of_5_waiting_for_vercel_shows_ci_so_far_and_the_previous_excerpt', async () => {
    const project = seedWithChecks((p) => {
      p.state.review.cycle = 1
      seedAttemptTwoWaiting(p)
    })
    await openExecution(project)

    const region = await findPanel()
    expect(within(region).getByTestId('build-check-summary')).toHaveTextContent(
      'Attempt 2 of 5 · Waiting for Vercel preview',
    )
    expect(within(region).getByText('Waiting', { selector: 'span' })).toBeInTheDocument()

    // The sources so far: the preview pending, the CI checks `build` and `lint` passed (linked).
    const sources = within(region).getByRole('list', { name: 'Sources' })
    const build = within(sources).getByRole('link', { name: /^build/ })
    expect(build).toHaveAttribute('href', 'https://github.com/acme/inventory/actions/runs/1')
    expect(within(build.closest('li') as HTMLElement).getByText('Passed')).toBeInTheDocument()
    expect(within(sources).getAllByText('Passed')).toHaveLength(2)
    expect(within(sources).getAllByText('Pending')).toHaveLength(1)

    // Earlier attempts collapse into a list; the previous attempt's excerpt is open while the next check waits.
    const earlier = within(region).getByRole('list', { name: 'Earlier attempts' })
    expect(earlier.children).toHaveLength(2)
    const excerpts = within(earlier).getAllByTestId('build-check-excerpt')
    const previous = excerpts.find((e) => e.textContent.includes("Property 'id' does not exist"))
    expect(previous).toBeVisible()
    expect(previous).toHaveAttribute('tabindex', '0')
    // Attempt 0 stays collapsed.
    const first = excerpts.find((e) => e.textContent.includes("Can't resolve '@/lib/db'"))
    expect(first).not.toBeVisible()
    expect(within(earlier).getByText(/fix steps 4, 5/)).toBeInTheDocument()
  })

  it('FR259_the_panel_has_no_axe_violations', async () => {
    const project = seedWithChecks((p) => {
      seedAttemptTwoWaiting(p)
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
    expect(within(region).getByTestId('build-check-summary')).toHaveTextContent(
      'Initial check · Waiting for the preview build and CI checks',
    )
  })

  it.each<[BuildCheck['status'], Partial<BuildCheck>, string, string]>([
    ['passed', {}, 'Passed', 'Attempt 2 of 5 · The preview build and required checks passed'],
    ['failed', { fixStepNumbers: [7, 8] }, 'Failed', 'Attempt 2 of 5 · Build failed: fix steps 7 and 8 planned'],
    ['failed', {}, 'Failed', 'Attempt 2 of 5 · Build failed: planning fix steps'],
    ['skipped', {}, 'Skipped', 'Attempt 2 of 5 · No preview target or required checks'],
    ['exhausted', {}, 'Still failing', 'Attempt 2 of 5 · Review starts with the failures'],
    ['cancelled', {}, 'Cancelled', 'Attempt 2 of 5 · Cancelled: the version changed or execution was stopped'],
  ])('FR259_status_copy_%s', async (status, extra, badge, summary) => {
    const project = seedWithChecks((p) => {
      seedBuildCheck(p, { attempt: 2, status, completedAt: '2026-09-01T12:00:00Z', ...extra })
    })
    await openExecution(project)

    const region = await findPanel()
    expect(within(region).getByText(badge, { selector: 'span' })).toBeInTheDocument()
    expect(within(region).getByTestId('build-check-summary')).toHaveTextContent(summary)
  })

  it('FR258_FR259_exhausted_warns_that_review_includes_the_failures', async () => {
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
        sources: [{ name: 'Vercel preview', kind: 'preview', conclusion: 'failure', url: null }],
        failures: [{ failureId: 'failure:preview:vercel', source: 'preview', message: 'Vercel build failed', excerpt: moduleNotFound }],
        completedAt: '2026-09-01T12:00:00Z',
      })
    })
    await openExecution(project)

    const region = await findPanel()
    expect(within(region).getByTestId('build-check-summary')).toHaveTextContent('Attempt 2 of 2')
    expect(
      within(region).getByText('Build still failing after 2 fix attempts. Review will include the failures.'),
    ).toBeInTheDocument()
    // The current attempt's failure shows its excerpt in the scrolling evidence block.
    const excerpt = within(region).getByTestId('build-check-excerpt')
    expect(excerpt).toHaveTextContent("Module not found: Can't resolve '@/lib/db'")
    expect(excerpt).toHaveAccessibleName('Log excerpt for failure:preview:vercel')
    expect(excerpt).toHaveClass('overflow-auto')
    expect(within(region).getByText('Vercel preview')).toBeInTheDocument()
  })

  it('FR259_after_a_rollback_the_newer_initial_check_stays_current_on_a_live_update', async () => {
    let superseded: BuildCheck | undefined
    const project = seedWithChecks((p) => {
      // Attempt 2 of the cycle started first; a rollback superseded it, and a new initial check started later.
      superseded = seedBuildCheck(p, { attempt: 2, status: 'cancelled', startedAt: '2026-09-01T11:00:00Z', completedAt: '2026-09-01T11:05:00Z' })
      seedBuildCheck(p, { attempt: 0, status: 'waiting', startedAt: '2026-09-01T12:00:00Z' })
    })
    const { live, queryClient } = await openExecution(project)
    const region = await findPanel()
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
      expect(queryClient.isFetching()).toBe(0)
    })
    expect(within(region).getByTestId('build-check-summary')).toHaveTextContent(/^Initial check · Waiting/)
    if (!superseded) {
      throw new Error('not seeded')
    }

    // A live update of the older check keeps it in the earlier attempts: newest first is by start time.
    live.latest.emitBuildCheck({ projectId: project.id, check: { ...superseded, completedAt: '2026-09-01T11:06:00Z' } })
    await waitFor(() => {
      expect(within(panel()).getByRole('list', { name: 'Earlier attempts' })).toHaveTextContent('Attempt 2 of 5')
    })
    expect(within(panel()).getByTestId('build-check-summary')).toHaveTextContent(/^Initial check · Waiting/)
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

    const region = await findPanel()
    expect(within(region).getByTestId('build-check-summary')).toHaveTextContent('Attempt 1 of 5 · The preview build and required checks passed')
    expect(within(region).getByText(/Before review cycle 1 · commit/)).toBeInTheDocument()
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
    const { live, queryClient } = await openExecution(project)
    await findPanel()
    // After the join everything is refetched once; let that settle so only the live message changes the panel.
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
      expect(queryClient.isFetching()).toBe(0)
    })
    if (!waiting) {
      throw new Error('not seeded')
    }

    // Only the live message carries the new state (the mock API still answers "waiting").
    live.latest.emitBuildCheck({
      projectId: project.id,
      check: {
        ...waiting,
        status: 'passed',
        sources: waiting.sources.map((s) => ({ ...s, conclusion: 'success' })),
        completedAt: '2026-09-01T11:40:00Z',
      },
    })
    expect(await within(panel()).findByText('Attempt 2 of 5 · The preview build and required checks passed')).toBeInTheDocument()

    // A message for another project is ignored.
    live.latest.emitBuildCheck({ projectId: 'other', check: { ...waiting, status: 'cancelled' } })
    expect(within(panel()).getByTestId('build-check-summary')).toHaveTextContent('The preview build and required checks passed')

    // A new attempt arrives live and becomes the current one; attempt 2 joins the earlier attempts.
    live.latest.emitBuildCheck({
      projectId: project.id,
      check: buildCheck(project, { reviewCycle: 1, attempt: 3, status: 'waiting', sources: [], startedAt: '2026-09-01T11:45:00Z' }),
    })
    expect(await within(panel()).findByText(/^Attempt 3 of 5 ·/)).toBeInTheDocument()
    expect(within(panel()).getByRole('heading', { name: 'Earlier attempts (3)' })).toBeInTheDocument()
  })
})

describe('Build fix badge (FR-259)', () => {
  it('FR259_build_fix_steps_carry_the_build_fix_attempt_badge', async () => {
    const project = seedWithChecks((p, steps) => {
      Object.assign(steps[2] ?? {}, { origin: 'build_fix', buildCheckAttempt: 2, title: 'Fix the missing db module' })
      seedBuildCheck(p, { attempt: 2, status: 'failed', fixStepNumbers: [3] })
    })
    await openExecution(project)

    const list = await screen.findByRole('list', { name: 'Execution steps' })
    const fix = within(list).getByRole('listitem', { name: /^Step 3:/ })
    expect(within(fix).getByTestId('build-fix-badge')).toHaveTextContent('Build fix · attempt 2')
    const plan = within(list).getByRole('listitem', { name: /^Step 1:/ })
    expect(within(plan).queryByTestId('build-fix-badge')).not.toBeInTheDocument()
  })
})
