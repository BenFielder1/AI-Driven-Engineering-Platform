import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { gitHostTerms, unknownGitHost } from '../../../lib/git-host'
import { contextualErrorMessages, rollbackAwaitingMergeMessage, rollbackBlockedMessages } from '../../../lib/errors'
import { seedPullRequest } from '../../../mocks/handlers/execution'
import { problem } from '../../../mocks/handlers/problem'
import { gitSettings } from '../../../mocks/handlers/projects'
import { seedAwaitingMerge, seedRelease, seedReleasePhase } from '../../../mocks/handlers/release'
import { seedReviewPhase } from '../../../mocks/handlers/review'
import { server } from '../../../mocks/server'
import { seedProjectAs } from '../../../test/artifacts'
import { openOwnerSettings, requestsTo } from '../../../test/project-settings'
import { renderRoute } from '../../../test/render'

const failState = () => {
  server.use(http.get('*/api/projects/:id/state', () => problem(502, 'UPSTREAM_ERROR', 'Boom.')))
}

describe('FR-155 (M31): host-neutral copy and release settings', () => {
  it('FR155_review_preview_not_configured_names_no_single_host', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReviewPhase(p)
    })
    await renderRoute(`/projects/${project.id}/review`)

    const text = await screen.findByTestId('deployment-not-configured')
    expect(within(text).getByRole('link', { name: 'Set up deployment in Settings → Deployment' })).toBeInTheDocument()
    expect(text).not.toHaveTextContent('Vercel')
  })

  it('FR155_release_form_falls_back_when_the_host_does_not_offer_the_held_strategy', async () => {
    const { user } = await openOwnerSettings('git', { git: gitSettings({ provider: 'github_pat', mergeStrategy: 'squash' }) })
    const form = await screen.findByRole('form', { name: 'Release' })

    expect(within(form).getByTestId('merge-strategy-unavailable')).toHaveTextContent(
      "This project is set to “AIDE merges (squash)”, which GitHub doesn't offer.",
    )
    expect(within(form).getByLabelText('AIDE merges the pull request')).toBeChecked()

    await user.click(within(form).getByRole('button', { name: 'Save release settings' }))
    expect(await screen.findByText('Release settings saved.')).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/git').at(-1)?.body).toEqual({ mergeStrategy: 'aide_merges' })
    await waitFor(() => {
      expect(within(form).queryByTestId('merge-strategy-unavailable')).not.toBeInTheDocument()
    })
  })

  it('FR155_release_form_shows_no_fallback_for_a_strategy_the_host_offers', async () => {
    await openOwnerSettings('git', { git: gitSettings({ provider: 'bitbucket', mergeStrategy: 'squash' }) })
    const form = await screen.findByRole('form', { name: 'Release' })

    expect(within(form).getByLabelText('AIDE merges (squash)')).toBeChecked()
    expect(within(form).queryByTestId('merge-strategy-unavailable')).not.toBeInTheDocument()
  })

  it('FR155_bitbucket_release_notes_error_uses_its_own_copy', async () => {
    server.use(http.get('*/api/projects/:id/releases/:releaseId/notes', () => problem(404, 'NOT_FOUND', 'Gone.')))
    const project = seedProjectAs('owner', (p) => {
      seedReleasePhase(p)
      seedPullRequest(p, { provider: 'bitbucket', number: 3, url: 'https://bitbucket.org/acme/todo-app/pull-requests/3' })
      p.status = 'released'
      seedRelease(p, { tag: 'v0.1.0', githubReleaseUrl: 'https://bitbucket.org/acme/todo-app/src/v0.1.0' })
    })
    await renderRoute(`/projects/${project.id}/release`)

    expect(await screen.findByText(contextualErrorMessages.releaseNotes.NOT_FOUND)).toBeInTheDocument()
    expect(screen.getByText("The release notes couldn't load")).toBeInTheDocument()
  })

  it('FR155_danger_zone_reads_the_host_from_settings_when_state_fails', async () => {
    failState()
    const project = seedProjectAs('owner', (p) => {
      p.git = gitSettings({ provider: 'bitbucket' })
      seedAwaitingMerge(p)
    })
    await renderRoute(`/projects/${project.id}/settings/danger`)

    expect(await screen.findByText(rollbackAwaitingMergeMessage(gitHostTerms('bitbucket')))).toBeInTheDocument()
  })

  it('FR155_danger_zone_stays_host_neutral_when_the_host_cannot_be_loaded', async () => {
    failState()
    server.use(http.get('*/api/projects/:id/settings', () => problem(502, 'UPSTREAM_ERROR', 'Boom.')))
    const project = seedProjectAs('owner', (p) => {
      seedAwaitingMerge(p)
    })
    await renderRoute(`/projects/${project.id}/settings/danger`)

    const reason = await screen.findByText(rollbackAwaitingMergeMessage(unknownGitHost))
    expect(reason).toHaveTextContent('on the Git host')
    expect(reason).toHaveTextContent('pull or merge request')
    expect(reason).not.toHaveTextContent('GitHub')
  })

  it('FR155_rollback_blocked_by_the_server_names_no_single_host', () => {
    expect(rollbackBlockedMessages.releasePartiallyApplied).not.toMatch(/GitHub/)
    expect(contextualErrorMessages.releaseSettings.CONFLICT).not.toMatch(/GitHub/)
  })
})
