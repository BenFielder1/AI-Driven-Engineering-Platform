import { createFileRoute } from '@tanstack/react-router'

import { ProjectsListPage } from '../../../features/projects/projects-list-page'
import { parseProjectsSearch, type ProjectsSearch } from '../../../features/projects/projects-search'

export const Route = createFileRoute('/_app/projects/')({
  validateSearch: (search: Record<string, unknown>): ProjectsSearch => parseProjectsSearch(search),
  component: ProjectsRoute,
})

function ProjectsRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <ProjectsListPage
      search={search}
      onSearchChange={(next) => {
        void navigate({ search: next, replace: true })
      }}
    />
  )
}
