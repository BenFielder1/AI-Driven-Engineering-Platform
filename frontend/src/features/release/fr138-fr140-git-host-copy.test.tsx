import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { copyForCode, rollbackAwaitingMergeMessage } from '../../lib/errors'
import { gitHostTerms } from '../../lib/git-host'
import { seedPullRequest } from '../../mocks/handlers/execution'
import { seedGate } from '../../mocks/handlers/lifecycle'
import type { StoredProject } from '../../mocks/handlers/projects'
import { mergeReadiness, seedAwaitingMerge, seedRelease, seedReleasePhase } from '../../mocks/handlers/release'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'
import { mergeStrategyDescription, mergeStrategyLabel, notificationEventLabels } from '../projects/labels'

type Host = 'github' | 'gitlab'

const hosts = {
  github: {
    name: 'GitHub',
    pr: 'pull request',
    prTitle: 'Pull request',
    release: 'GitHub Release',
    prUrl: 'https://github.com/acme/inventory/pull/42',
    releaseUrl: 'https://github.com/acme/inventory/releases/tag/v0.1.0',
    // GitHub's raw mergeStateStatus.
    rawState: 'BLOCKED',
  },
  gitlab: {
    name: 'GitLab',
    pr: 'merge request',
    prTitle: 'Merge request',
    release: 'GitLab Release',
    prUrl: 'https://gitlab.com/acme/inventory/-/merge_requests/7',
    releaseUrl: 'https://gitlab.com/acme/inventory/-/releases/v0.1.0',
    // GitLab's detailed_merge_status (FR-138).
    rawState: 'not_approved',
  },
} as const

/** Records the project's PR/MR on the given host (the Release page reads the host from /state's pull request). */
function onHost(p: StoredProject, host: Host) {
  const h = hosts[host]
  seedPullRequest(p, {
    provider: host === 'gitlab' ? 'gitlab' : 'github_app',
    number: host === 'gitlab' ? 7 : 42,
    url: h.prUrl,
  })
}

async function openRelease(project: StoredProject) {
  await renderRoute(`/projects/${project.id}/release`)
  await screen.findByRole('region', { name: 'Merge readiness' })
}

const releasePanel = () =>
  waitFor(() => {
    const panel = document.getElementById('release')
    if (!panel) {
      throw new Error('no release panel')
    }
    return panel
  })

describe.each(['github', 'gitlab'] as const)('Git host copy on the Release page (%s)', (host) => {
  const h = hosts[host]

  it(`FR138_merge_readiness_names_the_host_and_hides_the_raw_merge_state_${host}`, async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'release'
      p.status = 'active'
      seedGate(p, { type: 'pre_release' })
      onHost(p, host)
      p.mergeReadiness = mergeReadiness({
        reasons: [
          { code: 'review_required', message: '' },
          { code: 'draft', message: '' },
        ],
        mergeStateStatus: h.rawState,
      })
    })
    await openRelease(project)

    const panel = screen.getByRole('region', { name: 'Merge readiness' })
    expect(await within(panel).findByText(`A merge on ${h.name} will be needed`)).toBeInTheDocument()
    const reasons = within(panel).getByRole('list', { name: `Why a merge on ${h.name} is needed` })
    expect(reasons).toHaveTextContent('An approving review is required')
    expect(reasons).toHaveTextContent('The merge request is still a draft')
    expect(within(panel).getByText(new RegExp(`Merge strategy: AIDE merges the ${h.pr}`))).toBeInTheDocument()
    expect(within(panel).getByRole('list', { name: `${h.prTitle} checks` })).toBeInTheDocument()
    // The host's raw merge state is never shown.
    expect(screen.queryByText(new RegExp(h.rawState))).not.toBeInTheDocument()

    const gate = within(screen.getByRole('region', { name: 'Gates' })).getByRole('listitem', { name: 'Release approval' })
    expect(await within(gate).findByText(`A merge on ${h.name} will be needed`)).toBeInTheDocument()
  })

  it(`FR140_release_link_names_the_host_release_${host}`, async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReleasePhase(p)
      onHost(p, host)
      p.status = 'released'
      const version = p.releaseVersions[0]
      if (version) {
        version.status = 'released'
      }
      seedRelease(p, { tag: 'v0.1.0', pullRequestUrl: h.prUrl, githubReleaseUrl: h.releaseUrl })
    })
    await openRelease(project)

    const panel = await releasePanel()
    expect(await within(panel).findByRole('link', { name: `View the ${h.release}` })).toHaveAttribute('href', h.releaseUrl)
    expect(within(panel).getByText(h.release)).toBeInTheDocument()
    expect(within(panel).getByText(h.prTitle)).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: `View on ${h.name}` })).toHaveAttribute('href', h.prUrl)

    const section = screen.getByRole('region', { name: h.release })
    await waitFor(() => {
      expect(within(section).getByRole('link', { name: `View the ${h.release} for v0.1.0` })).toHaveAttribute('href', h.releaseUrl)
    })
    expect(within(screen.getByRole('region', { name: 'Merge readiness' })).getByText(new RegExp(`no ${h.pr} left to merge`))).toBeInTheDocument()
  })

  it(`FR140_awaiting_merge_names_the_host_and_its_merge_request_${host}`, async () => {
    const project = seedProjectAs('owner', (p) => {
      onHost(p, host)
      seedAwaitingMerge(p, { pullRequestUrl: h.prUrl })
    })
    await openRelease(project)

    const panel = await releasePanel()
    expect(await within(panel).findByText(`Waiting for merge on ${h.name}`)).toBeInTheDocument()
    expect(within(panel).getByText(new RegExp(`AIDE couldn't merge the ${h.pr} itself. Merge it on ${h.name}`))).toBeInTheDocument()
    expect(within(panel).getByText(new RegExp(`creates the ${h.release}\\.`))).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: `Open the ${h.pr} on ${h.name}` })).toHaveAttribute('href', h.prUrl)
    expect(within(panel).getByText(new RegExp(`AIDE checks the ${h.pr} every 2 minutes`))).toBeInTheDocument()
  })

  it(`FR140_pr_closed_names_the_host_${host}`, async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReleasePhase(p)
      onHost(p, host)
      seedRelease(p, { status: 'failed', mergeCommitSha: null, errorCode: 'PR_CLOSED', errorMessage: null, pullRequestUrl: h.prUrl })
    })
    await openRelease(project)

    const panel = await releasePanel()
    expect(await within(panel).findByText(`The ${h.pr} was closed`)).toBeInTheDocument()
    expect(within(panel).getByText(`The ${h.pr} was closed without merging. Reopen it on ${h.name}, then retry.`)).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: `Open the ${h.pr} on ${h.name}` })).toHaveAttribute('href', h.prUrl)
  })
})

describe('Git host copy helpers (FR-140)', () => {
  it('FR140_recorded_merge_codes_default_to_github_and_name_gitlab_when_given', () => {
    expect(copyForCode('PR_CLOSED', 'releaseAttempt')).toBe('The pull request was closed without merging. Reopen it on GitHub, then retry.')
    expect(copyForCode('MERGE_TIMEOUT', 'releaseAttempt', gitHostTerms('gitlab'))).toBe(
      "The merge request wasn't merged within 14 days, so AIDE stopped waiting. Merge it on GitLab or fix what blocks it, then retry the release.",
    )
  })

  it('FR140_merge_strategy_copy_names_the_host', () => {
    expect(mergeStrategyLabel('aide_merges', gitHostTerms('github_pat'))).toBe('AIDE merges the pull request')
    expect(mergeStrategyLabel('manual', gitHostTerms('gitlab'))).toBe('Merge manually on GitLab — AIDE waits and tags the merge')
    expect(mergeStrategyDescription('manual', gitHostTerms('gitlab'))).toMatch(/merges it on GitLab, AIDE tags the merge commit and creates the GitLab Release\.$/)
  })
})

describe('Activity, rollback and notification copy name the Git host (FR-140)', () => {
  const line = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 's' })

  it('FR140_activity_release_lines_name_gitlab_from_the_payload_provider', () => {
    const gitlab = { tag: 'v1.2.0', provider: 'gitlab' }
    expect(line('RELEASE_AWAITING_MERGE', gitlab)).toBe('Release v1.2.0 is waiting for its merge request to be merged on GitLab')
    expect(line('RELEASE_COMPLETED', { ...gitlab, mergedExternally: true })).toBe('Released: merged on GitLab and tagged v1.2.0')
    expect(line('RELEASE_MERGED_EXTERNALLY', gitlab)).toBe('Release v1.2.0: the merge request was merged on GitLab')
    expect(line('RELEASE_FAILED', { ...gitlab, errorCode: 'PR_CLOSED' })).toBe(
      'Release v1.2.0 failed: the merge request was closed without merging',
    )
    expect(line('RELEASE_FAILED', { ...gitlab, errorCode: 'MERGE_TIMEOUT' })).toBe(
      "Release v1.2.0 failed: the merge request wasn't merged within 14 days",
    )
    expect(line('MERGE_READINESS_CHECKED', { provider: 'gitlab', willAutoMerge: false })).toBe(
      'Merge readiness checked: a merge on GitLab will be needed',
    )
  })

  it('FR140_activity_release_lines_without_a_provider_or_with_an_unknown_one_read_as_github', () => {
    expect(line('RELEASE_AWAITING_MERGE', { tag: 'v1.2.0' })).toBe('Release v1.2.0 is waiting for its pull request to be merged on GitHub')
    expect(line('RELEASE_MERGED_EXTERNALLY', { tag: 'v1.2.0', provider: 'github_app' })).toBe(
      'Release v1.2.0: the pull request was merged on GitHub',
    )
    expect(line('MERGE_READINESS_CHECKED', { willAutoMerge: false, provider: 'svn' })).toBe(
      'Merge readiness checked: a merge on GitHub will be needed',
    )
    expect(line('RELEASE_FAILED', { tag: 'v1.2.0', errorCode: 'PR_CLOSED' })).toBe(
      'Release v1.2.0 failed: the pull request was closed without merging',
    )
  })

  it('FR140_rollback_awaiting_merge_reason_names_the_host', () => {
    expect(rollbackAwaitingMergeMessage()).toBe(
      "A rollback isn't possible while the release is awaiting its merge on GitHub (the release is partially applied). Merge or close the pull request first.",
    )
    expect(rollbackAwaitingMergeMessage(gitHostTerms('gitlab'))).toBe(
      "A rollback isn't possible while the release is awaiting its merge on GitLab (the release is partially applied). Merge or close the merge request first.",
    )
  })

  it('FR140_danger_zone_rollback_reason_names_gitlab_for_a_gitlab_project', async () => {
    const project = seedProjectAs('owner', (p) => {
      onHost(p, 'gitlab')
      seedAwaitingMerge(p, { pullRequestUrl: hosts.gitlab.prUrl })
    })
    await renderRoute(`/projects/${project.id}/settings/danger`)
    expect(await screen.findByText(rollbackAwaitingMergeMessage(gitHostTerms('gitlab')))).toBeInTheDocument()
  })

  it('FR140_the_awaiting_merge_notification_label_names_no_single_host', () => {
    // Notifications carry no provider, so the type label stays host-neutral.
    expect(notificationEventLabels.release_awaiting_merge).toBe('Release awaiting a merge on the Git host')
  })
})
