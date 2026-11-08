// FR-197: copy that names the product says Tevrin (it said AIDE before 0.6.0); the assertions below use the new copy.
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { currentHref, renderRoute } from '../../test/render'

describe('Route guards (FR-1)', () => {
  it('FR-1: a signed-out visitor to /account is sent to /login with a returnUrl', async () => {
    const { router } = await renderRoute('/account')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to Tevrin' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/login?returnUrl=%2Faccount')
  })

  it('FR-211: a signed-out visitor to / gets the marketing Home, not the sign-in page (it redirected before 0.6.0)', async () => {
    const { router } = await renderRoute('/')

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Ship software with AI agents, under human control.' }),
    ).toBeInTheDocument()
    expect(currentHref(router)).toBe('/')
  })

  it('FR-207 / FR-211: a signed-in user visiting / lands on Home (/home; it was /projects before 0.6.0)', async () => {
    signInAs({ name: 'Ada' })
    const { router } = await renderRoute('/')

    expect(await screen.findByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/home')
  })

  it('FR-1 / FR-207: a signed-in user visiting /login goes to Home (/home; it was /projects before 0.6.0)', async () => {
    signInAs({ name: 'Ada' })
    const { router } = await renderRoute('/login')

    expect(await screen.findByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/home')
  })

  it('FR-1 / FR-207: a signed-in user visiting /register goes to /home', async () => {
    signInAs()
    const { router } = await renderRoute('/register')

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/home')
    })
  })

  it('FR-2: a GitHub link failure while signed in is shown on the account page', async () => {
    signInAs()
    const { router } = await renderRoute('/login?error=GITHUB_AUTH_FAILED&reason=already_linked')

    expect(
      await screen.findByText('This GitHub account is already linked to another Tevrin user.'),
    ).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/account')
  })

  it('FR-97: a signed-out deep link keeps path and search in returnUrl and lands back on it after sign-in', async () => {
    seedUser({ email: 'ada@example.com', password: 'password1' })
    const { router } = await renderRoute('/projects/abc/review?cycle=2')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to Tevrin' })).toBeInTheDocument()
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
