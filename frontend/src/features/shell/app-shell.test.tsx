import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'

import type { Schemas } from '../../api/client'
import { authDb, seedUser, signInAs } from '../../mocks/handlers/auth'
import { unhealthyReady } from '../../mocks/handlers/health'
import { seedGate } from '../../mocks/handlers/lifecycle'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { authKeys } from '../auth/auth-api'
import { SIDEBAR_STORAGE_KEY } from './use-sidebar-preference'

const sidebar = () => screen.getByRole('complementary', { name: 'Sidebar' })
const openUserMenu = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(within(sidebar()).getByRole('button', { name: /Account menu for/ }))
  return screen.findByRole('menu')
}

describe('FR-53: app shell', () => {
  it('FR53_shell: the sidebar shows the lockup, the search trigger and the workspace nav; the header shows breadcrumbs', async () => {
    signInAs({ systemRole: 'user', name: 'Bob' })
    await renderRoute('/projects')

    await screen.findByRole('heading', { level: 1, name: 'Projects' })
    const aside = sidebar()
    expect(within(aside).getByRole('link', { name: 'AIDE home' })).toHaveTextContent('AIDE')
    expect(within(aside).getByRole('button', { name: 'Search pages' })).toHaveAttribute('aria-keyshortcuts', 'Meta+K Control+K')
    const nav = within(aside).getByRole('navigation', { name: 'Main' })
    expect(within(nav).getByRole('list', { name: 'Workspace' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Projects' })).toHaveAttribute('aria-current', 'page')
    expect(within(nav).getByRole('link', { name: 'Notifications' })).not.toHaveAttribute('aria-current')
    expect(within(nav).getByRole('link', { name: 'Connections' })).toHaveAttribute('href', '/connections')
    expect(within(nav).queryByRole('list', { name: 'Admin' })).not.toBeInTheDocument()

    const crumbs = screen.getByRole('navigation', { name: 'Breadcrumb' })
    expect(within(crumbs).getByText('Projects')).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('button', { name: /^Notifications, \d+ unread$/ })).toBeInTheDocument()
  })

  it('FR53_shell / FR-3: admins get the Admin section with its six pages, and the breadcrumbs follow the route', async () => {
    signInAs({ systemRole: 'admin', name: 'Ada Admin' })
    await renderRoute('/admin/users')
    const user = userEvent.setup()

    await screen.findByRole('heading', { level: 1, name: 'Admin' })
    const adminList = within(within(sidebar()).getByRole('navigation', { name: 'Main' })).getByRole('list', { name: 'Admin' })
    expect(within(adminList).getAllByRole('link').map((l) => l.textContent)).toEqual([
      'Overview',
      'Users',
      'Events',
      'Agent runs',
      'Dead letters',
      'Integrations',
    ])
    expect(within(adminList).getByRole('link', { name: 'Users' })).toHaveAttribute('aria-current', 'page')
    const crumbs = screen.getByRole('navigation', { name: 'Breadcrumb' })
    expect(within(crumbs).getByRole('link', { name: 'Admin' })).toHaveAttribute('href', '/admin')
    expect(within(crumbs).getByText('Users')).toHaveAttribute('aria-current', 'page')

    const menu = await openUserMenu(user)
    expect(within(menu).getByText('Role: Admin')).toBeInTheDocument()
  })

  it('FR-3: /admin shows a forbidden state to non-admins', async () => {
    signInAs({ systemRole: 'user' })
    await renderRoute('/admin')

    expect(await screen.findByRole('heading', { level: 1, name: "You don't have access to this page" })).toBeInTheDocument()
  })

  it('FR53_shell: /connections shows GitHub and Vercel as not connected (FR-78 lists LLM keys above them)', async () => {
    signInAs()
    await renderRoute('/connections')

    expect(await screen.findByRole('heading', { level: 1, name: 'Connections' })).toBeInTheDocument()
    for (const name of ['GitHub', 'Vercel']) {
      expect(await within(screen.getByRole('region', { name })).findByTestId('empty-state')).toHaveAttribute('data-variant', 'not_connected')
    }
  })

  it('FR53_palette: ⌘K opens the command palette, which lists nav-config pages and navigates', async () => {
    signInAs({ systemRole: 'admin' })
    const { router } = await renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Projects' })

    await user.keyboard('{Control>}k{/Control}')
    const dialog = await screen.findByRole('dialog', { name: 'Command palette' })
    expect(within(dialog).getByRole('option', { name: 'Connections' })).toBeInTheDocument()
    expect(within(dialog).getByRole('option', { name: 'Dead letters' })).toBeInTheDocument()

    await user.type(within(dialog).getByRole('combobox', { name: 'Search pages' }), 'dead')
    await user.click(within(dialog).getByRole('option', { name: 'Dead letters' }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/admin/dead-letters')
    })
    expect(screen.queryByRole('dialog', { name: 'Command palette' })).not.toBeInTheDocument()
  })

  it('FR53_sidebar_collapse: the collapse toggle pins the sidebar and the choice survives a reload', async () => {
    signInAs()
    const first = await renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Projects' })
    expect(sidebar()).toHaveAttribute('data-sidebar-mode', 'auto')

    // Below xl (jsdom matches no media query) auto mode shows icons only, so the toggle expands.
    await user.click(within(sidebar()).getByRole('button', { name: 'Expand sidebar' }))
    expect(sidebar()).toHaveAttribute('data-sidebar-mode', 'expanded')
    expect(localStorage.getItem(SIDEBAR_STORAGE_KEY)).toBe('expanded')

    await user.click(within(sidebar()).getByRole('button', { name: 'Collapse sidebar' }))
    expect(localStorage.getItem(SIDEBAR_STORAGE_KEY)).toBe('collapsed')
    first.unmount()

    await renderRoute('/projects')
    await screen.findByRole('heading', { level: 1, name: 'Projects' })
    expect(sidebar()).toHaveAttribute('data-sidebar-mode', 'collapsed')
    expect(within(sidebar()).getByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument()
  })

  it('FR53_sidebar_collapse: unusable storage falls back to auto without breaking the shell', async () => {
    signInAs()
    localStorage.setItem(SIDEBAR_STORAGE_KEY, 'sideways')
    await renderRoute('/projects')

    await screen.findByRole('heading', { level: 1, name: 'Projects' })
    expect(sidebar()).toHaveAttribute('data-sidebar-mode', 'auto')
  })

  it('FR53_drawer: at 375 px the menu opens the navigation as a left drawer with no horizontal overflow', async () => {
    window.innerWidth = 375
    window.dispatchEvent(new Event('resize'))
    signInAs()
    const { router } = await renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Projects' })

    await user.click(screen.getByRole('button', { name: 'Open navigation' }))
    const drawer = await screen.findByTestId('mobile-drawer')
    expect(drawer).toHaveAttribute('role', 'dialog')
    // Docked left, capped to the viewport (85vw, max 20rem) and clipping its own overflow.
    expect(drawer.className).toMatch(/\bleft-0\b/)
    expect(drawer.className).toMatch(/w-\[85vw\]/)
    expect(drawer.className).toMatch(/max-w-xs/)
    expect(drawer.className).toMatch(/overflow-x-hidden/)
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth)
    // Same nav-config as the sidebar: lockup and workspace nav; choosing a page closes the drawer.
    expect(within(drawer).getByText('AIDE')).toBeInTheDocument()
    await user.click(within(within(drawer).getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Notifications' }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/notifications')
    })
    await waitFor(() => {
      expect(screen.queryByTestId('mobile-drawer')).not.toBeInTheDocument()
    })
    window.innerWidth = 1024
  })

  it('FR53_drawer: any location change closes the drawer (Back, the user menu, links outside the nav)', async () => {
    signInAs()
    const { router } = await renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Projects' })

    await user.click(screen.getByRole('button', { name: 'Open navigation' }))
    await screen.findByTestId('mobile-drawer')
    // Navigation that doesn't go through the drawer's own links (the user menu, Back/Forward, a programmatic redirect).
    await router.navigate({ to: '/account' })
    await waitFor(() => {
      expect(screen.queryByTestId('mobile-drawer')).not.toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: 'Open navigation' }))
    await screen.findByTestId('mobile-drawer')
    router.history.back()
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/projects')
    })
    await waitFor(() => {
      expect(screen.queryByTestId('mobile-drawer')).not.toBeInTheDocument()
    })
  })

  it('FR-1: Sign out ends the session, clears cached data and returns to /login', async () => {
    signInAs()
    const { queryClient, router } = await renderRoute('/account')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Account' })
    expect(queryClient.getQueryData(['me', 'tokens', 'list'])).toBeDefined()

    const menu = await openUserMenu(user)
    expect(within(menu).getByRole('menuitem', { name: /Account/ })).toHaveAttribute('href', '/account')
    expect(within(menu).getByRole('menuitem', { name: /Connections/ })).toHaveAttribute('href', '/connections')
    await user.click(within(menu).getByRole('menuitem', { name: /Sign out/ }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to AIDE' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(authDb.currentUserId).toBeNull()
    expect(queryClient.getQueryData(['me', 'tokens', 'list'])).toBeUndefined()
    expect(queryClient.getQueryData(authKeys.me())).toBeNull()
  })

  it('NFR-A11Y2: right after Account navigates, or while the menu is still closing, a click on the trigger opens the menu', async () => {
    // jsdom runs no CSS animations: report the menu's enter/exit animation names (an inline `animation-name` wins, as in
    // a browser) so a closed menu stays mounted for its exit animation.
    const realStyle = window.getComputedStyle.bind(window)
    const spy = vi.spyOn(window, 'getComputedStyle').mockImplementation((element, pseudo) => {
      const style = realStyle(element, pseudo)
      if (!(element instanceof HTMLElement) || !element.matches('[data-slot=dropdown-menu-content]')) {
        return style
      }
      return new Proxy(style, {
        get(target, prop) {
          if (prop === 'animationName') {
            return element.style.animationName || (element.getAttribute('data-state') === 'closed' ? 'exit' : 'enter')
          }
          const value: unknown = Reflect.get(target, prop)
          return typeof value === 'function' ? (value as (...args: unknown[]) => unknown).bind(target) : value
        },
      })
    })
    const trigger = () => within(sidebar()).getByRole('button', { name: /Account menu for/ })
    try {
      signInAs({ name: 'Bob Builder' })
      await renderRoute('/projects')
      const user = userEvent.setup()
      await screen.findByRole('heading', { level: 1, name: 'Projects' })

      // Choosing Account closes the menu at once (no exit animation left over the page), so the next click opens it.
      const menu = await openUserMenu(user)
      await user.click(within(menu).getByRole('menuitem', { name: /Account/ }))
      expect(await screen.findByRole('heading', { level: 1, name: 'Account' })).toBeInTheDocument()
      expect(screen.queryByRole('menu', { hidden: true })).not.toBeInTheDocument()
      await user.click(trigger())
      expect(await screen.findByRole('menu')).toHaveAttribute('data-state', 'open')

      // Escape animates the close; a click on the trigger meanwhile reopens it instead of being dismissed as "outside".
      await user.keyboard('{Escape}')
      expect(screen.getByRole('menu', { hidden: true })).toHaveAttribute('data-state', 'closed')
      await user.click(trigger())
      await waitFor(() => {
        expect(screen.getByRole('menu')).toHaveAttribute('data-state', 'open')
      })
      expect(trigger()).toHaveAttribute('aria-expanded', 'true')
    } finally {
      spy.mockRestore()
    }
  })

  it('NFR-A11Y: has a skip link to the main landmark, and the user menu closes with Escape back to its trigger', async () => {
    seedUser({ systemRole: 'admin', email: 'root@example.com' })
    signInAs({ systemRole: 'user', email: 'bob@example.com', name: 'Bob Builder' })
    await renderRoute('/projects')
    const user = userEvent.setup()

    await screen.findByRole('heading', { level: 1, name: 'Projects' })
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute('href', '#main')
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main')

    const trigger = within(sidebar()).getByRole('button', { name: 'Account menu for Bob Builder' })
    expect(trigger).toHaveTextContent('BB')
    const menu = await openUserMenu(user)
    expect(within(menu).getByText('bob@example.com')).toBeInTheDocument()
    expect(within(menu).getByText('Role: User')).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })

  it('NFR_A11Y_menus: an open user menu does not aria-hide the focusable page behind it (axe aria-hidden-focus)', async () => {
    signInAs({ systemRole: 'user', name: 'Bob Builder', email: 'bob@example.com' })
    await renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Projects' })

    const menu = await openUserMenu(user)
    expect(menu).toBeInTheDocument()
    // Nothing outside the menu is hidden from assistive tech or made inert while it is open.
    expect(document.querySelector('[aria-hidden="true"] a, [aria-hidden="true"] button, [aria-hidden="true"] input')).toBeNull()
    expect(document.querySelector('[data-aria-hidden="true"]')).toBeNull()
    expect(screen.getByRole('link', { name: 'Skip to content' })).toBeInTheDocument()
    expect(screen.getByRole('main')).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('FR53_theme: the theme menu applies and persists System / Light / Dark', async () => {
    signInAs()
    await renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('heading', { level: 1, name: 'Projects' })

    await user.click(within(sidebar()).getByRole('button', { name: /^Theme/ }))
    await user.click(await screen.findByRole('menuitemradio', { name: 'Light' }))
    expect(document.documentElement).toHaveClass('light')
    expect(document.documentElement).not.toHaveClass('dark')
    expect(localStorage.getItem('aide.theme')).toBe('light')

    await user.click(within(sidebar()).getByRole('button', { name: 'Theme: Light' }))
    await user.click(await screen.findByRole('menuitemradio', { name: 'Dark' }))
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('aide.theme')).toBe('dark')

    await user.click(within(sidebar()).getByRole('button', { name: 'Theme: Dark' }))
    await user.click(await screen.findByRole('menuitemradio', { name: 'System' }))
    expect(localStorage.getItem('aide.theme')).toBeNull()
    // Dark first: with no system preference for light, System is dark.
    expect(document.documentElement).toHaveClass('dark')
  })

  it('NFR-OBS: the footer shows API readiness', async () => {
    signInAs()
    server.use(
      http.get('*/api/health/ready', () => HttpResponse.json<Schemas['HealthResponse']>(unhealthyReady, { status: 503 })),
    )
    await renderRoute('/projects')

    await waitFor(() => {
      expect(screen.getByText(/API status:/)).toHaveTextContent('API status: Unhealthy')
    })
  })
})

describe('FR-54: project mode', () => {
  it('FR54_stage_states: in planning with a pending plan_approval gate, Refinement and Specification are done, Planning is current with "1", later stages muted but navigable', async () => {
    signInAs()
    const project = seedProject({ name: 'Inventory tracker', currentPhase: 'planning' })
    seedGate(project, { type: 'plan_approval', artifactType: 'implementation_plan', artifactVersion: 1 })
    await renderRoute(`/projects/${project.id}`)

    const nav = await screen.findByRole('navigation', { name: 'Project sections' })
    const lifecycle = within(nav).getByRole('list', { name: 'Lifecycle' })
    const link = (name: string) => within(lifecycle).getByRole('link', { name })

    await waitFor(() => {
      expect(link('Planning')).toHaveAttribute('data-stage-state', 'current')
    })
    expect(link('Refinement')).toHaveAttribute('data-stage-state', 'done')
    expect(link('Refinement')).toHaveAccessibleDescription('Done')
    expect(within(link('Refinement')).getByTestId('stage-done')).toBeInTheDocument()
    expect(link('Specification')).toHaveAttribute('data-stage-state', 'done')
    expect(link('Planning')).toHaveAccessibleDescription('Current stage, 1 awaiting action')
    expect(within(link('Planning')).getByTestId('stage-current')).toBeInTheDocument()
    expect(within(link('Planning')).getByTestId('stage-needs-action')).toHaveTextContent('1')
    for (const later of ['Execution', 'Review', 'Release']) {
      expect(link(later)).toHaveAttribute('data-stage-state', 'upcoming')
      expect(link(later)).toHaveAccessibleDescription('Upcoming')
      expect(link(later)).toHaveAttribute('href', `/projects/${project.id}/${later.toLowerCase()}`)
    }
  })

  it('FR54_project_mode: the switcher shows the project and lists my projects; Project section and "All projects" back to the workspace', async () => {
    signInAs()
    const project = seedProject({ name: 'Inventory tracker', currentPhase: 'refinement' })
    seedProject({ name: 'Billing service' })
    const { router } = await renderRoute(`/projects/${project.id}/settings`)
    const user = userEvent.setup()

    const nav = await screen.findByRole('navigation', { name: 'Project sections' })
    const projectList = within(nav).getByRole('list', { name: 'Project' })
    expect(within(projectList).getAllByRole('link').map((l) => l.textContent)).toEqual([
      'Overview',
      'Artifacts',
      'Agent runs',
      'Activity',
      'Settings',
    ])
    expect(within(projectList).getByRole('link', { name: 'Settings' })).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()

    const switcher = await within(sidebar()).findByRole('button', { name: 'Switch project. Current: Inventory tracker' })
    await user.click(switcher)
    const menu = await screen.findByRole('menu')
    expect(await within(menu).findByRole('menuitem', { name: 'Billing service' })).toBeInTheDocument()
    expect(within(menu).getByRole('menuitem', { name: /Inventory tracker/ })).toBeInTheDocument()
    await user.keyboard('{Escape}')

    const crumbs = screen.getByRole('navigation', { name: 'Breadcrumb' })
    expect(within(crumbs).getByRole('link', { name: 'Inventory tracker' })).toHaveAttribute('href', `/projects/${project.id}`)
    // /settings redirects to its General section (SPEC §7.2 settings sub-routes).
    expect(within(crumbs).getByRole('link', { name: 'Settings' })).toHaveAttribute('href', `/projects/${project.id}/settings`)
    expect(within(crumbs).getByText('General')).toHaveAttribute('aria-current', 'page')

    await user.click(within(sidebar()).getByRole('link', { name: 'All projects' }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/projects')
    })
    expect(await within(sidebar()).findByRole('navigation', { name: 'Main' })).toBeInTheDocument()
  })

  it('FR54_stage_routes: the stage and activity routes render their pages instead of 404', async () => {
    signInAs()
    const project = seedProject({ name: 'Inventory tracker', currentPhase: 'refinement' })

    for (const [path, heading] of [
      ['specification', 'Specification'],
      ['planning', 'Planning'],
      ['release', 'Release'],
      ['activity', 'Activity'],
    ] as const) {
      const { unmount } = await renderRoute(`/projects/${project.id}/${path}`)
      expect(await screen.findByRole('heading', { level: 2, name: heading })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Page not found' })).not.toBeInTheDocument()
      unmount()
    }
  })
})
