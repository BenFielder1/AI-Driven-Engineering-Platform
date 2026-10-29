import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages, rollbackAwaitingMergeMessage } from '../../lib/errors'
import { seedGate } from '../../mocks/handlers/lifecycle'
import type { StoredProject } from '../../mocks/handlers/projects'
import { mergeReadiness, seedAwaitingMerge, seedRelease, seedReleasePhase } from '../../mocks/handlers/release'
import { seedProjectAs } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'
import { restoreBlockReason } from '../stages/stage-model'

async function openRelease(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/release`)
  await screen.findByRole('region', { name: 'Merge readiness' })
  return { ...utils, user: userEvent.setup() }
}

const readinessPanel = () => screen.getByRole('region', { name: 'Merge readiness' })
/** The release panel (`#release`; the stage page section is also named Release). */
const releasePanel = async () => {
  return waitFor(() => {
    const panel = document.getElementById('release')
    if (!panel) {
      throw new Error('no release panel')
    }
    return panel
  })
}

const reviewRequired = mergeReadiness({
  reasons: [{ code: 'review_required', message: 'The default branch requires 1 approving review.' }],
  checks: [
    { name: 'ci / test', status: 'completed', conclusion: 'failure', required: true, detailsUrl: 'https://github.com/acme/inventory/actions/runs/7' },
    { name: 'lint', status: 'in_progress', conclusion: null, required: false, detailsUrl: null },
  ],
})

/** In release with the pre-release gate pending, the PR waiting on a review. */
function preReleaseProject(role: 'owner' | 'viewer' = 'owner') {
  return seedProjectAs(role, (p) => {
    p.currentPhase = 'release'
    p.status = 'active'
    seedGate(p, { type: 'pre_release' })
    p.mergeReadiness = reviewRequired
  })
}

describe('Merge readiness (FR-84)', () => {
  it('FR84_readiness_review_required_shows_a_merge_on_github_will_be_needed_on_the_panel_and_the_gate', async () => {
    const project = preReleaseProject()
    await openRelease(project)

    const panel = readinessPanel()
    expect(await within(panel).findByText('A merge on GitHub will be needed')).toBeInTheDocument()
    expect(within(panel).getByRole('list', { name: 'Why a merge on GitHub is needed' })).toHaveTextContent(
      'The default branch requires 1 approving review.',
    )
    expect(within(panel).getByText(/Merge strategy: AIDE merges the pull request/)).toBeInTheDocument()
    // The pre-release gate card shows the same verdict.
    const gate = within(screen.getByRole('region', { name: 'Gates' })).getByRole('listitem', { name: 'Release approval' })
    expect(await within(gate).findByText('A merge on GitHub will be needed')).toBeInTheDocument()
  })

  it('FR84_FR85_checks_list_status_conclusion_required_and_details_link_failing_required_first', async () => {
    const project = preReleaseProject()
    await openRelease(project)

    const checks = await within(readinessPanel()).findByRole('list', { name: 'Pull request checks' })
    const [first, second] = within(checks).getAllByRole('listitem')
    expect(first).toHaveAccessibleName('ci / test')
    expect(first).toHaveTextContent('Failed')
    expect(first).toHaveTextContent('Required')
    expect(within(screen.getByRole('listitem', { name: 'ci / test' })).getByRole('link', { name: /Details of ci \/ test/ })).toHaveAttribute(
      'href',
      'https://github.com/acme/inventory/actions/runs/7',
    )
    expect(second).toHaveTextContent('Running')
    expect(second).toHaveTextContent('Optional')
  })

  it('FR84_check_again_asks_github_again_and_shows_the_new_result', async () => {
    const project = preReleaseProject()
    project.mergeReadinessOnRefresh = mergeReadiness()
    const { user } = await openRelease(project)
    const panel = readinessPanel()
    await within(panel).findByText('A merge on GitHub will be needed')

    await user.click(within(panel).getByRole('button', { name: 'Check again' }))

    expect(await within(panel).findByText('AIDE will merge automatically')).toBeInTheDocument()
    // The mock records only refresh=true checks.
    expect(requestsTo('GET', '/git/merge-readiness')).toHaveLength(1)
    expect(within(panel).queryByRole('list', { name: 'Why a merge on GitHub is needed' })).not.toBeInTheDocument()
  })

  it('FR84_manual_strategy_never_auto_merges', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'release'
      p.status = 'active'
      p.git.mergeStrategy = 'manual'
    })
    await openRelease(project)
    const panel = readinessPanel()
    expect(await within(panel).findByText('A merge on GitHub will be needed')).toBeInTheDocument()
    expect(within(panel).getByText(/The project merges manually/)).toBeInTheDocument()
  })

  it('FR84_before_review_readiness_is_not_requested', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'execution'
      p.status = 'building'
    })
    await openRelease(project)
    expect(within(readinessPanel()).getByText('Merge readiness is checked once the project reaches Review.')).toBeInTheDocument()
    expect(project.mergeReadiness).toBeUndefined()
  })
})

describe('Awaiting merge (FR-65)', () => {
  it('FR65_awaiting_merge_shows_the_live_pill_since_time_pr_link_reasons_and_polling_and_no_retry', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedAwaitingMerge(p)
    })
    await openRelease(project)

    const panel = await releasePanel()
    expect(await within(panel).findByText('Waiting for merge on GitHub')).toBeInTheDocument()
    expect(within(panel).getByText(/^since/)).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: 'Open the pull request on GitHub' })).toHaveAttribute(
      'href',
      'https://github.com/acme/inventory/pull/42',
    )
    expect(within(panel).getByRole('list', { name: 'Why AIDE is waiting' })).toHaveTextContent('requires 1 approving review')
    expect(within(panel).getByText(/every 2 minutes for the first hour, then every 10 minutes, for up to 14 days/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Retry release' })).not.toBeInTheDocument()
    // The project status reads Awaiting merge (header badge and attempt badge).
    expect(screen.getAllByText('Awaiting merge').length).toBeGreaterThanOrEqual(2)
  })

  it('FR65_pr_closed_shows_the_copy_and_the_owner_can_retry', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReleasePhase(p)
      seedRelease(p, { status: 'failed', mergeCommitSha: null, errorCode: 'PR_CLOSED', errorMessage: null })
    })
    const { user } = await openRelease(project)

    const panel = await releasePanel()
    expect(await within(panel).findByText('The pull request was closed')).toBeInTheDocument()
    expect(within(panel).getByText('The pull request was closed without merging. Reopen it on GitHub, then retry.')).toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Retry release' }))

    expect(await within(panel).findByText('Merging and tagging…')).toBeInTheDocument()
    expect(requestsTo('POST', `/projects/${project.id}/release`)).toHaveLength(1)
  })

  it('FR65_merge_timeout_has_its_own_copy', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReleasePhase(p)
      seedRelease(p, { status: 'failed', mergeCommitSha: null, errorCode: 'MERGE_TIMEOUT' })
    })
    await openRelease(project)
    const panel = await releasePanel()
    expect(await within(panel).findByText('The release stopped waiting for the merge')).toBeInTheDocument()
    expect(
      within(panel).getByText(
        "The pull request wasn't merged within 14 days, so AIDE stopped waiting. Merge it on GitHub or fix what blocks it, then retry the release.",
      ),
    ).toBeInTheDocument()
  })

  it('FR65_rollback_is_blocked_with_the_reason_while_awaiting_merge', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedAwaitingMerge(p)
    })
    await renderRoute(`/projects/${project.id}/settings/danger`)
    const button = await screen.findByRole('button', { name: 'Roll back…' })
    expect(button).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByText(rollbackAwaitingMergeMessage())).toBeInTheDocument()
  })

  it('FR65_FR92_activity_lines_for_awaiting_merge_merged_externally_and_pr_closed', () => {
    expect(
      describeActivity({
        type: 'RELEASE_AWAITING_MERGE',
        payload: {
          tag: 'v1.2.0',
          awaitingMerge: true,
          reasons: [{ code: 'review_required', message: '1 approving review is required' }],
        },
        summary: 's',
      }),
    ).toBe('Release v1.2.0 is waiting for its pull request to be merged on GitHub: 1 approving review is required')
    expect(describeActivity({ type: 'RELEASE_AWAITING_MERGE', payload: { tag: 'v1.2.0' }, summary: 's' })).toBe(
      'Release v1.2.0 is waiting for its pull request to be merged on GitHub',
    )
    expect(
      describeActivity({ type: 'RELEASE_COMPLETED', payload: { tag: 'v1.2.0', mergedExternally: true }, summary: 's' }),
    ).toBe('Released: merged on GitHub and tagged v1.2.0')
    expect(describeActivity({ type: 'RELEASE_FAILED', payload: { tag: 'v1.2.0', errorCode: 'MERGE_TIMEOUT' }, summary: 's' })).toBe(
      "Release v1.2.0 failed: the pull request wasn't merged within 14 days",
    )
    expect(describeActivity({ type: 'MERGE_READINESS_CHECKED', payload: {}, summary: 's' })).toBe('Merge readiness checked')
    expect(
      describeActivity({ type: 'REVIEW_COMPLETED', payload: { overallScore: 0.9, checksTimedOut: true }, summary: 's' }),
    ).toMatch(/stopped waiting for checks$/)
    expect(describeActivity({ type: 'RELEASE_MERGED_EXTERNALLY', payload: { tag: 'v1.2.0' }, summary: 's' })).toBe(
      'Release v1.2.0: the pull request was merged on GitHub',
    )
    expect(describeActivity({ type: 'RELEASE_FAILED', payload: { tag: 'v1.2.0', errorCode: 'PR_CLOSED' }, summary: 's' })).toBe(
      'Release v1.2.0 failed: the pull request was closed without merging',
    )
  })
})

describe('GitHub Release (FR-64)', () => {
  it('FR64_released_shows_released_version_tag_and_the_github_release_link', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReleasePhase(p)
      p.status = 'released'
      const version = p.releaseVersions[0]
      if (version) {
        version.status = 'released'
      }
      seedRelease(p, { tag: 'v0.1.0', githubReleaseUrl: 'https://github.com/acme/inventory/releases/tag/v0.1.0' })
    })
    await openRelease(project)

    const panel = await releasePanel()
    expect(await within(panel).findByText('Released 0.1.0')).toBeInTheDocument()
    expect(within(panel).getByText('v0.1.0')).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: 'View the GitHub Release' })).toHaveAttribute(
      'href',
      'https://github.com/acme/inventory/releases/tag/v0.1.0',
    )
    const section = screen.getByRole('region', { name: 'GitHub Release' })
    await waitFor(() => {
      expect(within(section).getByRole('link', { name: 'View the GitHub Release for v0.1.0' })).toHaveAttribute(
        'href',
        'https://github.com/acme/inventory/releases/tag/v0.1.0',
      )
    })
    // Released: merge readiness has nothing left to check.
    expect(within(readinessPanel()).getByText(/no pull request left to merge/)).toBeInTheDocument()
  })

  it('FR64_retry_conflict_copy_is_contextual', () => {
    expect(contextualErrorMessages.releaseSettings.CONFLICT).toMatch(/awaiting its merge/)
  })
})

describe('Awaiting merge elsewhere (FR-65)', () => {
  it('FR65_restore_is_blocked_as_releasing_while_awaiting_merge', () => {
    expect(
      restoreBlockReason({
        type: 'technical_specification',
        project: { currentPhase: 'release', status: 'awaiting_merge', developmentVersion: '1.2.0' },
        readOnly: false,
        version: { integrity: 'ok', releaseVersion: '1.2.0' },
      }),
    ).toBe('releasing')
  })
})
