import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay, http } from 'msw'
import type { Metadata } from 'next'
import { afterEach, describe, expect, it } from 'vitest'

import { marketingPaths } from '../../lib/app-paths'
import { DOCS_URL } from '../../lib/site'
import { signInAs } from '../../mocks/handlers/auth'
import { server } from '../../mocks/server'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { currentHref, renderRoute } from '../../test/render'
import { marketingPages } from './content/pages'
import { GITHUB_URL } from './content/site-nav'

afterEach(() => {
  setTheme('light')
})

async function renderMarketing(path: string) {
  const result = await renderRoute(path)
  await screen.findByRole('heading', { level: 1 })
  return result
}

function header() {
  return screen.getByTestId('marketing-header')
}

describe('FR-210 marketing shell', () => {
  it.each(marketingPaths.map((p) => [p]))('FR210_single_h1: %s renders the shell and exactly one h1', async (path) => {
    await renderMarketing(path)

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute('href', '#main')
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main')
    expect(screen.getByTestId('marketing-footer')).toBeInTheDocument()
  })

  it('FR210_header_links: logo, Product, How it works, Integrations, Self-host, Docs ↗, Changelog, GitHub ↗, Sign in, Get started', async () => {
    await renderMarketing('/product')
    const bar = within(header())

    expect(bar.getByRole('link', { name: 'Tevrin home' })).toHaveAttribute('href', '/')
    const nav = within(bar.getByRole('navigation', { name: 'Primary' }))
    expect(nav.getAllByRole('link').map((a) => a.textContent)).toEqual([
      'Product',
      'How it works',
      'Integrations',
      'Self-host',
      'Docs ↗ (opens in a new tab)',
      'Changelog',
    ])
    expect(nav.getByRole('link', { name: 'Product' })).toHaveAttribute('aria-current', 'page')
    const docs = nav.getByRole('link', { name: /^Docs/ })
    expect(docs).toHaveAttribute('href', DOCS_URL)
    expect(docs).toHaveAttribute('target', '_blank')
    expect(docs).toHaveAttribute('rel', 'noopener noreferrer')
    expect(bar.getByRole('link', { name: /^GitHub/ })).toHaveAttribute('href', GITHUB_URL)
    expect(bar.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login')
    expect(bar.getByRole('link', { name: 'Get started' })).toHaveAttribute('href', '/register')
    expect(bar.queryByRole('link', { name: 'Open app' })).not.toBeInTheDocument()
  })

  it('FR210_signed_in: the header swaps Sign in and Get started for Open app (→ /home) once me resolves', async () => {
    signInAs({ name: 'Ada' })
    await renderMarketing('/product')

    expect(await within(header()).findByRole('link', { name: 'Open app' })).toHaveAttribute('href', '/home')
    expect(within(header()).queryByRole('link', { name: 'Sign in' })).not.toBeInTheDocument()
    expect(within(header()).queryByRole('link', { name: 'Get started' })).not.toBeInTheDocument()
  })

  it('FR210_get_started: Get started opens /register', async () => {
    const { router } = await renderMarketing('/faq')

    await userEvent.setup().click(within(header()).getByRole('link', { name: 'Get started' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Create your Tevrin account' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/register')
  })

  it('FR210_mobile_menu: the menu button opens a sheet with the same links; Escape closes it and focus returns', async () => {
    await renderMarketing('/')
    const user = userEvent.setup()
    const button = within(header()).getByRole('button', { name: 'Open menu' })

    await user.click(button)

    const sheet = await screen.findByRole('dialog', { name: 'Menu' })
    const links = within(within(sheet).getByRole('navigation', { name: 'Menu' })).getAllByRole('link')
    expect(links.map((a) => a.textContent.replace(' ↗ (opens in a new tab)', ''))).toEqual([
      'Product',
      'How it works',
      'Integrations',
      'Self-host',
      'Docs',
      'Changelog',
      'GitHub',
    ])
    expect(within(sheet).getByRole('link', { name: 'Sign in' })).toBeInTheDocument()
    expect(within(sheet).getByRole('link', { name: 'Get started' })).toBeInTheDocument()

    await user.keyboard('{Escape}')

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Menu' })).not.toBeInTheDocument()
    })
    expect(button).toHaveFocus()
  })

  it('FR210_mobile_menu_navigates: choosing a link in the sheet navigates and closes it', async () => {
    const { router } = await renderMarketing('/')
    const user = userEvent.setup()
    await user.click(within(header()).getByRole('button', { name: 'Open menu' }))

    await user.click(within(await screen.findByRole('dialog', { name: 'Menu' })).getByRole('link', { name: 'Changelog' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Changelog' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/changelog')
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Menu' })).not.toBeInTheDocument()
    })
  })

  it('FR210_footer / FR-204: footer columns link to the pages and docs; the bottom row has © year Tevrin and the theme selector', async () => {
    await renderMarketing('/security')
    const footer = within(screen.getByTestId('marketing-footer'))
    const nav = within(footer.getByRole('navigation', { name: 'Footer' }))

    expect(nav.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual([
      'Product',
      'Developers',
      'Self-host',
      'Trust',
    ])
    expect(nav.getByRole('link', { name: 'Features' })).toHaveAttribute('href', '/product')
    expect(nav.getByRole('link', { name: /^CLI/ })).toHaveAttribute('href', `${DOCS_URL}guides/cli/`)
    expect(nav.getByRole('link', { name: /^API reference/ })).toHaveAttribute('href', `${DOCS_URL}reference/api-and-contract/`)
    expect(nav.getByRole('link', { name: /^Install/ })).toHaveAttribute('href', `${DOCS_URL}getting-started/`)
    expect(nav.getByRole('link', { name: /^Requirements/ })).toHaveAttribute('href', `${DOCS_URL}getting-started/prerequisites/`)
    expect(nav.getByRole('link', { name: /^Upgrade/ })).toHaveAttribute('href', `${DOCS_URL}operations/postgres-18-upgrade/`)
    expect(nav.getByRole('link', { name: 'Security' })).toHaveAttribute('href', '/security')
    expect(nav.getByRole('link', { name: 'FAQ' })).toHaveAttribute('href', '/faq')
    expect(footer.getByText(/^©/)).toHaveTextContent(`© ${String(new Date().getFullYear())} Tevrin`)
    expect(footer.getByRole('group', { name: 'Theme' })).toBeInTheDocument()
  })

  it.each(THEMES.map((t) => [t]))('FR210_axe: the shell and a marketing page have no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderMarketing('/product')

    expect(await axeViolations(container)).toEqual([])
  })
})

describe('FR-211 root routing', () => {
  it('FR211_visitor_root: / renders the marketing Home for a visitor', async () => {
    const { router } = await renderMarketing('/')

    expect(screen.getByRole('heading', { level: 1, name: 'Ship software with AI agents, under human control.' })).toBeInTheDocument()
    expect(within(screen.getByTestId('home-hero')).getByRole('link', { name: 'Get started' })).toHaveAttribute('href', '/register')
    expect(currentHref(router)).toBe('/')
  })

  it('FR211_never_blocks: marketing content renders while the session check is still pending', async () => {
    server.use(http.get('*/api/auth/me', () => delay('infinite')))
    await renderRoute('/how-it-works')

    expect(await screen.findByRole('heading', { level: 1, name: 'How Tevrin takes an idea to a release' })).toBeInTheDocument()
    expect(within(header()).getByRole('link', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('FR211_signed_in_root: a signed-in user on / is redirected to /home', async () => {
    signInAs({ name: 'Ada' })
    const { router } = await renderRoute('/')

    expect(await screen.findByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/home')
  })

  it('FR211_other_routes_signed_in: /product stays put for a signed-in user', async () => {
    signInAs({ name: 'Ada' })
    const { router } = await renderMarketing('/product')

    await within(header()).findByRole('link', { name: 'Open app' })
    expect(currentHref(router)).toBe('/product')
  })
})

describe('Marketing 404 (SPEC §7)', () => {
  it('FR210_not_found: an unknown URL renders the marketing shell with links to Home and the docs', async () => {
    await renderRoute('/no-such-page')

    expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument()
    expect(screen.getByTestId('marketing-header')).toBeInTheDocument()
    const main = within(screen.getByRole('main'))
    expect(main.getByRole('link', { name: 'Go to the home page' })).toHaveAttribute('href', '/')
    expect(main.getByRole('link', { name: /^Read the docs/ })).toHaveAttribute('href', DOCS_URL)
  })
})

describe('FR-221 metadata', () => {
  // `(…)` is glob syntax, so list every layout and pick the marketing ones by key.
  const layouts = import.meta.glob<{ metadata?: Metadata }>('../../app/**/layout.tsx', { eager: true })

  function layoutFor(path: string) {
    const dir = path === '/' ? '(home)' : path.slice(1)
    return layouts[`../../app/(marketing)/${dir}/layout.tsx`]
  }

  it.each(marketingPaths.map((p) => [p]))('FR221_metadata: %s has a server layout with title, description, Open Graph and Twitter', (path) => {
    const metadata = layoutFor(path)?.metadata
    const page = marketingPages[path]

    expect(metadata, `no metadata for ${path}`).toBeDefined()
    expect(metadata?.title).toEqual({ absolute: page.title })
    expect(page.title).toBe(path === '/' ? 'Tevrin — Ship software with AI agents, under human control.' : `${page.name} · Tevrin`)
    expect(metadata?.description).toBe(page.description)
    expect(metadata?.openGraph).toMatchObject({ title: page.title, url: path, images: [expect.objectContaining({ url: '/og.png' })] })
    expect(metadata?.twitter).toMatchObject({ card: 'summary_large_image', images: ['/og.png'] })
  })
})
