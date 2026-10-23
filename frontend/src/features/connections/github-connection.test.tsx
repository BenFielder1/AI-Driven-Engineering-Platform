import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { configureGitHubApp, connectGitHub, githubDb, INSTALL_URL } from '../../mocks/handlers/github'
import { currentHref, renderRoute } from '../../test/render'

async function openConnections(path = '/connections') {
  await renderRoute(path)
  await screen.findByRole('heading', { level: 1, name: 'Connections' })
  return { section: screen.getByRole('region', { name: 'GitHub' }), user: userEvent.setup() }
}

describe('/connections → GitHub (FR-81)', () => {
  it('FR81_not_configured: without a registered App, it explains that an Admin must register it', async () => {
    signInAs({ systemRole: 'user' })
    const { section } = await openConnections()

    expect(await within(section).findByText("GitHub isn't set up yet")).toBeInTheDocument()
    expect(section).toHaveTextContent('an Admin must register it first under Admin → Integrations → GitHub')
    expect(within(section).queryByRole('link', { name: 'Connect GitHub' })).not.toBeInTheDocument()
    expect(within(section).queryByRole('link', { name: 'Register the GitHub App' })).not.toBeInTheDocument()
  })

  it('FR81_not_configured_admin: an Admin gets a link to register the App', async () => {
    signInAs({ systemRole: 'admin' })
    const { section } = await openConnections()
    expect(await within(section).findByRole('link', { name: 'Register the GitHub App' })).toHaveAttribute('href', '/admin/integrations')
  })

  it('FR81_not_connected: offers Connect GitHub as a full-page link back to /connections', async () => {
    signInAs({ systemRole: 'user' })
    configureGitHubApp()
    const { section } = await openConnections()

    expect(await within(section).findByText('Not connected')).toBeInTheDocument()
    expect(within(section).getByRole('link', { name: 'Connect GitHub' })).toHaveAttribute(
      'href',
      '/api/me/connections/github/connect?returnUrl=%2Fconnections',
    )
  })

  it('FR81_connected: shows the login, status, installations with manage links and the install link; never a token', async () => {
    const me = signInAs({ systemRole: 'user' })
    connectGitHub(me.id, { login: 'ada' })
    githubDb.installations.push({
      id: 9002,
      account: { login: 'acme', type: 'organization' },
      repositorySelection: 'all',
      manageUrl: 'https://github.com/organizations/acme/settings/installations/9002',
    })
    const { section } = await openConnections()

    expect(await within(section).findByTestId('github-login')).toHaveTextContent('ada')
    expect(within(section).getByTestId('github-connection-status')).toHaveTextContent('Connected')
    const list = within(section).getByRole('list', { name: 'GitHub App installations' })
    const acme = within(list).getByRole('listitem', { name: 'acme' })
    expect(acme).toHaveTextContent('acme (organisation) · All repositories')
    expect(within(acme).getByRole('link', { name: /Manage acme/ })).toHaveAttribute(
      'href',
      'https://github.com/organizations/acme/settings/installations/9002',
    )
    expect(within(list).getByRole('listitem', { name: 'ada' })).toHaveTextContent('Selected repositories')
    expect(within(section).getByRole('link', { name: /Install AIDE on more repositories/ })).toHaveAttribute('href', INSTALL_URL)
  })

  it('FR81_disconnect: Disconnect asks for confirmation, then shows Not connected', async () => {
    const me = signInAs({ systemRole: 'user' })
    connectGitHub(me.id)
    const { section, user } = await openConnections()

    await user.click(await within(section).findByRole('button', { name: 'Disconnect' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Disconnect GitHub?' })
    await user.click(within(dialog).getByRole('button', { name: 'Disconnect' }))

    expect(await screen.findByText('GitHub disconnected.')).toBeInTheDocument()
    expect(await within(section).findByText('Not connected')).toBeInTheDocument()
    expect(githubDb.connections[me.id]).toBeUndefined()
  })

  it('FR81_invalid_connection: an invalid connection offers Reconnect', async () => {
    const me = signInAs({ systemRole: 'user' })
    connectGitHub(me.id, { status: 'invalid' })
    const { section } = await openConnections()

    await waitFor(() => {
      expect(within(section).getByTestId('github-connection-status')).toHaveTextContent('Needs reconnecting')
    })
    expect(within(section).getByRole('link', { name: 'Reconnect GitHub' })).toHaveAttribute(
      'href',
      '/api/me/connections/github/connect?returnUrl=%2Fconnections',
    )
  })

  it('FR81_connect_failed: ?error=GITHUB_AUTH_FAILED explains the failure', async () => {
    signInAs({ systemRole: 'user' })
    configureGitHubApp()
    const { section } = await openConnections('/connections?error=GITHUB_AUTH_FAILED&reason=already_linked')
    expect(await within(section).findByText('This GitHub account is already linked to another AIDE user.')).toBeInTheDocument()
  })

  it('FR81_app_installed: the setup URL /account/connections?github=installed keeps the param, toasts and shows the installations', async () => {
    const me = signInAs({ systemRole: 'user' })
    connectGitHub(me.id, { login: 'ada' })
    const renderResult = await renderRoute('/account/connections?github=installed')
    await screen.findByRole('heading', { level: 1, name: 'Connections' })

    expect(await screen.findByText('AIDE installed on GitHub')).toBeInTheDocument()
    expect(currentHref(renderResult.router)).toBe('/connections?github=installed')
    const section = screen.getByRole('region', { name: 'GitHub' })
    expect(await within(section).findByRole('listitem', { name: 'ada' })).toBeInTheDocument()
  })

  it('FR81_connect_cancelled: reason=access_denied says the authorization was cancelled', async () => {
    signInAs({ systemRole: 'user' })
    configureGitHubApp()
    const { section } = await openConnections('/connections?error=GITHUB_AUTH_FAILED&reason=access_denied')
    expect(await within(section).findByText('GitHub authorization was cancelled.')).toBeInTheDocument()
  })
})
