import { Link, useMatchRoute } from '@tanstack/react-router'
import type { ReactNode } from 'react'

import { buttonClasses } from '../../components/ui/button-classes'
import { ErrorState, ForbiddenState, LoadingState } from '../../components/ui/states'
import { isApiError } from '../../lib/errors'
import { ProjectLive } from '../live/live-indicator'
import { useDocumentTitle } from '../../lib/use-document-title'
import { PhaseTimeline, StatusBadge } from './project-badges'
import { ProjectContext } from './project-context'
import { useProject } from './projects-api'

const tabs = [
  { to: '/projects/$projectId', label: 'Overview', exact: true },
  { to: '/projects/$projectId/refinement', label: 'Refinement', exact: false },
  { to: '/projects/$projectId/artifacts', label: 'Artifacts', exact: false },
  { to: '/projects/$projectId/execution', label: 'Execution', exact: false },
  { to: '/projects/$projectId/review', label: 'Review', exact: false },
  { to: '/projects/$projectId/runs', label: 'Runs', exact: false },
  { to: '/projects/$projectId/settings', label: 'Settings', exact: false },
] as const

function ProjectNotFound() {
  useDocumentTitle('Project not found')
  return (
    <section aria-labelledby="project-not-found" className="py-6">
      <h1 id="project-not-found" className="text-2xl font-semibold tracking-tight">
        Project not found
      </h1>
      <p className="mt-2 text-muted">
        It may have been deleted, or you may not be a member. Ask a project Owner to add you.
      </p>
      <Link to="/projects" className={buttonClasses('secondary', 'mt-4')}>
        Back to projects
      </Link>
    </section>
  )
}

/** /projects/:id layout: loads the project, shows its header and section links, and handles 403/404. */
export function ProjectLayout({ projectId, children }: { projectId: string; children: ReactNode }) {
  const project = useProject(projectId)
  // The Overview renders the detailed phase timeline itself, so the compact one is shown on the other sections only.
  const onOverview = Boolean(useMatchRoute()({ to: '/projects/$projectId', params: { projectId } }))

  if (project.isPending) {
    return <LoadingState label="Loading project…" />
  }
  if (project.isError) {
    if (isApiError(project.error, 'NOT_FOUND')) {
      return <ProjectNotFound />
    }
    if (isApiError(project.error, 'FORBIDDEN')) {
      return <ForbiddenState>You aren&apos;t a member of this project. Ask a project Owner to add you.</ForbiddenState>
    }
    return <ErrorState error={project.error} onRetry={() => void project.refetch()} title="The project couldn't load" />
  }

  const p = project.data
  return (
    <ProjectContext value={p}>
      <header className="mb-6 flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <h1 className="break-words text-2xl font-semibold tracking-tight">{p.name}</h1>
          <StatusBadge status={p.status} />
          <span className="text-sm text-muted">Version {p.projectVersion}</span>
          <ProjectLive projectId={projectId} />
        </div>
        {!onOverview && <PhaseTimeline current={p.currentPhase} />}
        <nav aria-label="Project sections">
          <ul className="flex flex-wrap gap-1 border-b border-border">
            {tabs.map((tab) => (
              <li key={tab.to}>
                <Link
                  to={tab.to}
                  params={{ projectId }}
                  activeOptions={{ exact: tab.exact, includeSearch: false }}
                  className="-mb-px inline-flex min-h-10 items-center border-b-2 border-transparent px-3 py-2 text-sm font-medium text-muted hover:text-fg"
                  activeProps={{ className: 'border-primary! text-fg!', 'aria-current': 'page' }}
                >
                  {tab.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      </header>
      {children}
    </ProjectContext>
  )
}
