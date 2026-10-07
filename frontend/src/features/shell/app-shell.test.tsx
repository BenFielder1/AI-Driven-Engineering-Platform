import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { authDb, seedUser, signInAs } from '../../mocks/handlers/auth'
import { unhealthyReady } from '../../mocks/handlers/health'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { authKeys } from '../auth/auth-api'

describe('App shell', () => {
  it('FR-3: admins see the Admin nav link and their role in the user menu', async () => {
    signInAs({ systemRole: 'admin', name: 'Ada Admin' })
    renderRoute('/projects')
    const user = userEvent.setup()

    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(nav).getByRole('link', { name: 'Projects' })).toHaveAttribute('aria-current', 'page')
    expect(within(nav).getByRole('link', { name: 'Notifications' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Admin' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /Account menu for/ }))
    expect(screen.getByText('Role: Admin')).toBeInTheDocument()
  })

  it('FR-3: non-admins do not see the Admin nav link', async () => {
    seedUser({ systemRole: 'admin', email: 'root@example.com' })
    signInAs({ systemRole: 'user', email: 'bob@example.com', name: 'Bob' })
    renderRoute('/projects')
    const user = userEvent.setup()

    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(nav).queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /Account menu for/ }))
    expect(screen.getByText('Role: User')).toBeInTheDocument()
  })

  it('FR-3: /admin shows a forbidden state to non-admins', async () => {
    signInAs({ systemRole: 'user' })
    renderRoute('/admin')

    expect(
      await screen.findByRole('heading', { level: 1, name: "You don't have access to this page" }),
    ).toBeInTheDocument()
  })

  it('FR-1: Sign out ends the session, clears cached data and returns to /login', async () => {
    signInAs()
    const { queryClient, router } = renderRoute('/account')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Account' })
    expect(queryClient.getQueryData(['me', 'tokens', 'list'])).toBeDefined()

    await user.click(screen.getByRole('button', { name: /Account menu for/ }))
    await user.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to AIDE' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(authDb.currentUserId).toBeNull()
    expect(queryClient.getQueryData(['me', 'tokens', 'list'])).toBeUndefined()
    expect(queryClient.getQueryData(authKeys.me())).toBeNull()
  })

  it('NFR-A11Y: has a skip link to the main landmark and closes the user menu with Escape', async () => {
    signInAs()
    renderRoute('/projects')
    const user = userEvent.setup()

    await screen.findByRole('heading', { level: 1, name: 'Projects' })
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute('href', '#main')
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main')

    const menuButton = screen.getByRole('button', { name: /Account menu for/ })
    await user.click(menuButton)
    expect(menuButton).toHaveAttribute('aria-expanded', 'true')
    await user.keyboard('{Escape}')
    expect(menuButton).toHaveAttribute('aria-expanded', 'false')
    expect(menuButton).toHaveFocus()
  })

  it('theme toggle applies and persists the choice', async () => {
    signInAs()
    renderRoute('/projects')
    const user = userEvent.setup()

    await user.selectOptions(await screen.findByLabelText('Theme'), 'dark')
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('aide.theme')).toBe('dark')

    await user.selectOptions(screen.getByLabelText('Theme'), 'light')
    expect(document.documentElement).not.toHaveClass('dark')

    await user.selectOptions(screen.getByLabelText('Theme'), 'system')
    expect(localStorage.getItem('aide.theme')).toBeNull()
  })

  it('NFR-OBS: the footer shows API readiness', async () => {
    signInAs()
    server.use(
      http.get('*/api/health/ready', () =>
        HttpResponse.json<Schemas['HealthResponse']>(unhealthyReady, { status: 503 }),
      ),
    )
    renderRoute('/projects')

    await waitFor(() => {
      expect(screen.getByText(/API status:/)).toHaveTextContent('API status: Unhealthy')
    })
  })
})
