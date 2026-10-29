import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../../lib/errors'
import { seedUser } from '../../../mocks/handlers/auth'
import { bitbucketDb, seedBitbucketConnection } from '../../../mocks/handlers/bitbucket'
import { bitbucketGitSettings, gitSettings, seedProject } from '../../../mocks/handlers/projects'
import { openSettings, requestsTo, signInAsOwner } from '../../../test/project-settings'
import { mergeStrategiesFor, mergeStrategyLabel } from '../labels'
import { gitHostTerms } from '../../../lib/git-host'

const REPO_ID = '{b0b0-todo}'

/** A Bitbucket project bound to Ana's connection, opened by its Owner Ada, who has her own Bitbucket connection. */
async function openBitbucketGit({ bound = true } = {}) {
  const ana = seedUser({ email: 'ana@example.com', name: 'Ana', systemRole: 'user' })
  const anaConnection = seedBitbucketConnection(ana.id, { login: 'ana-bb' })
  const ada = signInAsOwner()
  const adaConnection = seedBitbucketConnection(ada.id, { login: 'ada-bb', authKind: 'token' })
  const project = seedProject({
    git: bitbucketGitSettings({
      repoId: REPO_ID,
      repoFullName: 'acme/todo-app',
      binding: bound
        ? { connectionId: anaConnection.id, login: 'ana-bb', status: 'valid', boundBy: { id: ana.id, name: 'Ana' } }
        : null,
    }),
    members: [
      { userId: ada.id, role: 'owner' },
      { userId: ana.id, role: 'owner' },
    ],
  })
  const utils = await openSettings('git', project)
  await screen.findByRole('heading', { name: 'Git repository' })
  return { ...utils, adaConnection, anaConnection }
}

const connectionSummary = () => screen.getByRole('region', { name: 'Connection' })

describe('Settings → Git for a Bitbucket project (FR-136)', () => {
  it('FR136_bitbucket_binding_shown: Bitbucket Cloud, the repository link and "connected by Ana"', async () => {
    await openBitbucketGit()

    const summary = connectionSummary()
    expect(within(summary).getByTestId('git-mode')).toHaveTextContent('Bitbucket')
    expect(within(summary).getByTestId('git-instance')).toHaveTextContent('Bitbucket Cloud (bitbucket.org)')
    expect(within(summary).getByRole('link', { name: /acme\/todo-app/ })).toHaveAttribute(
      'href',
      'https://bitbucket.org/acme/todo-app',
    )
    const binding = within(summary).getByTestId('git-binding')
    expect(binding).toHaveTextContent('Bitbucket · connected by Ana (@ana-bb)')
    expect(binding).toHaveTextContent('Clones, pushes, pull requests and releases')
  })

  it('FR136_bitbucket_rebind: an Owner binds their own Bitbucket connection; the summary shows it', async () => {
    const { user, adaConnection } = await openBitbucketGit()

    const rebind = screen.getByRole('region', { name: 'Rebind' })
    const select = await within(rebind).findByLabelText('Your Bitbucket connection')
    expect(select).toHaveValue(adaConnection.id)
    expect(within(rebind).getByRole('option', { name: 'ada-bb (API token)' })).toBeInTheDocument()
    // Another user's connection is never offered.
    expect(within(rebind).queryByRole('option', { name: /^ana-bb/ })).not.toBeInTheDocument()

    await user.click(within(rebind).getByRole('button', { name: 'Rebind' }))

    expect(await screen.findByText("The project now uses @ada-bb's Bitbucket connection.")).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/git').at(-1)?.body).toEqual({ connectionId: adaConnection.id })
    await waitFor(() => {
      expect(within(connectionSummary()).getByTestId('git-binding')).toHaveTextContent(
        'Bitbucket · connected by Ada Lovelace (@ada-bb)',
      )
    })
  })

  it('FR136_bitbucket_rebind_forbidden: a connection without write access is refused with the reason', async () => {
    const { user } = await openBitbucketGit()
    bitbucketDb.noWriteAccess = [REPO_ID]

    const rebind = screen.getByRole('region', { name: 'Rebind' })
    await within(rebind).findByLabelText('Your Bitbucket connection')
    await user.click(within(rebind).getByRole('button', { name: 'Rebind' }))

    await waitFor(() => {
      expect(within(rebind).getByLabelText('Your Bitbucket connection')).toHaveAccessibleDescription(
        expect.stringContaining(contextualErrorMessages.gitBindingBitbucket.FORBIDDEN),
      )
    })
  })

  it('FR136_bitbucket_binding_cleared: the warning names Bitbucket and Re-bind is offered', async () => {
    await openBitbucketGit({ bound: false })

    expect(await screen.findByText('No Bitbucket connection is bound')).toBeInTheDocument()
    expect(screen.getByText(/pushes, pull requests and releases fail/)).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Re-bind the Bitbucket connection' })).toBeInTheDocument()
  })
})

describe('Merge strategy per host (FR-140)', () => {
  it('FR140_bitbucket_merge_strategies: fast-forward and squash are offered on Bitbucket and saved', async () => {
    const { user } = await openBitbucketGit()

    const group = await screen.findByRole('group', { name: 'Merge strategy' })
    expect(within(group).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual([
      'aide_merges',
      'fast_forward',
      'squash',
      'manual',
    ])
    await user.click(within(group).getByRole('radio', { name: 'AIDE merges (squash)' }))
    expect(within(group).getByRole('radio', { name: 'AIDE merges (fast-forward)' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Save release settings' }))

    await waitFor(() => {
      expect(requestsTo('PATCH', '/settings/git').at(-1)?.body).toEqual({ mergeStrategy: 'squash' })
    })
    await waitFor(() => {
      expect(within(connectionSummary()).getByTestId('git-merge-strategy')).toHaveTextContent('AIDE merges (squash)')
    })
  })

  it('FR140_merge_strategies_github_only_two: GitHub and GitLab projects offer only AIDE merges and manual', async () => {
    signInAsOwner()
    const project = seedProject({ git: gitSettings({ provider: 'github_app' }) })
    await openSettings('git', project)

    const group = await screen.findByRole('group', { name: 'Merge strategy' })
    expect(within(group).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual(['aide_merges', 'manual'])
    expect(screen.queryByText('AIDE merges (squash)')).not.toBeInTheDocument()

    expect(mergeStrategiesFor('gitlab')).toEqual(['aide_merges', 'manual'])
    expect(mergeStrategyLabel('fast_forward', gitHostTerms('bitbucket'))).toBe('AIDE merges (fast-forward)')
  })
})
