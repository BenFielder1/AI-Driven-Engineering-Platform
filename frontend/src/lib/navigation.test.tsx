import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { z } from 'zod'

import { renderWithProviders } from '../test/render'
import {
  buildHref,
  interpolatePath,
  isActiveHref,
  Link,
  matchPath,
  Navigate,
  parseSearch,
  stringifySearch,
  useLocation,
  useNavigate,
  useSearch,
  useSetSearch,
} from './navigation'

const at = (pathname: string, search: Record<string, string> = {}) => ({ pathname, search })

describe('FR-94 navigation adapter: pure helpers', () => {
  it('FR-94: stringifies search like TanStack (undefined dropped, %20 for spaces, order kept)', () => {
    expect(stringifySearch({ search: 'bob @', page: 2, flag: true, none: undefined, nil: null })).toBe('?search=bob%20%40&page=2&flag=true')
    expect(stringifySearch({})).toBe('')
    expect(parseSearch('?a=1&b=x%20y')).toEqual({ a: '1', b: 'x y' })
  })

  it('FR-94: interpolates $param and [param] patterns and rejects missing params', () => {
    expect(interpolatePath('/projects/$projectId/settings/$section', { projectId: 'p 1', section: 'git' })).toBe('/projects/p%201/settings/git')
    expect(interpolatePath('/projects/[projectId]', { projectId: 'abc' })).toBe('/projects/abc')
    expect(() => interpolatePath('/projects/$projectId')).toThrow(/projectId/)
  })

  it('FR-94: builds hrefs from to/params/search/hash, the current path, updaters and `search: true`', () => {
    const here = at('/admin/runs', { status: 'failed', page: '2' })
    expect(buildHref({ to: '/projects/$projectId/runs', params: { projectId: 'a' }, search: { run: 'r1' } }, here)).toBe('/projects/a/runs?run=r1')
    expect(buildHref({ search: { status: 'ok' } }, here)).toBe('/admin/runs?status=ok')
    expect(buildHref({ to: '.', search: (prev) => ({ ...prev, page: undefined }) }, here)).toBe('/admin/runs?status=failed')
    expect(buildHref({ search: true }, here)).toBe('/admin/runs?status=failed&page=2')
    expect(buildHref({ to: '/projects/$projectId', params: { projectId: 'a' }, hash: 'pending-gate-1' }, here)).toBe('/projects/a#pending-gate-1')
    expect(buildHref({ href: '/login?returnUrl=%2Faccount' }, here)).toBe('/login?returnUrl=%2Faccount')
  })

  it('FR-62: ?release= is kept across project pages unless the link sets it (retainSearchParams parity)', () => {
    const here = at('/projects/a/review', { release: '1.1.0', cycle: '2' })
    expect(buildHref({ to: '/projects/$projectId/runs', params: { projectId: 'a' } }, here)).toBe('/projects/a/runs?release=1.1.0')
    expect(buildHref({ to: '/projects/$projectId', params: { projectId: 'b' }, search: { release: undefined } }, here)).toBe('/projects/b')
    expect(buildHref({ href: '/projects/a/execution' }, here)).toBe('/projects/a/execution?release=1.1.0')
    expect(buildHref({ to: '/projects' }, here)).toBe('/projects')
    expect(buildHref({ to: '/projects/new' }, here)).toBe('/projects/new')
  })

  it('FR-94: matches paths exactly or fuzzily, with decoded params', () => {
    expect(matchPath('/projects/$projectId', '/projects/a%20b')).toEqual({ projectId: 'a b' })
    expect(matchPath('/projects/$projectId', '/projects/a/runs')).toBeNull()
    expect(matchPath('/projects/$projectId', '/projects/a/runs', { fuzzy: true })).toEqual({ projectId: 'a' })
  })

  it('FR-94: active links follow TanStack (prefix by default, exact on request, search as a subset)', () => {
    expect(isActiveHref(at('/projects/a/runs'), '/projects')).toBe(true)
    expect(isActiveHref(at('/projects/a/runs'), '/projects', { exact: true })).toBe(false)
    expect(isActiveHref(at('/projectsx'), '/projects')).toBe(false)
    expect(isActiveHref(at('/notifications', { unread: 'true' }), '/notifications?unread=true')).toBe(true)
    expect(isActiveHref(at('/notifications'), '/notifications?unread=true')).toBe(false)
    expect(isActiveHref(at('/notifications'), '/notifications?unread=true', { includeSearch: false })).toBe(true)
  })

  it('FR-94: includeHash makes a link active only when the hash matches too (TanStack semantics)', () => {
    const withHash = (hash: string) => ({ ...at('/projects/a'), hash })
    expect(isActiveHref(withHash(''), '/projects/a#stage-gates', { exact: true })).toBe(true)
    expect(isActiveHref(withHash(''), '/projects/a#stage-gates', { exact: true, includeHash: true })).toBe(false)
    expect(isActiveHref(withHash('#stage-gates'), '/projects/a#stage-gates', { exact: true, includeHash: true })).toBe(true)
    expect(isActiveHref(withHash('stage-gates'), '/projects/a#stage-gates', { includeHash: true })).toBe(true)
    expect(isActiveHref(withHash('#other'), '/projects/a#stage-gates', { includeHash: true })).toBe(false)
    expect(isActiveHref(withHash('#other'), '/projects/a', { includeHash: true })).toBe(false)
    expect(isActiveHref(withHash(''), '/projects/a', { includeHash: true })).toBe(true)
  })

  it('FR-94: a Link with includeHash becomes active once the URL carries its hash', async () => {
    const { router } = renderWithProviders(
      <Link to="/projects/$projectId" params={{ projectId: 'a' }} hash="stage-gates" activeOptions={{ exact: true, includeHash: true }}>
        Review the gate
      </Link>,
      { url: '/projects/a' },
    )
    const link = screen.getByRole('link', { name: 'Review the gate' })
    expect(link).toHaveAttribute('href', '/projects/a#stage-gates')
    expect(link).not.toHaveAttribute('aria-current')

    await userEvent.setup().click(link)

    expect(router.state.location.hash).toBe('#stage-gates')
    await waitFor(() => {
      expect(screen.getByRole('link', { name: 'Review the gate' })).toHaveAttribute('aria-current', 'page')
    })
  })
})

function Probe() {
  const location = useLocation()
  return <output data-testid="href">{location.href}</output>
}

describe('FR-94 navigation adapter: components and hooks', () => {
  it('FR-94: Link renders the href, marks the active link and navigates on click', async () => {
    const { router } = renderWithProviders(
      <>
        <Link to="/admin" activeOptions={{ exact: true }} activeProps={{ className: 'on' }} inactiveProps={{ className: 'off' }} className="base">
          Overview
        </Link>
        <Link to="/admin/users" activeProps={{ className: 'on' }} inactiveProps={{ className: 'off' }} className="base">
          Users
        </Link>
        <Probe />
      </>,
      { url: '/admin' },
    )
    const overview = screen.getByRole('link', { name: 'Overview' })
    expect(overview).toHaveAttribute('aria-current', 'page')
    expect(overview).toHaveAttribute('data-status', 'active')
    expect(overview).toHaveClass('base', 'on')
    const users = screen.getByRole('link', { name: 'Users' })
    expect(users).toHaveAttribute('href', '/admin/users')
    expect(users).toHaveClass('base', 'off')

    await userEvent.click(users)

    expect(router.href).toBe('/admin/users')
    expect(screen.getByRole('link', { name: 'Users' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current')
  })

  it('FR-94: an explicit aria-current wins over the automatic one', () => {
    renderWithProviders(
      <Link to="/projects" aria-current={undefined}>
        Projects
      </Link>,
      { url: '/projects' },
    )
    expect(screen.getByRole('link', { name: 'Projects' })).not.toHaveAttribute('aria-current')
  })

  it('FR-95: useSearch parses with a validator function or a Zod schema; useSetSearch writes the URL', async () => {
    const schema = z.object({ page: z.coerce.number().int().min(2).optional().catch(undefined) })
    function Page() {
      const fn = useSearch((raw) => ({ q: typeof raw.q === 'string' ? raw.q : undefined }))
      const zod = useSearch(schema)
      const setSearch = useSetSearch()
      return (
        <>
          <p>q={fn.q ?? '-'}</p>
          <p>page={zod.page ?? '-'}</p>
          <button type="button" onClick={() => void setSearch({ q: 'next', page: 3 }, { replace: true })}>
            Next
          </button>
        </>
      )
    }
    const { router } = renderWithProviders(<Page />, { url: '/admin/users?q=bob&page=x' })
    expect(screen.getByText('q=bob')).toBeInTheDocument()
    expect(screen.getByText('page=-')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Next' }))

    expect(router.href).toBe('/admin/users?q=next&page=3')
    expect(router.calls.at(-1)?.type).toBe('replace')
    expect(screen.getByText('page=3')).toBeInTheDocument()
  })

  it('FR-94: useNavigate resolves once the new location has rendered', async () => {
    function Nav() {
      const navigate = useNavigate()
      const [settled, setSettled] = useState(false)
      return (
        <>
          <button
            type="button"
            onClick={() => {
              void navigate({ to: '/projects/$projectId', params: { projectId: 'p1' } }).then(() => {
                setSettled(true)
              })
            }}
          >
            Go
          </button>
          {settled && <p>settled</p>}
          <Probe />
        </>
      )
    }
    const { router } = renderWithProviders(<Nav />, { url: '/projects' })
    await userEvent.click(screen.getByRole('button', { name: 'Go' }))
    expect(await screen.findByText('settled')).toBeInTheDocument()
    expect(router.href).toBe('/projects/p1')
    expect(screen.getByTestId('href')).toHaveTextContent('/projects/p1')
  })

  it('FR-94: <Navigate replace> redirects on mount', async () => {
    const { router } = renderWithProviders(<Navigate to="/projects/$projectId/settings/$section" params={{ projectId: 'p', section: 'general' }} replace />, {
      url: '/projects/p/settings',
    })
    await waitFor(() => {
      expect(router.href).toBe('/projects/p/settings/general')
    })
    expect(router.calls.at(-1)?.type).toBe('replace')
  })
})
