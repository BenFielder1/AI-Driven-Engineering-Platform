import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { authDb, signInAs } from '../../mocks/handlers/auth'
import { BITBUCKET_CALLBACK_URL, bitbucketDb, configureBitbucketConsumer } from '../../mocks/handlers/bitbucket'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

function signInAdmin() {
  return signInAs({ email: 'admin@example.com', name: 'Ada Admin', systemRole: 'admin' })
}

async function openPanel() {
  await renderRoute('/admin/integrations')
  const panel = await screen.findByRole('region', { name: 'Bitbucket' })
  await within(panel).findByTestId('bitbucket-callback-url')
  return { panel, user: userEvent.setup() }
}

describe('Admin → Integrations → Bitbucket (FR-133)', () => {
  it('FR133_registration_help: shows the callback URL, the consumer permissions and that app passwords are unsupported', async () => {
    signInAdmin()
    const { panel } = await openPanel()

    expect(within(panel).getByTestId('bitbucket-callback-url')).toHaveTextContent(BITBUCKET_CALLBACK_URL)
    const permissions = within(panel).getByRole('list', { name: 'Consumer permissions' })
    expect(within(permissions).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'account',
      'email',
      'repository:write',
      'repository:admin',
      'pullrequest:write',
      'project',
      'webhook',
    ])
    expect(panel).toHaveTextContent('App passwords')
    expect(panel).toHaveTextContent('Atlassian has removed them')
    expect(within(panel).getByTestId('bitbucket-consumer-status')).toHaveTextContent('Not set up')
    expect(within(panel).queryByRole('button', { name: 'Remove consumer' })).not.toBeInTheDocument()
  })

  it('FR133_save_consumer: saves the key and secret, then the secret is write-only and sign-in turns on', async () => {
    signInAdmin()
    const { panel, user } = await openPanel()

    await user.type(within(panel).getByLabelText('Key'), 'bb-key-1')
    await user.click(within(panel).getByRole('button', { name: 'Save consumer' }))
    expect(await within(panel).findAllByText("Enter the consumer's Secret.")).not.toHaveLength(0)
    expect(bitbucketDb.consumerUpdates).toEqual([])

    await user.type(within(panel).getByLabelText('Secret'), 's3cret')
    await user.click(within(panel).getByRole('button', { name: 'Save consumer' }))

    await waitFor(() => {
      expect(within(panel).getByTestId('bitbucket-consumer-status')).toHaveTextContent('Set up')
    })
    expect(bitbucketDb.consumerUpdates).toEqual([{ clientId: 'bb-key-1', clientSecret: 's3cret' }])
    expect(authDb.bitbucketSignIn).toBe(true)
    expect(within(panel).getByLabelText('Secret')).toHaveValue('')
    expect(within(panel).getByText(/A secret is saved\. Leave this empty to keep it/)).toBeInTheDocument()
  })

  it('FR133_keep_secret: changing only the key keeps the saved secret (no clientSecret sent)', async () => {
    signInAdmin()
    configureBitbucketConsumer()
    const { panel, user } = await openPanel()

    const key = within(panel).getByLabelText('Key')
    await user.clear(key)
    await user.type(key, 'bb-key-2')
    await user.click(within(panel).getByRole('button', { name: 'Save consumer' }))

    await waitFor(() => {
      expect(bitbucketDb.consumerUpdates).toEqual([{ clientId: 'bb-key-2' }])
    })
  })

  it('FR133_remove_consumer: Remove asks first, then clears the consumer', async () => {
    signInAdmin()
    configureBitbucketConsumer()
    const { panel, user } = await openPanel()

    await user.click(within(panel).getByRole('button', { name: 'Remove consumer' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove the Bitbucket consumer?' })
    expect(dialog).toHaveTextContent('API-token connections keep working')
    await user.click(within(dialog).getByRole('button', { name: 'Remove consumer' }))

    await waitFor(() => {
      expect(within(panel).getByTestId('bitbucket-consumer-status')).toHaveTextContent('Not set up')
    })
    expect(authDb.bitbucketSignIn).toBe(false)
  })

  it('FR133_config_managed: a consumer from Bitbucket__OAuth__* is read-only, with no form or Remove', async () => {
    signInAdmin()
    configureBitbucketConsumer({ source: 'configuration', clientId: 'from-env' })
    const { panel } = await openPanel()

    expect(within(panel).getByTestId('bitbucket-config-managed')).toHaveTextContent('Bitbucket__OAuth__ClientId')
    expect(panel).toHaveTextContent('from-env')
    expect(within(panel).queryByRole('form', { name: 'Bitbucket OAuth consumer' })).not.toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Remove consumer' })).not.toBeInTheDocument()
  })

  it('FR133_conflict: a 409 CONFLICT on save explains the consumer is set in configuration', async () => {
    signInAdmin()
    server.use(
      http.put('*/api/admin/integrations/bitbucket', () => problem(409, 'CONFLICT', 'The consumer is configured in the environment.')),
    )
    const { panel, user } = await openPanel()

    await user.type(within(panel).getByLabelText('Key'), 'bb-key')
    await user.type(within(panel).getByLabelText('Secret'), 's3cret')
    await user.click(within(panel).getByRole('button', { name: 'Save consumer' }))

    expect(await within(panel).findByText(/set in the server's configuration \(Bitbucket__OAuth__\*\)/)).toBeInTheDocument()
    expect(within(panel).getByLabelText('Secret')).toHaveValue('')
  })
})
