'use client'

import { ProjectsListPage } from '../../../features/projects/projects-list-page'
import { parseProjectsSearch } from '../../../features/projects/projects-search'
import { useSearch, useSetSearch } from '../../../lib/navigation'

/** /projects (FR-6): search, filters, sort and page live in the URL; changes replace the history entry. */
export default function ProjectsRoute() {
  const search = useSearch(parseProjectsSearch)
  const setSearch = useSetSearch()
  return (
    <ProjectsListPage
      search={search}
      onSearchChange={(next) => {
        void setSearch(next, { replace: true })
      }}
    />
  )
}
