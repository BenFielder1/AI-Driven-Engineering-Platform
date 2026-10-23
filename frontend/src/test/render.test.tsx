import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { Link, useParams } from '../lib/navigation'
import { matchAppRoute, routeFromFile } from './app-routes'
import { parseSearchTyped } from './memory-router'
import { currentHref, renderRoute, renderWithProviders } from './render'

describe('test router (FR-94 test harness)', () => {
  it('derives Next.js routes from src/app files (groups, dynamic and catch-all segments)', () => {
    const files = [
      '../app/page.tsx',
      '../app/(app)/projects/page.tsx',
      '../app/(app)/projects/new/page.tsx',
      '../app/(app)/projects/[projectId]/page.tsx',
      '../app/(app)/projects/[projectId]/settings/[section]/page.tsx',
      '../app/(app)/docs/[...slug]/page.tsx',
    ]
    expect(routeFromFile(files[3] ?? '')).toMatchObject({
      pattern: '/projects/[projectId]',
      dirs: ['', '(app)', '(app)/projects', '(app)/projects/[projectId]'],
    })
    expect(matchAppRoute('/', files)?.route.pattern).toBe('/')
    expect(matchAppRoute('/projects/new', files)?.route.pattern).toBe('/projects/new')
    expect(matchAppRoute('/projects/a%20b', files)?.params).toEqual({ projectId: 'a b' })
    expect(matchAppRoute('/projects/p/settings/git', files)?.params).toEqual({ projectId: 'p', section: 'git' })
    expect(matchAppRoute('/docs/a/b', files)?.params).toEqual({ slug: 'a/b' })
    expect(matchAppRoute('/nope', files)).toBeNull()
  })

  it('parses search like TanStack for assertions', () => {
    expect(parseSearchTyped('?version=4&release=1.1.0&unread=true&q=x')).toEqual({ version: 4, release: '1.1.0', unread: true, q: 'x' })
  })

  it('renderWithProviders puts components on the in-memory router (links navigate, history goes back)', async () => {
    function Item() {
      const { projectId } = useParams<{ projectId: string }>()
      return <p>param={projectId}</p>
    }
    const { router } = renderWithProviders(
      <>
        <Link to="/login" search={{ returnUrl: '/account' }}>
          Sign in
        </Link>
        <Item />
      </>,
      { url: '/register' },
    )
    await userEvent.click(screen.getByRole('link', { name: 'Sign in' }))
    expect(currentHref(router)).toBe('/login?returnUrl=%2Faccount')
    expect(router.state.location.pathname).toBe('/login')
    router.history.back()
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/register')
    })
  })

  it('renderRoute renders the root not-found page for unknown URLs', async () => {
    await renderRoute('/does-not-exist')
    expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument()
  })
})
