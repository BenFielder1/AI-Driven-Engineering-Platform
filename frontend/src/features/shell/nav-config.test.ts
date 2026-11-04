import { describe, expect, it } from 'vitest'

import { toAppRouterPattern } from '../../lib/app-paths'
import { matchAppRoute } from '../../test/app-routes'
import {
  crumbsFor,
  findNavItem,
  hrefFor,
  IMPORT_REPOSITORY_HREF,
  matchesHref,
  paletteActions,
  paletteEntries,
  projectIdFromPath,
  projectSections,
  workspaceSections,
} from './nav-config'
import { DOCS_URL } from '../../lib/site'

const admin = { isAdmin: true }
const user = { isAdmin: false }

const labels = (sections: ReturnType<typeof workspaceSections>) =>
  sections.map((s) => ({ label: s.label, items: s.items.map((i) => i.label) }))

/** True when a src/app page serves the pattern (so nav-config can't point at a route that doesn't exist). */
function hasPage(to: string): boolean {
  const pattern = toAppRouterPattern(to)
  return matchAppRoute(pattern)?.route.pattern === pattern
}

describe('FR-53: workspace navigation', () => {
  it('FR53_nav_config / FR-206: Home, Projects, Notifications and Connections for everyone; Admin with its six pages for admins only', () => {
    expect(labels(workspaceSections(user))).toEqual([{ label: 'Workspace', items: ['Home', 'Projects', 'Notifications', 'Connections'] }])
    expect(labels(workspaceSections(admin))).toEqual([
      { label: 'Workspace', items: ['Home', 'Projects', 'Notifications', 'Connections'] },
      { label: 'Admin', items: ['Overview', 'Users', 'Events', 'Agent runs', 'Dead letters', 'Integrations'] },
    ])
  })

  it('FR53_nav_config: every item points at a route that exists', () => {
    const items = [...workspaceSections(admin), ...projectSections()].flatMap((s) => s.items)
    for (const item of items) {
      expect(hasPage(item.to), item.to).toBe(true)
    }
  })

  it('FR53_nav_config: matches the current page (exact items only on their own path)', () => {
    expect(matchesHref('/projects/abc/review', '/projects')).toBe(true)
    expect(matchesHref('/admin/users', '/admin', true)).toBe(false)
    expect(matchesHref('/admin/', '/admin', true)).toBe(true)
    expect(findNavItem('/admin/users', admin)?.item.label).toBe('Users')
    expect(findNavItem('/admin', admin)?.item.label).toBe('Overview')
    expect(findNavItem('/notifications', user)?.item.label).toBe('Notifications')
  })
})

describe('FR-54: project navigation', () => {
  it('FR54_nav_config: Lifecycle has the six stages in order, Project has Overview, Artifacts, Agent runs, Activity and Settings', () => {
    expect(labels(projectSections())).toEqual([
      { label: 'Lifecycle', items: ['Refinement', 'Specification', 'Planning', 'Execution', 'Review', 'Release'] },
      { label: 'Project', items: ['Overview', 'Artifacts', 'Agent runs', 'Activity', 'Settings'] },
    ])
  })

  it('FR91_nav_deployments: Deployments shows between Activity and Settings only when the project has a deployment target', () => {
    expect(projectSections({ deploymentsEnabled: false })[1]?.items.map((i) => i.label)).not.toContain('Deployments')
    const project = projectSections({ deploymentsEnabled: true })[1]
    expect(project?.items.map((i) => i.label)).toEqual(['Overview', 'Artifacts', 'Agent runs', 'Activity', 'Deployments', 'Settings'])
    expect(hrefFor(project?.items[4] ?? { to: '/projects' }, { projectId: 'p1' })).toBe('/projects/p1/deployments')
    expect(paletteEntries(user, { id: 'p1', name: 'P' }).map((e) => e.label)).not.toContain('Deployments')
    expect(paletteEntries(user, { id: 'p1', name: 'P', deploymentsEnabled: true }).map((e) => e.label)).toContain('Deployments')
  })

  it('FR54_nav_config: stage items link to /projects/:id/<stage>', () => {
    const lifecycle = projectSections()[0]
    expect(lifecycle?.items.map((i) => hrefFor(i, { projectId: 'p1' }))).toEqual([
      '/projects/p1/refinement',
      '/projects/p1/specification',
      '/projects/p1/planning',
      '/projects/p1/execution',
      '/projects/p1/review',
      '/projects/p1/release',
    ])
  })

  it('FR54_nav_config: project mode is any /projects/:id path, not /projects or /projects/new', () => {
    expect(projectIdFromPath('/projects/p1/artifacts/technical_specification')).toBe('p1')
    expect(projectIdFromPath('/projects')).toBeUndefined()
    expect(projectIdFromPath('/projects/new')).toBeUndefined()
  })
})

describe('FR-53: breadcrumbs come from nav-config', () => {
  it('FR53_breadcrumbs: workspace and admin pages', () => {
    expect(crumbsFor('/projects', user)).toEqual([{ label: 'Projects' }])
    expect(crumbsFor('/home', user)).toEqual([{ label: 'Home' }])
    expect(crumbsFor('/projects/new', user)).toEqual([{ label: 'Projects', href: '/projects' }, { label: 'New project' }])
    expect(crumbsFor('/connections', user)).toEqual([{ label: 'Connections' }])
    expect(crumbsFor('/admin', admin)).toEqual([{ label: 'Admin' }])
    expect(crumbsFor('/admin/dead-letters', admin)).toEqual([{ label: 'Admin', href: '/admin' }, { label: 'Dead letters' }])
  })

  it('FR53_breadcrumbs: project pages show Projects › project › section (› artifact type)', () => {
    const ctx = { projectName: 'Inventory tracker' }
    expect(crumbsFor('/projects/p1', user, ctx)).toEqual([{ label: 'Projects', href: '/projects' }, { label: 'Inventory tracker' }])
    expect(crumbsFor('/projects/p1/planning', user, ctx)).toEqual([
      { label: 'Projects', href: '/projects' },
      { label: 'Inventory tracker', href: '/projects/p1' },
      { label: 'Planning' },
    ])
    expect(crumbsFor('/projects/p1/artifacts/technical_specification', user, ctx)).toEqual([
      { label: 'Projects', href: '/projects' },
      { label: 'Inventory tracker', href: '/projects/p1' },
      { label: 'Artifacts', href: '/projects/p1/artifacts' },
      { label: 'Technical specification' },
    ])
    expect(crumbsFor('/projects/p1/settings/git', user, ctx)).toEqual([
      { label: 'Projects', href: '/projects' },
      { label: 'Inventory tracker', href: '/projects/p1' },
      { label: 'Settings', href: '/projects/p1/settings' },
      { label: 'Git' },
    ])
  })
})

describe('FR-53: the command palette lists the same pages', () => {
  it('FR53_palette / FR-209: workspace pages plus Account (Home and New project are actions); admin pages for admins only', () => {
    const forUser = paletteEntries(user).map((e) => e.label)
    expect(forUser).toEqual(['Projects', 'Notifications', 'Connections', 'Account'])
    expect(paletteEntries(admin).map((e) => e.href)).toContain('/admin/integrations')
  })

  it('FR53_palette: in a project, its pages come first with concrete hrefs', () => {
    const entries = paletteEntries(user, { id: 'p1', name: 'Inventory tracker' })
    expect(entries[0]).toMatchObject({ label: 'Refinement', group: 'Inventory tracker · Lifecycle', href: '/projects/p1/refinement' })
    expect(entries.find((e) => e.label === 'Settings')?.href).toBe('/projects/p1/settings')
  })
})

describe('FR-209: command palette actions', () => {
  it('FR209_palette_actions: Go to Home, New project, Import repository, Switch theme (Light/Dark/System), Open docs', () => {
    const actions = paletteActions()
    expect(actions.map((a) => a.label)).toEqual([
      'Go to Home',
      'New project',
      'Import repository',
      'Switch theme: Light',
      'Switch theme: Dark',
      'Switch theme: System',
      'Open docs',
    ])
    expect(actions.map((a) => a.run)).toEqual([
      { type: 'navigate', href: '/home' },
      { type: 'navigate', href: '/projects/new' },
      { type: 'navigate', href: IMPORT_REPOSITORY_HREF },
      { type: 'theme', theme: 'light' },
      { type: 'theme', theme: 'dark' },
      { type: 'theme', theme: 'system' },
      { type: 'external', href: DOCS_URL },
    ])
    expect(IMPORT_REPOSITORY_HREF).toBe('/projects/new?source=import')
  })
})
