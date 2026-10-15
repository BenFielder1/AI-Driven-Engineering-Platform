import { Link } from '@tanstack/react-router'
import { PanelLeftClose, PanelLeftOpen, Search } from 'lucide-react'

import { BrandLockup } from '../../components/brand'
import { ProjectVersionBadges } from '../../components/shared/version-badge'
import { cn } from '../../lib/utils'
import type { User } from '../auth/auth-api'
import { whenCollapsed } from './collapse-classes'
import { projectSections, workspaceSections } from './nav-config'
import { NavList } from './nav-list'
import { ProjectSwitcher } from './project-switcher'
import { ThemeMenu } from './theme-toggle'
import type { ShellNav } from './use-shell-nav'
import type { SidebarMode } from './use-sidebar-preference'
import { UserMenu } from './user-menu'

const widthClass: Record<SidebarMode, string> = {
  auto: 'w-16 xl:w-64',
  collapsed: 'w-16',
  expanded: 'w-64',
}

/** The search trigger (⌘K). Shared by the sidebar and the drawer. */
export function SearchButton({ mode, onClick }: { mode: SidebarMode; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label="Search pages"
      aria-keyshortcuts="Meta+K Control+K"
      className={cn(
        'flex min-h-11 w-full items-center gap-2 rounded-lg border border-border/60 bg-background/40 px-3 text-sm text-muted-foreground transition-colors hover:text-foreground',
        whenCollapsed.center[mode],
      )}
    >
      <Search className="size-4 shrink-0" aria-hidden />
      <span className={cn('flex-1 text-left', whenCollapsed.srOnly[mode])}>Search…</span>
      <kbd className={cn('rounded border border-border/60 px-1.5 text-[10px]', whenCollapsed.hidden[mode])}>⌘K</kbd>
    </button>
  )
}

/** Navigation for the current mode: workspace, or project (switcher, Lifecycle, Project) inside /projects/:id. */
export function ShellNavigation({
  nav,
  mode,
  onNavigate,
}: {
  nav: ShellNav
  mode: SidebarMode
  onNavigate?: () => void
}) {
  if (nav.projectId) {
    return (
      <div className="flex flex-col gap-4">
        <ProjectSwitcher
          projectId={nav.projectId}
          projectName={nav.projectName}
          badges={
            nav.projectName !== undefined ? (
              <ProjectVersionBadges released={nav.releasedVersion} development={nav.developmentVersion} />
            ) : undefined
          }
          mode={mode}
          onNavigate={onNavigate}
        />
        <nav aria-label="Project sections">
          <NavList
            sections={projectSections()}
            pathname={nav.pathname}
            projectId={nav.projectId}
            stages={nav.stages}
            mode={mode}
            onNavigate={onNavigate}
          />
        </nav>
      </div>
    )
  }
  return (
    <nav aria-label="Main">
      <NavList sections={workspaceSections(nav.viewer)} pathname={nav.pathname} mode={mode} onNavigate={onNavigate} />
    </nav>
  )
}

/**
 * FR-53 desktop sidebar (SPEC §7.1): fixed and frosted; hidden below lg (the drawer replaces it), icons-only 64 px
 * from lg to xl and 256 px from xl, unless the user pins it collapsed or expanded.
 */
export function Sidebar({
  user,
  nav,
  mode,
  expanded,
  onToggle,
  onOpenPalette,
}: {
  user: User
  nav: ShellNav
  mode: SidebarMode
  expanded: boolean
  onToggle: () => void
  onOpenPalette: () => void
}) {
  return (
    <aside
      aria-label="Sidebar"
      data-sidebar-mode={mode}
      className={cn(
        'fixed inset-y-0 left-0 z-40 hidden flex-col border-r border-sidebar-border/70 bg-sidebar/70 text-sidebar-foreground backdrop-blur-xl lg:flex',
        widthClass[mode],
      )}
    >
      <div className="flex flex-col gap-3 px-2 pt-4 pb-3 xl:px-3">
        <Link
          to="/projects"
          activeOptions={{ exact: true }}
          aria-label="AIDE home"
          className={cn('flex min-h-11 items-center rounded-lg px-2', whenCollapsed.center[mode])}
        >
          <BrandLockup wordmarkClassName={whenCollapsed.srOnly[mode]} />
        </Link>
        <SearchButton mode={mode} onClick={onOpenPalette} />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-4 xl:px-3">
        <ShellNavigation nav={nav} mode={mode} />
      </div>
      <div className={cn('flex items-center gap-1 border-t border-sidebar-border/70 p-2 xl:p-3', whenCollapsed.stack[mode])}>
        <UserMenu user={user} mode={mode} />
        <ThemeMenu />
        <button
          type="button"
          onClick={onToggle}
          aria-label={expanded ? 'Collapse sidebar' : 'Expand sidebar'}
          className="flex size-11 shrink-0 items-center justify-center rounded-lg text-muted-foreground hover:bg-sidebar-accent/60 hover:text-foreground"
        >
          {expanded ? <PanelLeftClose className="size-5" aria-hidden /> : <PanelLeftOpen className="size-5" aria-hidden />}
        </button>
      </div>
    </aside>
  )
}
