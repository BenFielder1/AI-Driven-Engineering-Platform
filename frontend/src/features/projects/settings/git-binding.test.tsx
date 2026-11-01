import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../../lib/errors'
import { seedUser } from '../../../mocks/handlers/auth'
import { GITLAB_COM_ID, gitlabDb, seedGitLabConnection } from '../../../mocks/handlers/gitlab'
import { problem } from '../../../mocks/handlers/problem'
import { gitlabGitSettings, instanceRef, seedProject } from '../../../mocks/handlers/projects'
import { server } from '../../../mocks/server'
import { openSettings, requestsTo, signInAsOwner } from '../../../test/project-settings'
import { bindingClearedMessage } from './git-binding'

const REPO_ID = '5123'

/**
 * A GitLab project on gitlab.com bound to Ana's connection (`bound: false`: the binding was cleared), opened by its
 * Owner Ada, who has her own gitlab.com connection.
 */
async function openGitLabGit({ bound = true } = {}) {
  const ana = seedUser({ email: 'ana@example.com', name: 'Ana', systemRole: 'user' })
  const anaConnection = seedGitLabConnection(ana.id, { login: 'ana' })
  const ada = signInAsOwner()
  const adaConnection = seedGitLabConnection(ada.id, { login: 'ada' })
  const project = seedProject({
    git: gitlabGitSettings({
      instance: instanceRef(GITLAB_COM_ID),
      repoId: REPO_ID,
      repoFullName: 'acme/todo-app',
      binding: bound
        ? { connectionId: anaConnection.id, login: 'ana', status: 'valid', boundBy: { id: ana.id, name: 'Ana' } }
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

describe('Settings → Git for a GitLab project (FR-136)', () => {
  it('FR136_binding_shown: host, instance, repository link and "connected by Ana" with its status', async () => {
    await openGitLabGit()

    const summary = connectionSummary()
    expect(within(summary).getByTestId('git-mode')).toHaveTextContent('GitLab')
    expect(within(summary).getByTestId('git-instance')).toHaveTextContent('GitLab.com (gitlab.com)')
    expect(within(summary).getByRole('link', { name: /acme\/todo-app/ })).toHaveAttribute('href', 'https://gitlab.com/acme/todo-app')
    const binding = within(summary).getByTestId('git-binding')
    expect(binding).toHaveTextContent('GitLab · connected by Ana (@ana)')
    expect(binding).toHaveTextContent('Valid')
    expect(screen.queryByText('No GitLab connection is bound')).not.toBeInTheDocument()
  })

  it('FR136_rebind: an Owner binds their own connection on the instance; the summary shows it', async () => {
    const { user, adaConnection } = await openGitLabGit()

    const rebind = screen.getByRole('region', { name: 'Rebind' })
    const select = await within(rebind).findByLabelText('Your GitLab connection')
    expect(select).toHaveValue(adaConnection.id)
    // Another user's connection is never offered.
    expect(within(rebind).queryByRole('option', { name: /^ana on/ })).not.toBeInTheDocument()

    await user.click(within(rebind).getByRole('button', { name: 'Rebind' }))

    expect(await screen.findByText("The project now uses @ada's GitLab connection.")).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/git').at(-1)?.body).toEqual({ connectionId: adaConnection.id })
    await waitFor(() => {
      expect(within(connectionSummary()).getByTestId('git-binding')).toHaveTextContent(
        'GitLab · connected by Ada Lovelace (@ada)',
      )
    })
  })

  it('FR136_rebind_forbidden: a connection without Developer access is refused with the reason', async () => {
    gitlabDb.noDeveloperAccess = [REPO_ID]
    const { user } = await openGitLabGit()
    const rebind = screen.getByRole('region', { name: 'Rebind' })
    await within(rebind).findByLabelText('Your GitLab connection')

    await user.click(within(rebind).getByRole('button', { name: 'Rebind' }))

    expect(await within(rebind).findByLabelText('Your GitLab connection')).toHaveAccessibleDescription(
      contextualErrorMessages.gitBinding.FORBIDDEN,
    )
    expect(within(connectionSummary()).getByTestId('git-binding')).toHaveTextContent('connected by Ana (@ana)')
  })

  it.each(['CREDENTIAL_NOT_OWNER', 'GIT_NOT_CONNECTED'] as const)(
    'FR136_rebind_%s: the refusal is explained on the connection',
    async (code) => {
      server.use(
        http.patch('*/api/projects/:id/settings/git', () =>
          problem(code === 'GIT_NOT_CONNECTED' ? 409 : 403, code, 'The binding was refused.'),
        ),
      )
      const { user } = await openGitLabGit()
      const rebind = screen.getByRole('region', { name: 'Rebind' })
      await within(rebind).findByLabelText('Your GitLab connection')

      await user.click(within(rebind).getByRole('button', { name: 'Rebind' }))

      expect(await within(rebind).findByLabelText('Your GitLab connection')).toHaveAccessibleDescription(
        contextualErrorMessages.gitBinding[code],
      )
      if (code === 'GIT_NOT_CONNECTED') {
        expect(within(rebind).getByRole('link', { name: 'Go to Connections' })).toHaveAttribute('href', '/connections')
      }
    },
  )

  it('FR136_binding_cleared: the cleared state says Git work stops until an Owner re-binds, and Bind connection restores it', async () => {
    const { user, adaConnection } = await openGitLabGit({ bound: false })

    const alert = screen.getByText('No GitLab connection is bound').closest('[role="status"]')
    expect(alert).not.toBeNull()
    expect(alert).toHaveTextContent(bindingClearedMessage('GitLab'))
    expect(within(connectionSummary()).getByTestId('git-binding')).toHaveTextContent('Not bound')

    const section = screen.getByRole('region', { name: 'Re-bind the GitLab connection' })
    await within(section).findByLabelText('Your GitLab connection')
    await user.click(within(section).getByRole('button', { name: 'Bind connection' }))

    await waitFor(() => {
      expect(within(connectionSummary()).getByTestId('git-binding')).toHaveTextContent('connected by Ada Lovelace (@ada)')
    })
    expect(requestsTo('PATCH', '/settings/git').at(-1)?.body).toEqual({ connectionId: adaConnection.id })
    expect(screen.queryByText('No GitLab connection is bound')).not.toBeInTheDocument()
  })

  it('FR136_rebind_no_connection: without an own connection on the instance, Rebind links to Connections', async () => {
    const { user } = await openGitLabGit()
    // Ada's only connection is removed (another tab).
    gitlabDb.connections = Object.fromEntries(
      Object.entries(gitlabDb.connections).map(([userId, list]) => [userId, list.filter((c) => c.login !== 'ada')]),
    )
    await user.click(screen.getByRole('link', { name: 'General' }))
    await user.click(await screen.findByRole('link', { name: 'Git' }))
    await screen.findByRole('heading', { name: 'Git repository' })

    const rebind = screen.getByRole('region', { name: 'Rebind' })
    expect(await within(rebind).findByText(/You have no other usable GitLab connection on GitLab.com/)).toBeInTheDocument()
    expect(within(rebind).getByRole('link', { name: 'Go to Connections' })).toHaveAttribute('href', '/connections')
  })
})
