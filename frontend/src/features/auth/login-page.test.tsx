import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { authDb, seedUser } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { currentHref, renderRoute } from '../../test/render'

async function fillAndSubmit(email: string, password: string) {
  const user = userEvent.setup()
  await user.type(await screen.findByLabelText('Email'), email)
  await user.type(screen.getByLabelText('Password'), password)
  await user.click(screen.getByRole('button', { name: 'Sign in' }))
}

describe('LoginPage', () => {
  it('FR-1: signs in with email and password and lands on the returnUrl', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    const { router } = renderRoute('/login?returnUrl=%2Faccount')

    await fillAndSubmit('ada@example.com', 'password1')

    expect(await screen.findByRole('heading', { level: 1, name: 'Account' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/account')
  })

  it('FR-1: without a returnUrl, signing in lands on /projects', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    const { router } = renderRoute('/login')

    await fillAndSubmit('ada@example.com', 'password1')

    expect(await screen.findByRole('heading', { level: 1, name: 'Projects' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/projects')
  })

  it('FR-1: ignores an off-site returnUrl', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    const { router } = renderRoute('/login?returnUrl=%2F%2Fevil.example.com')

    await fillAndSubmit('ada@example.com', 'password1')

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/projects')
    })
  })

  it('FR-1: INVALID_CREDENTIALS shows the error in an announced summary', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    renderRoute('/login')

    await fillAndSubmit('ada@example.com', 'wrong-password1')

    const alert = await screen.findByText('The email or password is incorrect.')
    expect(alert.closest('[role="alert"]')).not.toBeNull()
    expect(screen.getByLabelText('Email')).toHaveValue('ada@example.com')
  })

  it('NFR-SEC: RATE_LIMITED shows the Retry-After wait', async () => {
    server.use(
      http.post('*/api/auth/login', () =>
        problem(429, 'RATE_LIMITED', 'Too many attempts.', { headers: { 'Retry-After': '42' } }),
      ),
    )
    renderRoute('/login')

    await fillAndSubmit('ada@example.com', 'password1')

    expect(await screen.findByText('Too many attempts. Try again in 42 seconds.')).toBeInTheDocument()
  })

  it('FR-1: validates required fields before calling the API', async () => {
    renderRoute('/login')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Sign in' }))

    expect(await screen.findAllByText('Enter your email address.')).toHaveLength(2) // field + summary
    expect(screen.getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('link', { name: 'Enter your password.' })).toHaveAttribute('href', '#login-password')
  })

  it('FR80_login_button_shown_for_the_github_app: githubMode app shows "Continue with GitHub"', async () => {
    authDb.githubMode = 'app'
    renderRoute('/login')

    expect(await screen.findByRole('link', { name: 'Continue with GitHub' })).toHaveAttribute(
      'href',
      '/api/auth/github?returnUrl=%2Fprojects',
    )
  })

  it('FR80_register_button_follows_githubMode: register shows it for app and hides it for none', async () => {
    authDb.githubMode = 'app'
    const first = renderRoute('/register')
    expect(await screen.findByRole('link', { name: 'Continue with GitHub' })).toBeInTheDocument()
    first.unmount()

    authDb.githubMode = 'none'
    renderRoute('/register')
    await screen.findByRole('button', { name: /Create account/ })
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Continue with GitHub' })).not.toBeInTheDocument()
    })
  })

  it('FR80_login_button_hidden_when_githubMode_none: hides "Continue with GitHub" when GitHub is not configured', async () => {
    authDb.githubMode = 'none'
    renderRoute('/login')

    await screen.findByRole('button', { name: 'Sign in' })
    // Let the providers query settle.
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Continue with GitHub' })).not.toBeInTheDocument()
    })
  })

  it('FR-2: shows "Continue with GitHub" as a full-page link carrying the returnUrl', async () => {
    authDb.githubMode = 'oauth'
    renderRoute('/login?returnUrl=%2Faccount')

    const link = await screen.findByRole('link', { name: 'Continue with GitHub' })
    expect(link).toHaveAttribute('href', '/api/auth/github?returnUrl=%2Faccount')
  })

  it('FR-2: explains a GitHub failure from ?error=GITHUB_AUTH_FAILED&reason=…', async () => {
    renderRoute('/login?error=GITHUB_AUTH_FAILED&reason=link_required')

    expect(await screen.findByText(/Sign in with your password, then link GitHub/)).toBeInTheDocument()
    expect(screen.getByText('GitHub sign-in failed')).toBeInTheDocument()
  })

  it('FR-2: unknown GitHub failure reasons fall back to generic copy', async () => {
    renderRoute('/login?error=GITHUB_AUTH_FAILED&reason=something_new')

    expect(await screen.findByText("GitHub sign-in didn't complete. Try again.")).toBeInTheDocument()
  })
})
