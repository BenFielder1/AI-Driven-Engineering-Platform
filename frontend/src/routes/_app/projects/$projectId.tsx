import { createFileRoute, Outlet, retainSearchParams } from '@tanstack/react-router'

import { ProjectLayout } from '../../../features/projects/project-layout'
import { parseReleaseSearch } from '../../../features/versions/use-selected-release-version'

interface ProjectSearch {
  /** FR-62: a past release version to view read-only (`?release=1.1.0`). */
  release?: string
}

export const Route = createFileRoute('/_app/projects/$projectId')({
  validateSearch: (search: Record<string, unknown>): ProjectSearch => ({ release: parseReleaseSearch(search.release) }),
  // The picked version stays on screen while moving between the project's pages (sidebar, stepper, links).
  search: { middlewares: [retainSearchParams(['release'])] },
  component: ProjectRoute,
})

function ProjectRoute() {
  const { projectId } = Route.useParams()
  return (
    <ProjectLayout projectId={projectId}>
      <Outlet />
    </ProjectLayout>
  )
}
