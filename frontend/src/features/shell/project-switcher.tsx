import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { ArrowLeft, Check, ChevronsUpDown } from 'lucide-react'
import { useState, type ReactNode } from 'react'

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '../../components/ui/dropdown-menu'
import { cn } from '../../lib/utils'
import { projectsQueryOptions } from '../projects/projects-api'
import { whenCollapsed } from './collapse-classes'
import type { SidebarMode } from './use-sidebar-preference'

const SWITCHER_LIMIT = 20

/**
 * FR-54: the project switcher at the top of project mode. Shows the project's name and a slot for its version badges
 * (`badges`; released + in-development arrive with FR-60), and lists the user's projects when opened.
 */
export function ProjectSwitcher({
  projectId,
  projectName,
  badges,
  mode,
  onNavigate,
}: {
  projectId: string
  projectName: string | undefined
  badges?: ReactNode
  mode: SidebarMode
  onNavigate?: () => void
}) {
  const [open, setOpen] = useState(false)
  const projects = useQuery({
    ...projectsQueryOptions({ page: 1, limit: SWITCHER_LIMIT, sortBy: 'updated', sortOrder: 'desc' }),
    enabled: open,
  })
  const name = projectName ?? 'Project'

  return (
    <div className="flex flex-col gap-1">
      <DropdownMenu open={open} onOpenChange={setOpen}>
        <DropdownMenuTrigger
          aria-label={`Switch project. Current: ${name}`}
          className={cn(
            'flex min-h-11 w-full min-w-0 items-center gap-2.5 rounded-lg border border-border/60 bg-background/40 px-2 text-left hover:bg-sidebar-accent/60',
            whenCollapsed.center[mode],
          )}
        >
          <span
            aria-hidden
            className="flex size-7 shrink-0 items-center justify-center rounded-md bg-chart-2/15 font-heading text-sm text-foreground"
          >
            {name.trim().charAt(0).toUpperCase() || '?'}
          </span>
          <span className={cn('flex min-w-0 flex-1 flex-col gap-1 py-1.5', whenCollapsed.hidden[mode])}>
            <span className="truncate text-sm font-medium" data-testid="project-switcher-name">
              {name}
            </span>
            {badges && <span className="flex flex-wrap gap-1">{badges}</span>}
          </span>
          <ChevronsUpDown aria-hidden className={cn('size-4 shrink-0 text-muted-foreground', whenCollapsed.hidden[mode])} />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-72">
          <DropdownMenuLabel className="text-xs text-muted-foreground">Your projects</DropdownMenuLabel>
          {projects.isPending && <p className="px-2 py-3 text-sm text-muted-foreground">Loading projects…</p>}
          {projects.isError && <p className="px-2 py-3 text-sm text-destructive">Your projects couldn&apos;t load.</p>}
          {projects.isSuccess &&
            projects.data.items.map((p) => (
              <DropdownMenuItem key={p.id} asChild>
                <Link to="/projects/$projectId" params={{ projectId: p.id }} onClick={onNavigate}>
                  <span className="min-w-0 flex-1 truncate">{p.name}</span>
                  {p.id === projectId ? <Check className="size-4" aria-label="Current project" /> : <span className="size-4" aria-hidden />}
                </Link>
              </DropdownMenuItem>
            ))}
          <DropdownMenuSeparator />
          <DropdownMenuItem asChild>
            <Link to="/projects" activeOptions={{ exact: true }} onClick={onNavigate}>
              View all projects
            </Link>
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
      <Link
        to="/projects"
        activeOptions={{ exact: true }}
        onClick={onNavigate}
        title={mode === 'expanded' ? undefined : 'All projects'}
        className={cn(
          'flex min-h-11 items-center gap-3 rounded-lg px-3 text-sm text-muted-foreground hover:bg-sidebar-accent/60 hover:text-foreground',
          whenCollapsed.center[mode],
        )}
      >
        <ArrowLeft aria-hidden className="size-4 shrink-0" />
        <span className={whenCollapsed.srOnly[mode]}>All projects</span>
      </Link>
    </div>
  )
}
