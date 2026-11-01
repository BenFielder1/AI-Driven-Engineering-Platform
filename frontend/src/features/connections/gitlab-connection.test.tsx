import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { toast } from 'sonner'
import { describe, expect, it, vi } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import {
  configureGitLabComOAuth,
  GITLAB_COM_ID,
  gitlabDb,
  seedGitLabConnection,
  seedGitLabInstance,
} from '../../mocks/handlers/gitlab'
import { renderRoute } from '../../test/render'

async function openSection(path = '/connections') {
  await renderRoute(path)
  const section = await screen.findByRole('region', { name: 'GitLab' })
  await within(section).findByRole('region', { name: /GitLab\.com/ })
  return { section, user: userEvent.setup() }
}

describe('Connections → GitLab (FR-131)', () => {
  it('FR131_oauth_connect: each OAuth instance links to the API start with mode=connect, its id and a return URL', async () => {
    signInAs()
    configureGitLabComOAuth()
    const acme = seedGitLabInstance({ name: 'Acme GitLab' })
    const { section } = await openSection()

    const com = within(section).getByRole('link', { name: 'Connect with GitLab (GitLab.com)' })
    const href = new URL(com.getAttribute('href') ?? '', 'http://localhost')
    expect(href.pathname).toBe('/api/auth/gitlab/start')
    expect(Object.fromEntries(href.searchParams)).toEqual({
      mode: 'connect',
      instanceId: GITLAB_COM_ID,
      returnUrl: '/connections?gitlab=connected',
    })
    const acmeLink = within(section).getByRole('link', { name: 'Connect with GitLab (Acme GitLab)' })
    expect(acmeLink.getAttribute('href')).toContain(`instanceId=${acme.id}`)
  })

  it('FR131_token_only: an instance without an OAuth application offers only the token fallback', async () => {
    signInAs()
    const { section } = await openSection()

    const com = within(section).getByRole('region', { name: /GitLab\.com/ })
    expect(within(com).queryByRole('link', { name: /Connect with/ })).not.toBeInTheDocument()
    expect(within(com).getByRole('button', { name: 'Use an access token (GitLab.com)' })).toBeInTheDocument()
  })

  it('FR131_token_fallback: explains the token, links to the token page, and saves a valid token', async () => {
    signInAs()
    const { section, user } = await openSection()

    await user.click(within(section).getByRole('button', { name: 'Use an access token (GitLab.com)' }))
    const dialog = await screen.findByRole('dialog', { name: 'Connect GitLab with an access token' })
    expect(dialog).toHaveTextContent('personal access token')
    expect(dialog).toHaveTextContent('api and write_repository')
    expect(dialog).toHaveTextContent('warns you 14 days before')
    expect(within(dialog).getByRole('link', { name: /Create a token on gitlab\.com/ })).toHaveAttribute(
      'href',
      'https://gitlab.com/-/user_settings/personal_access_tokens?name=AIDE&scopes=api,write_repository',
    )

    await user.type(within(dialog).getByLabelText('Access token'), 'glpat-good')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Connect GitLab with an access token' })).not.toBeInTheDocument()
    })
    expect(gitlabDb.tokenRequests).toEqual([{ instanceId: GITLAB_COM_ID, token: 'glpat-good' }])
    const row = await within(section).findByRole('listitem', { name: 'ada on GitLab.com' })
    expect(row).toHaveTextContent('Access token')
    expect(within(row).getByTestId('gitlab-scopes')).toHaveTextContent('api, write_repository')
  })

  it('FR131_token_scope_insufficient: names the missing scopes on the token field and clears it', async () => {
    signInAs()
    const { section, user } = await openSection()

    await user.click(within(section).getByRole('button', { name: 'Use an access token (GitLab.com)' }))
    const dialog = await screen.findByRole('dialog', { name: 'Connect GitLab with an access token' })
    await user.type(within(dialog).getByLabelText('Access token'), 'readonly-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    expect(await within(dialog).findAllByText('This token is missing permissions: api, write_repository.')).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('Access token')).toHaveValue('')
  })

  it('FR131_token_invalid: CREDENTIAL_INVALID saves nothing', async () => {
    signInAs()
    const { section, user } = await openSection()

    await user.click(within(section).getByRole('button', { name: 'Use an access token (GitLab.com)' }))
    const dialog = await screen.findByRole('dialog', { name: 'Connect GitLab with an access token' })
    await user.type(within(dialog).getByLabelText('Access token'), 'bad-token')
    await user.click(within(dialog).getByRole('button', { name: 'Connect' }))

    expect(await within(dialog).findAllByText(/GitLab rejected this token, so it wasn't saved/)).not.toHaveLength(0)
  })

  it('FR131_list: shows login, instance, auth kind, the 14-day expiry warning and an invalid status', async () => {
    const user = signInAs()
    configureGitLabComOAuth()
    seedGitLabConnection(user.id, {
      login: 'ada-token',
      authKind: 'token',
      expiresAt: '2099-01-05T00:00:00Z',
      expiryWarning: true,
    })
    seedGitLabConnection(user.id, { login: 'ada-old', status: 'invalid' })
    const { section } = await openSection()

    const tokenRow = await within(section).findByRole('listitem', { name: 'ada-token on GitLab.com' })
    expect(tokenRow).toHaveTextContent('GitLab.com (gitlab.com) · Access token')
    expect(within(tokenRow).getByTestId('gitlab-expiry-warning')).toBeInTheDocument()

    const invalidRow = within(section).getByRole('listitem', { name: 'ada-old on GitLab.com' })
    expect(within(invalidRow).getByTestId('gitlab-connection-status')).toHaveTextContent('Invalid')
    expect(within(invalidRow).getByRole('link', { name: 'Reconnect' })).toHaveAttribute(
      'href',
      expect.stringContaining('mode=connect') as string,
    )
  })

  it('FR131_disabled_instance: a connection on a disabled instance says so', async () => {
    const user = signInAs()
    const lab = seedGitLabInstance({ name: 'Lab GitLab' })
    seedGitLabConnection(user.id, { instanceId: lab.id, login: 'ada-lab' })
    lab.enabled = false
    gitlabDb.connections[user.id]?.forEach((c) => {
      c.instanceEnabled = false
    })
    const { section } = await openSection()

    const disabled = await within(section).findByRole('list', { name: 'Connections on disabled instances' })
    expect(within(disabled).getByTestId('gitlab-instance-disabled')).toHaveTextContent('GitLab instance disabled')
  })

  it('FR131_test_and_delete: tests a connection, then deletes it after confirmation', async () => {
    const me = signInAs()
    seedGitLabConnection(me.id, { login: 'ada' })
    const { section, user } = await openSection()

    await user.click(await within(section).findByRole('button', { name: 'Test ada on GitLab.com' }))
    expect(await screen.findByText('ada on GitLab.com works.')).toBeInTheDocument()

    await user.click(within(section).getByRole('button', { name: 'Delete ada on GitLab.com' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete ada on GitLab.com?' })
    await user.click(within(confirm).getByRole('button', { name: 'Delete connection' }))

    await waitFor(() => {
      expect(within(section).queryByRole('listitem', { name: 'ada on GitLab.com' })).not.toBeInTheDocument()
    })
  })

  it('FR131_callback_failed: ?error=GIT_AUTH_FAILED&provider=gitlab shows the reason', async () => {
    signInAs()
    const { section } = await openSection('/connections?error=GIT_AUTH_FAILED&reason=access_denied&provider=gitlab')
    expect(within(section).getByRole('alert')).toHaveTextContent('GitLab authorization was cancelled.')
  })
  it('FR131_connect_failed_real_url: a failure appended to the connected returnUrl shows only the error', async () => {
    signInAs()
    const success = vi.spyOn(toast, 'success')
    await renderRoute('/connections?gitlab=connected&error=GIT_AUTH_FAILED&reason=access_denied&provider=gitlab')

    expect(await screen.findByText('GitLab authorization was cancelled.')).toBeInTheDocument()
    expect(success).not.toHaveBeenCalledWith('GitLab connected.')
  })
})
