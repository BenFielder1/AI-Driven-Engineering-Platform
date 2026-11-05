import { FolderGit2, Plus } from 'lucide-react'
import { useId, type ReactNode } from 'react'

import { EmptyState } from '../../components/shared/empty-state'
import { PageHeader } from '../../components/shared/page-header'
import { panelClass } from '../../components/shared/panel-class'
import { SkeletonTable } from '../../components/shared/skeletons'
import { ProjectVersionBadges } from '../../components/shared/version-badge'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { ErrorState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import type { User } from '../auth/auth-api'
import { NotificationItem } from '../notifications/notification-item'
import { PhaseBadge, StatusBadge } from '../projects/project-badges'
import { RelativeTime } from '../projects/relative-time'
import { setChecklistDismissed, useChecklistDismissed } from './checklist-dismissed'
import { useRecentProjects, useUnreadNotifications } from './home-api'
import { SetupChecklist } from './setup-checklist'

function HomePanel({ title, viewAll, children }: { title: string; viewAll?: ReactNode; children: ReactNode }) {
  const headingId = useId()
  return (
    <section aria-labelledby={headingId} className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div className="flex items-center justify-between gap-3">
        <h2 id={headingId} className="text-lg">
          {title}
        </h2>
        {viewAll}
      </div>
      {children}
    </section>
  )
}

const viewAllClass = 'inline-flex min-h-6 items-center text-sm font-medium text-primary underline-offset-4 hover:underline'

function RecentProjects() {
  const projects = useRecentProjects()
  return (
    <HomePanel
      title="Recent projects"
      viewAll={
        projects.data && projects.data.total > 0 ? (
          <Link to="/projects" className={viewAllClass} aria-label="View all projects">
            View all
          </Link>
        ) : undefined
      }
    >
      {projects.isPending && <SkeletonTable rows={3} columns={3} />}
      {projects.isError && <ErrorState error={projects.error} onRetry={() => void projects.refetch()} title="Your projects couldn't load" />}
      {projects.isSuccess && projects.data.items.length === 0 && (
        <EmptyState
          variant="no_data"
          bare
          headingLevel={3}
          title="No projects yet"
          description="A project takes an idea, or an existing repository, from refinement to release."
          action={
            <>
              <Link to="/projects/new" className={buttonClasses('primary')}>
                <Plus aria-hidden className="size-4" />
                Create project
              </Link>
              <Link to="/projects/new" search={{ source: 'import' }} className={buttonClasses('secondary')}>
                <FolderGit2 aria-hidden className="size-4" />
                Import a repository
              </Link>
            </>
          }
        />
      )}
      {projects.isSuccess && projects.data.items.length > 0 && (
        <ul aria-label="Recent projects" className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {projects.data.items.map((p) => (
            <li key={p.id} className="flex flex-col gap-2 rounded-lg border border-border bg-background p-3">
              <Link
                to="/projects/$projectId"
                params={{ projectId: p.id }}
                search={{ release: undefined }}
                className="font-medium break-words text-primary underline-offset-4 hover:underline"
              >
                {p.name}
              </Link>
              <div className="flex flex-wrap items-center gap-1.5">
                <PhaseBadge phase={p.currentPhase} />
                <StatusBadge status={p.status} />
                <ProjectVersionBadges released={p.releasedVersion} development={p.developmentVersion} />
              </div>
              <p className="text-xs text-muted-foreground">
                Updated <RelativeTime iso={p.updatedAt} />
              </p>
            </li>
          ))}
        </ul>
      )}
    </HomePanel>
  )
}

function UnreadNotifications() {
  const unread = useUnreadNotifications()
  return (
    <HomePanel
      title="Unread notifications"
      viewAll={
        <Link to="/notifications" search={{ unread: true }} className={viewAllClass} aria-label="View all notifications">
          View all
        </Link>
      }
    >
      {unread.isPending && <SkeletonTable rows={3} columns={2} />}
      {unread.isError && (
        <ErrorState error={unread.error} onRetry={() => void unread.refetch()} title="Your notifications couldn't load" />
      )}
      {unread.isSuccess && unread.data.items.length === 0 && (
        <EmptyState variant="no_data" bare headingLevel={3} title="You're all caught up" description="Gate decisions, reviews and releases on your projects appear here." />
      )}
      {unread.isSuccess && unread.data.items.length > 0 && (
        <ul aria-label="Unread notifications" className="divide-y divide-border overflow-hidden rounded-lg border border-border">
          {unread.data.items.map((n) => (
            <NotificationItem key={n.id} notification={n} />
          ))}
        </ul>
      )}
    </HomePanel>
  )
}

/**
 * /home (FR-207): the signed-in landing page. A greeting, the "Get set up" checklist (§4.30; dismissable, stored in
 * `aide.home.checklist-dismissed`), Recent projects (up to 6, last updated first) and Unread notifications (up to 5).
 * Each panel loads, fails and retries on its own.
 */
export function HomePage({ user }: { user: User }) {
  const dismissed = useChecklistDismissed()
  const firstName = user.name.trim().split(/\s+/).find((part) => part !== '') ?? user.name
  return (
    <>
      <PageHeader
        title={`Welcome, ${firstName}`}
        documentTitle="Home"
        description="Pick up where you left off, or finish setting up."
        className="mb-6"
        actions={
          dismissed ? (
            <Button
              variant="secondary"
              onClick={() => {
                setChecklistDismissed(false)
              }}
            >
              Show setup checklist
            </Button>
          ) : undefined
        }
      />
      <div className="flex flex-col gap-6">
        {!dismissed && <SetupChecklist user={user} />}
        <div className="grid grid-cols-1 gap-6 xl:grid-cols-[3fr_2fr]">
          <RecentProjects />
          <UnreadNotifications />
        </div>
      </div>
    </>
  )
}
