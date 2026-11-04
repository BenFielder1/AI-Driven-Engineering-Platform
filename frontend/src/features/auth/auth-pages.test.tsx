import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay, http } from 'msw'
import { afterEach, describe, expect, it } from 'vitest'

import { authDb } from '../../mocks/handlers/auth'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { currentHref, renderRoute } from '../../test/render'

afterEach(() => {
  setTheme('light')
})

/** `a` comes before `b` in document order. */
function precedes(a: Element, b: Element): boolean {
  return (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0
}

describe('FR-223 sign in', () => {
  it('FR223_login_layout: logo → /, h1, providers above the "or" divider and the email form, and the sign-up link', async () => {
    authDb.githubMode = 'oauth'
    await renderRoute('/login?returnUrl=%2Fprojects%2Fnew')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to Tevrin' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Tevrin home' })).toHaveAttribute('href', '/')
    expect(screen.getByTestId('auth-glow')).toHaveAttribute('aria-hidden', 'true')
    const github = await screen.findByRole('link', { name: 'Continue with GitHub' })
    const email = screen.getByLabelText('Email')
    const divider = screen.getByText('or')
    expect(precedes(github, divider)).toBe(true)
    expect(precedes(divider, email)).toBe(true)
    expect(screen.getByText(/Don.t have an account\?/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Create one' })).toHaveAttribute('href', '/register?returnUrl=%2Fprojects%2Fnew')
  })

  it('FR223_autocomplete: email and password allow password managers and paste (WCAG 3.3.8)', async () => {
    await renderRoute('/login')

    expect(await screen.findByLabelText('Email')).toHaveAttribute('autocomplete', 'email')
    const password = screen.getByLabelText('Password')
    expect(password).toHaveAttribute('autocomplete', 'current-password')
    expect(password).not.toHaveAttribute('onpaste')
  })

  it('FR223_no_providers: with none configured there are no provider buttons and no divider', async () => {
    authDb.githubMode = 'none'
    await renderRoute('/login')

    await screen.findByRole('button', { name: 'Sign in' })
    await waitFor(() => {
      expect(screen.queryByTestId('social-sign-in-pending')).not.toBeInTheDocument()
    })
    expect(screen.queryByText('or')).not.toBeInTheDocument()
  })

  it('FR223_providers_pending: while the providers load, one button height and the divider are reserved (no layout jump)', async () => {
    server.use(http.get('*/api/auth/providers', () => delay('infinite')))
    await renderRoute('/login')

    await screen.findByRole('button', { name: 'Sign in' })
    const pending = screen.getByTestId('social-sign-in-pending')
    expect(precedes(pending, screen.getByLabelText('Email'))).toBe(true)
    expect(within(pending).getByText('or')).toBeInTheDocument()
  })

  it.each(THEMES.map((t) => [t]))('FR223_login_axe: /login has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    authDb.githubMode = 'oauth'
    const { container } = await renderRoute('/login')
    await screen.findByRole('link', { name: 'Continue with GitHub' })

    expect(await axeViolations(container)).toEqual([])
  })
})

describe('FR-223 sign up', () => {
  it('FR223_register_layout: the intro panel (lg and up) has the tagline, three bullets and a lifecycle diagram with a text equivalent', async () => {
    authDb.githubMode = 'app'
    await renderRoute('/register')

    expect(await screen.findByRole('heading', { level: 1, name: 'Create your Tevrin account' })).toBeInTheDocument()
    const intro = screen.getByTestId('register-intro')
    expect(intro).toHaveClass('hidden', 'lg:flex')
    const panel = within(intro)
    expect(panel.getByText('Ship software with AI agents, under human control.')).toBeInTheDocument()
    expect(panel.getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      expect.stringMatching(/^Approval gates/),
      expect.stringMatching(/^Versioned, content-hashed artifacts/),
      expect.stringMatching(/^Rollback/),
    ])
    expect(intro.querySelector('svg[aria-hidden="true"] text')).not.toBeNull()
    expect(panel.getByText(/^The Tevrin lifecycle: Refinement, Specification, Planning, Execution, Review, Release/)).toHaveClass('sr-only')

    const github = await screen.findByRole('link', { name: 'Continue with GitHub' })
    expect(precedes(github, screen.getByLabelText('Email'))).toBe(true)
    expect(screen.getByLabelText(/^Password/)).toHaveAttribute('autocomplete', 'new-password')
    expect(screen.getByText(/Already have an account\?/)).toBeInTheDocument()
  })

  it('FR223_register_return_url: /register?returnUrl=/projects/new lands on /projects/new after sign-up; Sign in keeps it', async () => {
    seedProject({ name: 'Inventory tracker' })
    const { router } = await renderRoute('/register?returnUrl=%2Fprojects%2Fnew')
    expect(await screen.findByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login?returnUrl=%2Fprojects%2Fnew')

    const user = userEvent.setup()
    await user.type(screen.getByLabelText('Email'), 'a@x.com')
    await user.type(screen.getByLabelText('Name'), 'Ada')
    await user.type(screen.getByLabelText(/^Password/), 'password1')
    await user.click(screen.getByRole('button', { name: 'Create account' }))

    await screen.findByRole('heading', { level: 1, name: /new project/i })
    expect(currentHref(router)).toBe('/projects/new')
  })

  it.each(THEMES.map((t) => [t]))('FR223_register_axe: /register has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    authDb.githubMode = 'oauth'
    const { container } = await renderRoute('/register')
    await screen.findByRole('link', { name: 'Continue with GitHub' })

    expect(await axeViolations(container)).toEqual([])
  })
})

describe('FR-210 / FR-208: one h1 on the auth pages', () => {
  it.each([['/login'], ['/register']])('FR210_single_h1: %s renders exactly one h1', async (path) => {
    await renderRoute(path)
    await screen.findByRole('heading', { level: 1 })

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })
})
