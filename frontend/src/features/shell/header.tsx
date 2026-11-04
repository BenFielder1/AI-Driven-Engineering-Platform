import { ChevronRight, Menu, Search } from 'lucide-react'

import { cn } from '../../lib/utils'
import { ProjectLive } from '../live/live-indicator'
import { NotificationBell } from '../notifications/notification-bell'
import { VersionSwitcher } from '../versions/version-switcher'
import { HrefLink } from './href-link'
import { crumbsFor } from './nav-config'
import type { ShellNav } from './use-shell-nav'

/** FR-53: breadcrumbs from nav-config. Below sm only the current page shows. */
export function Breadcrumbs({ nav, className }: { nav: ShellNav; className?: string }) {
  const crumbs = crumbsFor(nav.pathname, nav.viewer, { projectName: nav.projectName })
  if (crumbs.length === 0) {
    return <div className={className} />
  }
  return (
    <nav aria-label="Breadcrumb" className={cn('min-w-0', className)}>
      <ol className="flex min-w-0 items-center gap-1 text-sm">
        {crumbs.map((c, i) => {
          const last = i === crumbs.length - 1
          return (
            <li key={`${c.label}-${String(i)}`} className={cn('flex min-w-0 items-center gap-1', !last && 'max-sm:hidden')}>
              {c.href ? (
                <HrefLink href={c.href} className="truncate text-muted-foreground hover:text-foreground">
                  {c.label}
                </HrefLink>
              ) : (
                <span className="truncate font-medium" aria-current="page">
                  {c.label}
                </span>
              )}
              {!last && <ChevronRight aria-hidden className="size-3.5 shrink-0 text-muted-foreground" />}
            </li>
          )
        })}
      </ol>
    </nav>
  )
}

/**
 * FR-53, FR-206 header (SPEC §7.1): sticky, opaque, 64 px with a 1 px border (`scroll-padding-top` in index.css keeps
 * focused elements clear of it); hamburger below lg, breadcrumbs, then on project pages the live indicator and the
 * version switcher, the page's primary action (`HeaderAction`), search below lg, the bell.
 */
export function Header({
  nav,
  onOpenDrawer,
  onOpenPalette,
  actionSlotRef,
}: {
  nav: ShellNav
  onOpenDrawer: () => void
  onOpenPalette: () => void
  actionSlotRef?: (element: HTMLElement | null) => void
}) {
  return (
    <header data-testid="app-header" className="sticky top-0 z-30 border-b border-border bg-background">
      <div className="flex h-16 items-center gap-1 px-2 sm:gap-2 sm:px-4 lg:px-6">
        <button
          type="button"
          onClick={onOpenDrawer}
          aria-label="Open navigation"
          className="flex size-11 shrink-0 items-center justify-center rounded-lg hover:bg-accent lg:hidden"
        >
          <Menu className="size-5" aria-hidden />
        </button>
        <Breadcrumbs nav={nav} className="flex-1" />
        <div className="flex shrink-0 items-center gap-0.5 sm:gap-1">
          {/* Only once the project loaded: a 403/404 project has no live updates. */}
          {nav.projectId && nav.projectName !== undefined && (
            <div className="mr-1 flex items-center gap-2" data-slot="project-header-tools">
              <ProjectLive projectId={nav.projectId} />
              <VersionSwitcher projectId={nav.projectId} />
            </div>
          )}
          <div ref={actionSlotRef} data-slot="header-actions" className="flex items-center gap-2 empty:hidden sm:mr-1" />
          <button
            type="button"
            onClick={onOpenPalette}
            aria-label="Search pages"
            className="flex size-11 items-center justify-center rounded-lg hover:bg-accent lg:hidden"
          >
            <Search className="size-5" aria-hidden />
          </button>
          <NotificationBell />
        </div>
      </div>
    </header>
  )
}
