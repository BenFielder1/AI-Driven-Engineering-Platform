import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { authDb, seedUser, signInAs } from '../../mocks/handlers/auth'
import { configureGitLabComOAuth, GITLAB_COM_ID, gitlabDb, seedGitLabInstance } from '../../mocks/handlers/gitlab'
import { currentHref, renderRoute } from '../../test/render'

function pendingLink() {
  gitlabDb.pendingLink = {
    instanceId: GITLAB_COM_ID,
    instanceName: 'GitLab.com',
    login: 'ada-gl',
    email: 'ada@example.com',
    expiresAt: '2099-01-01T00:00:00Z',
  }
}

describe('Sign in with GitLab (FR-131)', () => {
  it('FR131_continue_with_gitlab: one button per enabled instance with an OAuth application', async () => {
    configureGitLabComOAuth()
    const acme = seedGitLabInstance({ name: 'Acme GitLab' })
    seedGitLabInstance({ name: 'Token only', clientId: null, hasClientSecret: false, oauthConfigured: false })
    seedGitLabInstance({ name: 'Disabled GitLab', enabled: false })
    await renderRoute('/login?returnUrl=%2Faccount')

    const com = await screen.findByRole('link', { name: 'Continue with GitLab.com' })
    const href = new URL(com.getAttribute('href') ?? '', 'http://localhost')
    expect(href.pathname).toBe('/api/auth/gitlab/start')
    expect(Object.fromEntries(href.searchParams)).toEqual({ mode: 'signin', instanceId: GITLAB_COM_ID, returnUrl: '/account' })
    expect(screen.getByRole('link', { name: 'Continue with Acme GitLab' }).getAttribute('href')).toContain(acme.id)
    expect(screen.queryByRole('link', { name: 'Continue with Token only' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Continue with Disabled GitLab' })).not.toBeInTheDocument()
  })

  it('FR131_single_gitlab_com: reads "Continue with GitLab" when gitlab.com is the only instance', async () => {
    configureGitLabComOAuth()
    await renderRoute('/login')
    expect(await screen.findByRole('link', { name: 'Continue with GitLab' })).toBeInTheDocument()
  })

  it('FR131_no_instances: no GitLab button without an OAuth instance', async () => {
    await renderRoute('/login')
    await screen.findByRole('button', { name: 'Sign in' })
    expect(screen.queryByRole('link', { name: /Continue with GitLab/ })).not.toBeInTheDocument()
  })

  it('FR131_git_auth_failed: ?error=GIT_AUTH_FAILED&provider=gitlab shows the reason', async () => {
    await renderRoute('/login?error=GIT_AUTH_FAILED&reason=email_required&provider=gitlab')
    const text = await screen.findByText(/GitLab didn't share a verified email address/)
    expect(text.closest('[role="alert"]')).toHaveTextContent("Sign-in didn't complete")
  })

  it('FR131_git_auth_failed_busy: reason=busy asks to try again in a minute', async () => {
    await renderRoute('/login?error=GIT_AUTH_FAILED&reason=busy&provider=gitlab')
    expect(await screen.findByText('Too many sign-ins are in progress. Try again in a minute.')).toBeInTheDocument()
  })

  it('FR131_link_sign_in_then_confirm: signs in to the matching account, then confirms the link', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    pendingLink()
    const { router } = await renderRoute('/login?link=gitlab&returnUrl=%2Faccount')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Link your GitLab account' })
    expect(within(panel).getByTestId('gitlab-link-login')).toHaveTextContent('ada-gl')
    expect(panel).toHaveTextContent('ada@example.com')
    expect(screen.queryByRole('link', { name: /Continue with/ })).not.toBeInTheDocument()

    await user.type(screen.getByLabelText('Email'), 'ada@example.com')
    await user.type(screen.getByLabelText('Password'), 'password1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await user.click(await within(panel).findByRole('button', { name: 'Link GitLab account' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/account')
    })
    expect(gitlabDb.linkConfirmed).toBe(true)
  })

  it('FR131_link_signed_in_stays: a signed-in user on ?link=gitlab sees the confirmation, not a redirect', async () => {
    signInAs({ email: 'ada@example.com' })
    pendingLink()
    const { router } = await renderRoute('/login?link=gitlab')

    const panel = await screen.findByRole('region', { name: 'Link your GitLab account' })
    expect(panel).toHaveTextContent("You're signed in as ada@example.com")
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
  })

  it('FR131_link_wrong_account: FORBIDDEN explains to sign in to the matching account', async () => {
    signInAs({ email: 'other@example.com' })
    pendingLink()
    await renderRoute('/login?link=gitlab')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Link your GitLab account' })
    await user.click(within(panel).getByRole('button', { name: 'Link GitLab account' }))

    expect(await within(panel).findByText(/You're signed in to a different AIDE account/)).toBeInTheDocument()
    expect(gitlabDb.linkConfirmed).toBe(false)
  })

  it('FR131_link_cancel: cancelling discards the pending link', async () => {
    pendingLink()
    await renderRoute('/login?link=gitlab')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Link your GitLab account' })
    await user.click(within(panel).getByRole('button', { name: 'Cancel linking' }))

    await waitFor(() => {
      expect(screen.queryByRole('region', { name: 'Link your GitLab account' })).not.toBeInTheDocument()
    })
    expect(gitlabDb.pendingLink).toBeNull()
    expect(authDb.currentUserId).toBeNull()
  })

  it('FR131_link_expired: no pending link says so', async () => {
    await renderRoute('/login?link=gitlab')
    expect(await screen.findByText(/no longer waiting to be linked/)).toBeInTheDocument()
  })

  it('FR131_link_stale_signed_in: a signed-in user with no pending link can continue to the destination', async () => {
    signInAs({ email: 'ada@example.com' })
    const { router } = await renderRoute('/login?link=gitlab&returnUrl=%2Faccount')
    const user = userEvent.setup()

    expect(await screen.findByText(/no longer waiting to be linked/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/account')
    })
  })

  it('FR131_link_expires_before_sign_in: signing in after the link expired goes on to the destination', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    pendingLink()
    const { router } = await renderRoute('/login?link=gitlab&returnUrl=%2Faccount')
    const user = userEvent.setup()
    await screen.findByRole('region', { name: 'Link your GitLab account' })
    gitlabDb.pendingLink = null // expired (10 minutes) while the user typed

    await user.type(screen.getByLabelText('Email'), 'ada@example.com')
    await user.type(screen.getByLabelText('Password'), 'password1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/account')
    })
    expect(gitlabDb.linkConfirmed).toBe(false)
  })
})
