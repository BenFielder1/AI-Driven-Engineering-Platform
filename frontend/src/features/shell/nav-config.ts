/**
 * FR-53 / FR-54 (SPEC §7.1): the single source of truth for the sidebar, the mobile drawer, the ⌘K command palette and
 * the header breadcrumbs. Pure data and functions (no React), so the rules are unit-tested in nav-config.test.ts.
 */
import {
  Activity,
  Bell,
  Bot,
  ClipboardCheck,
  CloudUpload,
  Compass,
  FileStack,
  FileText,
  FolderKanban,
  Gauge,
  Inbox,
  LayoutDashboard,
  ListChecks,
  ListTree,
  Plug,
  Plus,
  Rocket,
  ScrollText,
  Settings,
  ShieldAlert,
  User,
  Users,
  Workflow,
  type LucideIcon,
} from 'lucide-react'

import type { Schemas } from '../../api/client'
import type { FileRouteTypes } from '../../routeTree.gen'
import { artifactTypeLabels, phaseLabels, phases } from '../projects/labels'
import { parseSettingsTab, settingsTabLabels } from '../projects/settings/settings-tabs'

/** Every navigable route path (typed by the router), e.g. `/projects/$projectId/review`. */
export type AppPath = FileRouteTypes['to']
export type Phase = Schemas['Phase']

/** Who is looking: decides which items show. */
export interface NavViewer {
  isAdmin: boolean
}

export interface NavItem {
  key: string
  label: string
  /** Route path; `$projectId` is filled from the current project. */
  to: AppPath
  icon: LucideIcon
  /** Active only on this exact path (Overview, Admin overview), not on sub-pages. */
  exact?: boolean
  /** Extra search terms for the command palette. */
  keywords?: readonly string[]
  /** A live count shown next to the label (the unread notifications count). */
  badge?: 'unread'
  /** Lifecycle items: the stage they show (FR-54). */
  stage?: Phase
}

export interface NavSection {
  key: 'workspace' | 'admin' | 'lifecycle' | 'project'
  /** Section label (11 px uppercase); the palette uses it as the group name. */
  label: string
  items: readonly NavItem[]
}

// ---- Workspace mode --------------------------------------------------------------------------------------------

const WORKSPACE: NavSection = {
  key: 'workspace',
  label: 'Workspace',
  items: [
    { key: 'projects', label: 'Projects', to: '/projects', icon: FolderKanban, keywords: ['home', 'list'] },
    { key: 'notifications', label: 'Notifications', to: '/notifications', icon: Bell, badge: 'unread', keywords: ['inbox', 'unread'] },
    {
      key: 'connections',
      label: 'Connections',
      to: '/connections',
      icon: Plug,
      keywords: ['llm', 'github', 'vercel', 'keys', 'providers', 'integrations'],
    },
  ],
}

const ADMIN: NavSection = {
  key: 'admin',
  label: 'Admin',
  items: [
    { key: 'admin-overview', label: 'Overview', to: '/admin', icon: Gauge, exact: true, keywords: ['admin'] },
    { key: 'admin-users', label: 'Users', to: '/admin/users', icon: Users, keywords: ['admin', 'roles'] },
    { key: 'admin-events', label: 'Events', to: '/admin/events', icon: ScrollText, keywords: ['admin', 'audit', 'log'] },
    { key: 'admin-runs', label: 'Agent runs', to: '/admin/runs', icon: Bot, keywords: ['admin', 'tokens', 'cost'] },
    { key: 'admin-dead-letters', label: 'Dead letters', to: '/admin/dead-letters', icon: Inbox, keywords: ['admin', 'jobs', 'retry'] },
    { key: 'admin-integrations', label: 'Integrations', to: '/admin/integrations', icon: Workflow, keywords: ['admin', 'github app', 'presets'] },
  ],
}

/** The admin pages in nav order (the admin layout's section nav and the admin overview use these too). */
export const adminNavItems: readonly NavItem[] = ADMIN.items

/** Sidebar / drawer sections outside a project. Admin only for admins (FR-3). */
export function workspaceSections(viewer: NavViewer): NavSection[] {
  return viewer.isAdmin ? [WORKSPACE, ADMIN] : [WORKSPACE]
}

// ---- Project mode (FR-54) ----------------------------------------------------------------------------------------

/** Each lifecycle stage's page (FR-54, FR-55). */
export const stageRoute: Record<Phase, AppPath> = {
  refinement: '/projects/$projectId/refinement',
  specification: '/projects/$projectId/specification',
  planning: '/projects/$projectId/planning',
  execution: '/projects/$projectId/execution',
  review: '/projects/$projectId/review',
  release: '/projects/$projectId/release',
}

const stageIcon: Record<Phase, LucideIcon> = {
  refinement: Compass,
  specification: FileText,
  planning: ListTree,
  execution: ListChecks,
  review: ClipboardCheck,
  release: Rocket,
}

const LIFECYCLE: NavSection = {
  key: 'lifecycle',
  label: 'Lifecycle',
  items: phases.map((stage) => ({
    key: `stage-${stage}`,
    label: phaseLabels[stage],
    to: stageRoute[stage],
    icon: stageIcon[stage],
    stage,
    keywords: ['stage', 'phase'],
  })),
}

/** FR-91: the Deployments page, shown between Activity and Settings only when the project has a deployment target. */
const DEPLOYMENTS_ITEM: NavItem = {
  key: 'deployments',
  label: 'Deployments',
  to: '/projects/$projectId/deployments',
  icon: CloudUpload,
  keywords: ['vercel', 'preview', 'production', 'promote', 'redeploy'],
}

const PROJECT: NavSection = {
  key: 'project',
  label: 'Project',
  items: [
    { key: 'overview', label: 'Overview', to: '/projects/$projectId', icon: LayoutDashboard, exact: true },
    { key: 'artifacts', label: 'Artifacts', to: '/projects/$projectId/artifacts', icon: FileStack, keywords: ['spec', 'plan', 'versions'] },
    { key: 'runs', label: 'Agent runs', to: '/projects/$projectId/runs', icon: Bot, keywords: ['tokens', 'llm'] },
    { key: 'activity', label: 'Activity', to: '/projects/$projectId/activity', icon: Activity, keywords: ['events', 'history', 'audit'] },
    { key: 'settings', label: 'Settings', to: '/projects/$projectId/settings', icon: Settings, keywords: ['members', 'git', 'llm'] },
  ],
}

/** What decides the optional project items (FR-54): `deploymentsEnabled` is `ProjectDto.deploymentsEnabled` (FR-88). */
export interface ProjectNavOptions {
  deploymentsEnabled?: boolean
}

/**
 * Sidebar / drawer sections inside a project (FR-54): Lifecycle (one item per stage), then Project. Deployments
 * (FR-91) shows only when the project has a deployment target.
 */
export function projectSections(options: ProjectNavOptions = {}): NavSection[] {
  if (!options.deploymentsEnabled) {
    return [LIFECYCLE, PROJECT]
  }
  const items = [...PROJECT.items]
  items.splice(items.length - 1, 0, DEPLOYMENTS_ITEM)
  return [LIFECYCLE, { ...PROJECT, items }]
}

/** Routes that aren't sidebar items but are in the palette and breadcrumbs. */
const EXTRA_ITEMS: readonly NavItem[] = [
  { key: 'new-project', label: 'New project', to: '/projects/new', icon: Plus, keywords: ['create', 'start'] },
  { key: 'account', label: 'Account', to: '/account', icon: User, keywords: ['profile', 'api tokens', 'github'] },
]

const APPROVAL_ITEM: NavItem = { key: 'approval', label: 'Approval', to: '/approvals/$token', icon: ShieldAlert }

// ---- Paths ---------------------------------------------------------------------------------------------------

export interface PathParams {
  projectId?: string
}

/** The concrete href of an item, e.g. `/projects/42/review`. */
export function hrefFor(item: Pick<NavItem, 'to'>, params: PathParams = {}): string {
  return item.to.replace('$projectId', params.projectId ?? '$projectId')
}

/** True when `pathname` is `href` or below it (or exactly `href` for exact items). Trailing slashes are ignored. */
export function matchesHref(pathname: string, href: string, exact = false): boolean {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname
  if (exact) {
    return path === href
  }
  return path === href || path.startsWith(`${href}/`)
}

/** The project id in a `/projects/:id/...` pathname (not `/projects/new`). */
export function projectIdFromPath(pathname: string): string | undefined {
  const match = /^\/projects\/([^/]+)/.exec(pathname)
  const id = match?.[1]
  return id && id !== 'new' ? decodeURIComponent(id) : undefined
}

export interface FoundItem {
  item: NavItem
  section: NavSection | null
}

/** The most specific nav item for a pathname (longest matching href), or undefined. */
export function findNavItem(pathname: string, viewer: NavViewer): FoundItem | undefined {
  const projectId = projectIdFromPath(pathname)
  const candidates: FoundItem[] = projectId
    ? projectSections({ deploymentsEnabled: true }).flatMap((section) => section.items.map((item) => ({ item, section })))
    : [
        ...workspaceSections(viewer).flatMap((section) => section.items.map((item) => ({ item, section }))),
        ...EXTRA_ITEMS.map((item) => ({ item, section: null })),
      ]
  let best: (FoundItem & { length: number }) | undefined
  for (const candidate of candidates) {
    const href = hrefFor(candidate.item, { projectId })
    if (matchesHref(pathname, href, candidate.item.exact) && (!best || href.length > best.length)) {
      best = { ...candidate, length: href.length }
    }
  }
  if (!best && matchesHref(pathname, '/approvals')) {
    return { item: APPROVAL_ITEM, section: null }
  }
  return best ? { item: best.item, section: best.section } : undefined
}

// ---- Breadcrumbs -----------------------------------------------------------------------------------------------

export interface Crumb {
  label: string
  /** Absent on the last crumb (the current page). */
  href?: string
}

/** Breadcrumbs for a pathname (FR-53: "the header shows breadcrumbs matching the route"). */
export function crumbsFor(pathname: string, viewer: NavViewer, context: { projectName?: string } = {}): Crumb[] {
  const crumbs: Crumb[] = []
  const projectId = projectIdFromPath(pathname)
  const found = findNavItem(pathname, viewer)

  if (projectId) {
    const projectHref = `/projects/${encodeURIComponent(projectId)}`
    crumbs.push({ label: 'Projects', href: '/projects' }, { label: context.projectName ?? 'Project', href: projectHref })
    if (found && found.item.key !== 'overview') {
      const href = hrefFor(found.item, { projectId })
      crumbs.push({ label: found.item.label, href })
      const rest = pathname.slice(href.length).split('/').filter(Boolean)
      if (rest.length > 0) {
        const type = rest[0] as keyof typeof artifactTypeLabels
        const section = found.item.key === 'settings' ? parseSettingsTab(rest[0]) : undefined
        crumbs.push({
          label: section
            ? settingsTabLabels[section]
            : found.item.key === 'artifacts' && type in artifactTypeLabels
              ? artifactTypeLabels[type]
              : 'Details',
        })
      }
    }
  } else if (found?.section?.key === 'admin') {
    crumbs.push({ label: 'Admin', href: '/admin' })
    if (found.item.key !== 'admin-overview') {
      crumbs.push({ label: found.item.label })
    }
  } else if (found?.item.key === 'new-project') {
    crumbs.push({ label: 'Projects', href: '/projects' }, { label: found.item.label })
  } else if (found) {
    crumbs.push({ label: found.item.label })
  }

  // The last crumb is the current page: no link.
  const last = crumbs.at(-1)
  if (last) {
    crumbs[crumbs.length - 1] = { label: last.label }
  }
  return crumbs
}

// ---- Command palette -------------------------------------------------------------------------------------------

export interface PaletteEntry {
  key: string
  label: string
  group: string
  href: string
  icon: LucideIcon
  keywords: readonly string[]
}

/** Every page the viewer can go to from ⌘K: the current project's pages first (in project mode), then the workspace. */
export function paletteEntries(
  viewer: NavViewer,
  project?: { id: string; name: string; deploymentsEnabled?: boolean },
): PaletteEntry[] {
  const entries: PaletteEntry[] = []
  const add = (item: NavItem, group: string, params: PathParams = {}) => {
    entries.push({
      key: `${group}:${item.key}`,
      label: item.label,
      group,
      href: hrefFor(item, params),
      icon: item.icon,
      keywords: item.keywords ?? [],
    })
  }
  if (project) {
    for (const section of projectSections({ deploymentsEnabled: project.deploymentsEnabled })) {
      for (const item of section.items) {
        add(item, `${project.name} · ${section.label}`, { projectId: project.id })
      }
    }
  }
  for (const section of workspaceSections(viewer)) {
    for (const item of section.items) {
      add(item, section.label)
    }
  }
  for (const item of EXTRA_ITEMS) {
    add(item, 'More')
  }
  return entries
}
