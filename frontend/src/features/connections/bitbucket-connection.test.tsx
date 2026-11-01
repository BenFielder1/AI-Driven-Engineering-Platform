import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { toast } from 'sonner'
import { describe, expect, it, vi } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import {
  BITBUCKET_MOCK_TOKEN_SCOPES,
  bitbucketDb,
  configureBitbucketConsumer,
  seedBitbucketConnection,
} from '../../mocks/handlers/bitbucket'
import { renderRoute } from '../../test/render'

async function openSection(path = '/connections') {
  await renderRoute(path)
  const section = await screen.findByRole('region', { name: 'Bitbucket' })
  return { section, user: userEvent.setup() }
}

async function openTokenDialog() {
  const { section, user } = await openSection()
  await user.click(within(section).getByRole('button', { name: 'Use an API token' }))
  const dialog = await screen.findByRole('dialog', { name: 'Connect Bitbucket with an API token' })
  return { section, user, dialog }
}

describe('Connections → Bitbucket (FR-134)', () => {
  it('FR134_oauth_connect: with the consumer configured, Connect links to the API start with mode=connect', async () => {
    signInAs()
    configureBitbucketConsumer()
    const { section } = await openSection()

    const link = await within(section).findByRole('link', { name: 'Connect with Bitbucket' })
    const href = new URL(link.getAttribute('href') ?? '', 'http://localhost')
    expect(href.pathname).toBe('/api/auth/bitbucket/start')
    expect(Object.fromEntries(href.searchParams)).toEqual({ mode: 'connect', returnUrl: '/connections?bitbucket=connected' })
  })

  it('FR134_token_only: without a consumer only the API token is offered, and it says why', async () => {
    signInAs()
    const { section } = await openSection()

    expect(await within(section).findByText(/hasn't set up the Bitbucket OAuth consumer/)).toBeInTheDocument()
    expect(within(section).queryByRole('link', { name: 'Connect with Bitbucket' })).not.toBeInTheDocument()
    expect(within(section).getByRole('button', { name: 'Use an API token' })).toBeInTheDocument()
    expect(within(section).getByText('Not connected.')).toBeInTheDocument()
  })

  it('FR134_token_fallback: lists the nine scopes, expiry advice and the Atlassian page, then saves email and token', async () => {
    signInAs()
    const { section, user, dialog } = await openTokenDialog()

    const scopes = within(dialog).getByRole('list', { name: 'Required scopes' })
    expect(within(scopes).getAllByRole('listitem').map((li) => li.textContent)).toEqual(BITBUCKET_MOCK_TOKEN_SCOPES)
    expect(dialog).toHaveTextContent('App passwords don')
    expect(dialog).toHaveTextContent('Expiry:')
    expect(within(dialog).getByRole('link', { name: /Create a token on Atlassian/ })).toHaveAttribute(
      'href',
      'https://id.atlassian.com/manage-profile/security/api-tokens',
    )

    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'ATATT-good')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Connect Bitbucket with an API token' })).not.toBeInTheDocument()
    })
    expect(bitbucketDb.tokenRequests).toEqual([{ email: 'ada@example.com', token: 'ATATT-good' }])
    const row = await within(section).findByRole('listitem', { name: 'ada on Bitbucket' })
    expect(row).toHaveTextContent('Atlassian API token')
    expect(within(row).getByTestId('bitbucket-scopes')).toHaveTextContent('write:pullrequest:bitbucket')
    expect(within(row).getByTestId('bitbucket-connection-status')).toHaveTextContent('Valid')
  })

  it('FR134_scope_insufficient: a token without write:pullrequest lists it, clears the token and saves nothing', async () => {
    const me = signInAs()
    const { user, dialog } = await openTokenDialog()

    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'nopr-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    expect(
      await within(dialog).findAllByText('This token is missing permissions: write:pullrequest:bitbucket.'),
    ).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('API token')).toHaveValue('')
    expect(within(dialog).getByLabelText('API token')).toHaveAttribute('aria-invalid', 'true')
    expect(bitbucketDb.connections[me.id] ?? []).toEqual([])
  })

  it('FR134_scope_unverifiable: lists the scopes AIDE could not confirm, with what to do instead, and saves nothing', async () => {
    const me = signInAs()
    configureBitbucketConsumer()
    const { user, dialog } = await openTokenDialog()

    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'unverif-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    const [message] = await within(dialog).findAllByText(
      /AIDE couldn't confirm these permissions: write:repository:bitbucket, admin:repository:bitbucket\./,
    )
    expect(message).toHaveTextContent('Connect with Bitbucket (OAuth) instead, or create a repository')
    expect(within(dialog).getByLabelText('API token')).toHaveValue('')
    expect(bitbucketDb.connections[me.id] ?? []).toEqual([])
  })

  it('FR134_scope_unverifiable_no_consumer: without a consumer the advice does not suggest OAuth', async () => {
    signInAs()
    const { user, dialog } = await openTokenDialog()

    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'unverif-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    const [message] = await within(dialog).findAllByText(/AIDE couldn't confirm these permissions/)
    expect(message).toHaveTextContent('Create a repository in the workspace first')
    expect(message).not.toHaveTextContent('OAuth')
  })

  it('FR134_connected_toast: ?bitbucket=connected announces the connection', async () => {
    signInAs()
    await openSection('/connections?bitbucket=connected')
    expect(await screen.findByText('Bitbucket connected.')).toBeInTheDocument()
  })

  it('FR134_connect_failed_real_url: a failure appended to the connected returnUrl shows only the error', async () => {
    signInAs()
    const success = vi.spyOn(toast, 'success')
    await openSection('/connections?bitbucket=connected&error=GIT_AUTH_FAILED&reason=access_denied&provider=bitbucket')

    expect(await screen.findByText('Bitbucket authorization was cancelled.')).toBeInTheDocument()
    expect(success).not.toHaveBeenCalledWith('Bitbucket connected.')
  })

  it('FR134_credential_invalid: a rejected email and token explain what to check', async () => {
    signInAs()
    const { user, dialog } = await openTokenDialog()

    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'bad-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    expect(await within(dialog).findAllByText(/Bitbucket rejected this email and token/)).not.toHaveLength(0)
  })

  it('FR134_rate_limited: UPSTREAM_RATE_LIMITED names Bitbucket and the Retry-After wait', async () => {
    signInAs()
    const { user, dialog } = await openTokenDialog()

    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'busy-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    expect(
      await within(dialog).findAllByText('Bitbucket is rate limiting requests. Try again in 5 minutes.'),
    ).not.toHaveLength(0)
  })

  it('FR134_email_required: the email is checked before anything is sent', async () => {
    signInAs()
    const { user, dialog } = await openTokenDialog()

    await user.type(within(dialog).getByLabelText('API token'), 'ATATT-good')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    expect(await within(dialog).findAllByText('Enter the email address of your Atlassian account.')).not.toHaveLength(0)
    expect(bitbucketDb.tokenRequests).toEqual([])
  })

  it('FR134_list_test_delete: shows status, tests and deletes a connection', async () => {
    const me = signInAs()
    configureBitbucketConsumer()
    seedBitbucketConnection(me.id, { login: 'old', status: 'invalid' })
    const { section, user } = await openSection()

    const row = await within(section).findByRole('listitem', { name: 'old on Bitbucket' })
    expect(within(row).getByTestId('bitbucket-connection-status')).toHaveTextContent('Invalid')
    expect(within(row).getByRole('link', { name: 'Reconnect' })).toHaveAttribute(
      'href',
      '/api/auth/bitbucket/start?mode=connect&returnUrl=%2Fconnections%3Fbitbucket%3Dconnected',
    )

    await user.click(within(row).getByRole('button', { name: 'Test old on Bitbucket' }))
    expect(await screen.findByText('old on Bitbucket: invalid.')).toBeInTheDocument()

    await user.click(within(row).getByRole('button', { name: 'Delete old on Bitbucket' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete old on Bitbucket?' })
    await user.click(within(confirm).getByRole('button', { name: 'Delete connection' }))

    await waitFor(() => {
      expect(within(section).queryByRole('listitem', { name: 'old on Bitbucket' })).not.toBeInTheDocument()
    })
    expect(bitbucketDb.connections[me.id]).toEqual([])
  })

  it('FR134_connect_failed: GIT_AUTH_FAILED with provider=bitbucket shows the reason in the Bitbucket section', async () => {
    signInAs()
    await openSection('/connections?error=GIT_AUTH_FAILED&reason=access_denied&provider=bitbucket')

    expect(await screen.findByText('Bitbucket authorization was cancelled.')).toBeInTheDocument()
    expect(screen.getByText('Bitbucket authorization was cancelled.').closest('[role="alert"]')).toHaveTextContent(
      "Bitbucket didn't connect",
    )
  })
})
