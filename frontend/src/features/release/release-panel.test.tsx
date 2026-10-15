import { screen, within } from '@testing-library/react'
import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'

import { seedRelease, seedReleasePhase } from '../../mocks/handlers/release'
import { openOverviewAs } from '../../test/overview'
import { requestsTo } from '../../test/project-settings'
import { describeActivity } from '../activity/activity-summary'
import { invalidateForEvent } from '../live/live-invalidation'
import { contextualErrorMessages } from '../../lib/errors'
import { projectKeys } from '../projects/projects-api'

const releasePanel = () => screen.findByRole('region', { name: 'Release' })

describe('Overview → Release (FR-42, FR-43)', () => {
  it('FR42_release_panel_shows_tag_and_merge', async () => {
    await openOverviewAs('owner', { checkpoint: 6 }, (p) => {
      seedReleasePhase(p)
      p.status = 'released'
      seedRelease(p)
    })

    const panel = await releasePanel()
    expect(await within(panel).findByText('Released')).toBeInTheDocument()
    expect(within(panel).getByText('v0.1.0')).toBeInTheDocument()
    expect(within(panel).getByTitle('3f2a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f')).toHaveTextContent('3f2a9c1')
    expect(within(panel).getByRole('link', { name: 'View on GitHub' })).toHaveAttribute(
      'href',
      'https://github.com/acme/inventory/pull/42',
    )
    // Released: nothing to retry.
    expect(within(panel).queryByRole('button', { name: 'Retry release' })).not.toBeInTheDocument()
  })

  it('FR42: no release panel before the release phase', async () => {
    await openOverviewAs('owner', { currentPhase: 'review', status: 'reviewing' })
    expect(screen.queryByRole('region', { name: 'Release' })).not.toBeInTheDocument()
  })

  it('FR43_conflict_lists_files_and_owner_can_retry', async () => {
    const { user, project } = await openOverviewAs('owner', { checkpoint: 6 }, (p) => {
      seedReleasePhase(p)
      seedRelease(p, {
        status: 'failed',
        mergeCommitSha: null,
        errorCode: 'MERGE_CONFLICT',
        errorMessage: 'Automatic merge failed.',
        conflictingFiles: ['src/inventory.ts', 'README.md'],
      })
    })

    const panel = await releasePanel()
    expect(await within(panel).findByText('Failed')).toBeInTheDocument()
    expect(within(panel).getByText('The release hit a merge conflict')).toBeInTheDocument()
    const files = within(panel).getByRole('list', { name: 'Conflicting files' })
    expect(within(files).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['src/inventory.ts', 'README.md'])
    expect(within(panel).getByText(/Resolve the conflict on the project branch, then retry the release/)).toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Retry release' }))

    expect(await within(panel).findByText('Merging and tagging…')).toBeInTheDocument()
    expect(requestsTo('POST', `/projects/${project.id}/release`)).toHaveLength(1)
    expect(within(panel).queryByRole('button', { name: 'Retry release' })).not.toBeInTheDocument()
    // Both attempts are listed, newest first.
    const history = within(panel).getByRole('list', { name: 'Release attempts' })
    expect(within(history).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      expect.stringContaining('attempt 2'),
      expect.stringContaining('attempt 1'),
    ])
  })

  it('FR43: a refused retry (RELEASE_IN_PROGRESS) shows the copy and the latest state', async () => {
    const { user, project } = await openOverviewAs('owner', {}, (p) => {
      seedReleasePhase(p)
      seedRelease(p, { status: 'failed', errorCode: 'MERGE_CONFLICT', conflictingFiles: ['a.ts'] })
    })
    const panel = await releasePanel()
    await within(panel).findByText('Failed')
    // Someone else retried meanwhile: the server has an in-progress attempt the page hasn't seen.
    seedRelease(project, { status: 'in_progress', completedAt: null, mergeCommitSha: null })

    await user.click(within(panel).getByRole('button', { name: 'Retry release' }))
    expect(await within(panel).findByText('A release is already queued or running. The page shows its outcome when it finishes.')).toBeInTheDocument()
    expect(await within(panel).findByText('Merging and tagging…')).toBeInTheDocument()
  })

  it('FR43_no_attempt_shows_owner_retry', async () => {
    // Releasing with no attempt at all (its job was lost): not "starting" forever, the Owner can start one.
    const { user, project } = await openOverviewAs('owner', { checkpoint: 6 }, (p) => {
      seedReleasePhase(p)
    })

    const panel = await releasePanel()
    expect(await within(panel).findByText('No release attempt is running for 0.1.0 · checkpoint 6.')).toBeInTheDocument()
    expect(within(panel).queryByText('The release is starting…')).not.toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Retry release' }))
    expect(await within(panel).findByText('Merging and tagging…')).toBeInTheDocument()
    expect(requestsTo('POST', `/projects/${project.id}/release`)).toHaveLength(1)
  })

  it('FR43, FR63: an attempt at an older checkpoint only is no attempt for the current one', async () => {
    await openOverviewAs('owner', { checkpoint: 6 }, (p) => {
      seedReleasePhase(p)
      seedRelease(p, { checkpoint: 4, tag: 'v4-aide' })
    })

    const panel = await releasePanel()
    expect(await within(panel).findByText('No release attempt is running for 0.1.0 · checkpoint 6.')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Retry release' })).toBeInTheDocument()
    expect(within(panel).getByRole('list', { name: 'Release attempts' })).toHaveTextContent('0.1.0 · checkpoint 4, attempt 1')
  })

  it('FR43: with no attempt, a non-owner is told an Owner can start it', async () => {
    await openOverviewAs('contributor', {}, (p) => {
      seedReleasePhase(p)
    })

    const panel = await releasePanel()
    expect(await within(panel).findByText(/No release attempt is running/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Retry release' })).not.toBeInTheDocument()
    expect(within(panel).getByText('An Owner can retry the release once the problem is fixed.')).toBeInTheDocument()
  })

  it.each(['TAG_EXISTS', 'GIT_ERROR', 'RELEASE_CANCELLED'] as const)(
    'FR43_failed_release_copy_%s',
    async (code) => {
      await openOverviewAs('owner', { checkpoint: 6 }, (p) => {
        seedReleasePhase(p)
        seedRelease(p, { status: 'failed', mergeCommitSha: null, errorCode: code, tag: 'v6-aide.2' })
      })

      const panel = await releasePanel()
      expect(await within(panel).findByText(contextualErrorMessages.releaseAttempt[code])).toBeInTheDocument()
      // Suffixed tags are shown as-is, never parsed.
      expect(within(panel).getByText('v6-aide.2')).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Retry release' })).toBeInTheDocument()
    },
  )

  it('FR43_retry_hidden_for_non_owner', async () => {
    await openOverviewAs('contributor', {}, (p) => {
      seedReleasePhase(p)
      seedRelease(p, { status: 'failed', errorCode: 'MERGE_CONFLICT', conflictingFiles: ['src/inventory.ts'] })
    })

    const panel = await releasePanel()
    expect(await within(panel).findByText('The release hit a merge conflict')).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Retry release' })).not.toBeInTheDocument()
    expect(within(panel).getByText('An Owner can retry the release once the problem is fixed.')).toBeInTheDocument()
  })
})

describe('Release and rollback events (FR-46, FR-50)', () => {
  it('FR46, FR60: a release event (including VERSION_STARTED) refreshes the whole project and the list', async () => {
    const client = new QueryClient()
    const spy = vi.spyOn(client, 'invalidateQueries')
    await invalidateForEvent(client, 'p1', 'release')
    const keys = spy.mock.calls.map(([filters]) => filters?.queryKey)
    // The detail key prefixes the releases, release versions and state queries.
    expect(keys).toContainEqual(projectKeys.detail('p1'))
    expect(keys).toContainEqual(projectKeys.lists())
  })

  it('FR50: release events read as plain sentences', () => {
    const d = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 'server' })
    expect(d('RELEASE_STARTED', { tag: 'v6-aide', attempt: 2 })).toBe('Release v6-aide started (attempt 2)')
    expect(d('RELEASE_COMPLETED', { tag: 'v6-aide' })).toBe('Released: merged and tagged v6-aide')
    expect(d('RELEASE_FAILED', { tag: 'v6-aide', errorCode: 'MERGE_CONFLICT', conflictingFiles: ['a', 'b'] })).toBe(
      'Release v6-aide failed: merge conflict in 2 files',
    )
    expect(d('RELEASE_FAILED', {})).toBe('Release failed')
  })
})

