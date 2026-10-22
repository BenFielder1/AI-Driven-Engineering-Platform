import { Link } from '@tanstack/react-router'
import { Check } from 'lucide-react'
import { useId } from 'react'

import { cn } from '../../lib/utils'
import { useUnreadCount } from '../notifications/notifications-api'
import { hrefFor, matchesHref, type NavItem, type NavSection } from './nav-config'
import type { StageStatus } from './stage-states'
import { whenCollapsed } from './collapse-classes'
import type { SidebarMode } from './use-sidebar-preference'

const stageText = { done: 'Done', current: 'Current stage', upcoming: 'Upcoming' } as const

function UnreadBadge({ mode }: { mode: SidebarMode }) {
  const unread = useUnreadCount()
  const count = unread.data ?? 0
  if (count === 0) {
    return null
  }
  // aria-hidden: the bell's accessible name carries the exact count, and the link keeps the plain name "Notifications".
  return (
    <span
      aria-hidden
      data-testid="nav-unread-badge"
      className={cn(
        'flex h-5 min-w-5 items-center justify-center rounded-full bg-primary px-1.5 font-mono text-[11px] leading-none font-semibold text-primary-foreground',
        whenCollapsed.corner[mode],
      )}
    >
      {count > 99 ? '99+' : count}
    </span>
  )
}

/** The stage's state marker (✓, pulsing dot) and needs-action count (FR-54), described to assistive tech. */
function StageMarkers({ status, mode }: { status: StageStatus; mode: SidebarMode }) {
  return (
    <>
      {status.needsAction > 0 && (
        <span
          aria-hidden
          data-testid="stage-needs-action"
          className={cn(
            'flex h-5 min-w-5 items-center justify-center rounded-full bg-warning px-1.5 font-mono text-[11px] leading-none font-semibold text-warning-foreground',
            whenCollapsed.corner[mode],
          )}
        >
          {status.needsAction}
        </span>
      )}
      {status.state === 'done' && (
        <Check aria-hidden data-testid="stage-done" className={cn('size-4 text-success', whenCollapsed.hidden[mode])} />
      )}
      {status.state === 'current' && (
        <span
          aria-hidden
          data-testid="stage-current"
          className={cn('size-2 animate-pulse rounded-full bg-primary shadow-[0_0_8px_var(--primary)]', whenCollapsed.hidden[mode])}
        />
      )}
    </>
  )
}

function ItemLink({
  item,
  projectId,
  pathname,
  mode,
  stage,
  onNavigate,
}: {
  item: NavItem
  projectId?: string
  pathname: string
  mode: SidebarMode
  stage?: StageStatus
  onNavigate?: () => void
}) {
  const descriptionId = useId()
  const href = hrefFor(item, { projectId })
  const active = matchesHref(pathname, href, item.exact)
  const Icon = item.icon
  // FR-54: done and current stages read as foreground; upcoming stages (and plain items) stay muted.
  const reached = stage !== undefined && stage.state !== 'upcoming'
  const link = (
    <Link
      to={item.to}
      params={projectId ? { projectId } : {}}
      activeOptions={{ exact: item.exact === true, includeSearch: false }}
      onClick={onNavigate}
      aria-current={active ? 'page' : undefined}
      aria-describedby={stage ? descriptionId : undefined}
      title={mode === 'expanded' ? undefined : item.label}
      data-stage-state={stage?.state}
      className={cn(
        'group relative flex min-h-11 min-w-0 flex-1 items-center gap-3 rounded-lg px-3 text-sm transition-colors',
        'focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none',
        active
          ? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground'
          : cn('hover:bg-sidebar-accent/60 hover:text-foreground', reached ? 'text-sidebar-foreground' : 'text-muted-foreground'),
        whenCollapsed.center[mode],
      )}
    >
      {active && (
        <span
          aria-hidden
          className="absolute top-1/2 left-1 size-1.5 -translate-y-1/2 animate-pulse rounded-full bg-primary shadow-[0_0_8px_var(--primary)]"
        />
      )}
      <Icon aria-hidden className={cn('size-4 shrink-0', active && 'text-primary')} />
      <span className={cn('min-w-0 flex-1 truncate', whenCollapsed.srOnly[mode])}>{item.label}</span>
      {item.badge === 'unread' && <UnreadBadge mode={mode} />}
      {stage && <StageMarkers status={stage} mode={mode} />}
    </Link>
  )
  if (!stage) {
    return link
  }
  // Outside the link, so the stage state is its description and its name stays the stage label.
  return (
    <>
      {link}
      <span id={descriptionId} className="sr-only">
        {stageText[stage.state]}
        {stage.needsAction > 0 ? `, ${String(stage.needsAction)} awaiting action` : ''}
      </span>
    </>
  )
}

/** Sidebar / drawer navigation built from nav-config (FR-53, FR-54). */
export function NavList({
  sections,
  pathname,
  projectId,
  stages,
  mode,
  onNavigate,
}: {
  sections: readonly NavSection[]
  pathname: string
  projectId?: string
  stages?: readonly StageStatus[]
  mode: SidebarMode
  onNavigate?: () => void
}) {
  return (
    <div className="flex flex-col gap-5">
      {sections.map((section) => (
        <SectionBlock
          key={section.key}
          section={section}
          pathname={pathname}
          projectId={projectId}
          stages={stages}
          mode={mode}
          onNavigate={onNavigate}
        />
      ))}
    </div>
  )
}

function SectionBlock({
  section,
  pathname,
  projectId,
  stages,
  mode,
  onNavigate,
}: {
  section: NavSection
  pathname: string
  projectId?: string
  stages?: readonly StageStatus[]
  mode: SidebarMode
  onNavigate?: () => void
}) {
  // useId: the sidebar and the drawer can both be in the DOM.
  const labelId = useId()
  return (
    <div className="flex flex-col gap-1" data-section={section.key}>
      <p
        id={labelId}
        className={cn('px-3 text-[11px] font-semibold tracking-wider text-muted-foreground uppercase', whenCollapsed.srOnly[mode])}
      >
        {section.label}
      </p>
      <ul aria-labelledby={labelId} className="flex flex-col gap-0.5">
        {section.items.map((item) => (
          <li key={item.key} className="flex">
            <ItemLink
              item={item}
              projectId={projectId}
              pathname={pathname}
              mode={mode}
              stage={item.stage ? stages?.find((s) => s.stage === item.stage) : undefined}
              onNavigate={onNavigate}
            />
          </li>
        ))}
      </ul>
    </div>
  )
}
