import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { bitbucketDb, configureBitbucketConsumer } from '../../mocks/handlers/bitbucket'
import { currentHref, renderRoute } from '../../test/render'

function pendingLink() {
  bitbucketDb.pendingLink = { login: 'ada-bb', email: 'ada@example.com', expiresAt: '2099-01-01T00:00:00Z' }
}

describe('Sign in with Bitbucket (FR-134)', () => {
  it('FR134_continue_with_bitbucket: shown when the consumer is configured, linking to the API sign-in start', async () => {
    configureBitbucketConsumer()
    await renderRoute('/login?returnUrl=%2Faccount')

    const link = await screen.findByRole('link', { name: 'Continue with Bitbucket' })
    const href = new URL(link.getAttribute('href') ?? '', 'http://localhost')
    expect(href.pathname).toBe('/api/auth/bitbucket/start')
    expect(Object.fromEntries(href.searchParams)).toEqual({ mode: 'signin', returnUrl: '/account' })
  })

  it('FR134_no_consumer: no Bitbucket button without a consumer', async () => {
    await renderRoute('/login')
    await screen.findByRole('button', { name: 'Sign in' })
    expect(screen.queryByRole('link', { name: 'Continue with Bitbucket' })).not.toBeInTheDocument()
  })

  it('FR134_git_auth_failed: ?error=GIT_AUTH_FAILED&provider=bitbucket names Bitbucket', async () => {
    await renderRoute('/login?error=GIT_AUTH_FAILED&reason=email_required&provider=bitbucket')
    const text = await screen.findByText(/Bitbucket didn't share a verified email address/)
    expect(text.closest('[role="alert"]')).toHaveTextContent("Sign-in didn't complete")
  })

  it('FR134_git_auth_failed_busy: reason=busy asks to try again in a minute', async () => {
    await renderRoute('/login?error=GIT_AUTH_FAILED&reason=busy&provider=bitbucket')
    expect(await screen.findByText('Too many sign-ins are in progress. Try again in a minute.')).toBeInTheDocument()
  })

  it('FR134_connect_failed_signed_in: a failure while signed in shows on Connections → Bitbucket', async () => {
    signInAs()
    const { router } = await renderRoute('/login?error=GIT_AUTH_FAILED&reason=access_denied&provider=bitbucket')

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/connections')
    })
    const section = await screen.findByRole('region', { name: 'Bitbucket' })
    expect(await within(section).findByText('Bitbucket authorization was cancelled.')).toBeInTheDocument()
  })

  it('FR134_consumer_removed: reason=instance_unavailable explains the consumer is gone', async () => {
    await renderRoute('/login?error=GIT_AUTH_FAILED&reason=instance_unavailable&provider=bitbucket')
    expect(await screen.findByText(/Bitbucket sign-in isn't set up here any more/)).toBeInTheDocument()
  })

  it('FR134_link_sign_in_then_confirm: signs in to the matching account, then confirms the link', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    pendingLink()
    const { router } = await renderRoute('/login?link=bitbucket&returnUrl=%2Faccount')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Link your Bitbucket account' })
    expect(within(panel).getByTestId('bitbucket-link-login')).toHaveTextContent('ada-bb')
    expect(panel).toHaveTextContent('ada@example.com')
    expect(screen.queryByRole('link', { name: /Continue with/ })).not.toBeInTheDocument()

    await user.type(screen.getByLabelText('Email'), 'ada@example.com')
    await user.type(screen.getByLabelText('Password'), 'password1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await user.click(await within(panel).findByRole('button', { name: 'Link Bitbucket account' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/account')
    })
    expect(bitbucketDb.linkConfirmed).toBe(true)
  })

  it('FR134_link_signed_in_stays: a signed-in user on ?link=bitbucket sees the confirmation, not a redirect', async () => {
    signInAs({ email: 'ada@example.com' })
    pendingLink()
    const { router } = await renderRoute('/login?link=bitbucket')

    const panel = await screen.findByRole('region', { name: 'Link your Bitbucket account' })
    expect(panel).toHaveTextContent("You're signed in as ada@example.com")
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
  })

  it('FR134_link_wrong_account: FORBIDDEN explains to sign in to the matching account', async () => {
    signInAs({ email: 'other@example.com' })
    pendingLink()
    await renderRoute('/login?link=bitbucket')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Link your Bitbucket account' })
    await user.click(within(panel).getByRole('button', { name: 'Link Bitbucket account' }))

    expect(await within(panel).findByText(/this Bitbucket email to link it/)).toBeInTheDocument()
    expect(bitbucketDb.linkConfirmed).toBe(false)
  })

  it('FR134_link_cancel: cancelling discards the pending link', async () => {
    pendingLink()
    await renderRoute('/login?link=bitbucket')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Link your Bitbucket account' })
    await user.click(within(panel).getByRole('button', { name: 'Cancel linking' }))

    await waitFor(() => {
      expect(screen.queryByRole('region', { name: 'Link your Bitbucket account' })).not.toBeInTheDocument()
    })
    expect(bitbucketDb.pendingLink).toBeNull()
  })

  it('FR134_link_stale_signed_in: a signed-in user with no pending link sees a status and can continue to the destination', async () => {
    signInAs({ email: 'ada@example.com' })
    const { router } = await renderRoute('/login?link=bitbucket&returnUrl=%2Faccount')
    const user = userEvent.setup()

    const text = await screen.findByText(/This Bitbucket sign-in is no longer waiting to be linked/)
    expect(text.closest('[role="status"]')).not.toBeNull()
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/account')
    })
  })

  it('FR134_link_expires_before_sign_in: signing in after the link expired goes on to the destination', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    pendingLink()
    const { router } = await renderRoute('/login?link=bitbucket&returnUrl=%2Faccount')
    const user = userEvent.setup()
    await screen.findByRole('region', { name: 'Link your Bitbucket account' })
    bitbucketDb.pendingLink = null // expired (10 minutes) while the user typed

    await user.type(screen.getByLabelText('Email'), 'ada@example.com')
    await user.type(screen.getByLabelText('Password'), 'password1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/account')
    })
    expect(bitbucketDb.linkConfirmed).toBe(false)
  })
})
