import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { currentHref, renderRoute } from '../../test/render'

describe('Route guards (FR-1)', () => {
  it('FR-1: a signed-out visitor to /account is sent to /login with a returnUrl', async () => {
    const { router } = await renderRoute('/account')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to AIDE' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/login?returnUrl=%2Faccount')
  })

  it('FR-1: / redirects to /projects, then to /login when signed out', async () => {
    const { router } = await renderRoute('/')

    await screen.findByRole('heading', { level: 1, name: 'Sign in to AIDE' })
    expect(currentHref(router)).toBe('/login?returnUrl=%2Fprojects')
  })

  it('FR-1: a signed-in user visiting /login goes to /projects', async () => {
    signInAs()
    const { router } = await renderRoute('/login')

    expect(await screen.findByRole('heading', { level: 1, name: 'Projects' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/projects')
  })

  it('FR-1: a signed-in user visiting /register goes to /projects', async () => {
    signInAs()
    const { router } = await renderRoute('/register')

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/projects')
    })
  })

  it('FR-2: a GitHub link failure while signed in is shown on the account page', async () => {
    signInAs()
    const { router } = await renderRoute('/login?error=GITHUB_AUTH_FAILED&reason=already_linked')

    expect(
      await screen.findByText('This GitHub account is already linked to another AIDE user.'),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/account')
  })

  it('FR-97: a signed-out deep link keeps path and search in returnUrl and lands back on it after sign-in', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    const { router } = await renderRoute('/projects/abc/review?cycle=2')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to AIDE' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/login?returnUrl=%2Fprojects%2Fabc%2Freview%3Fcycle%3D2')

    const user = userEvent.setup()
    await user.type(screen.getByLabelText('Email'), 'ada@example.com')
    await user.type(screen.getByLabelText('Password'), 'password1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/projects/abc/review?cycle=2')
    })
  })

  it('shows a not-found page for unknown routes', async () => {
    await renderRoute('/does-not-exist')

    expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument()
  })
})
