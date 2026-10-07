import { createFileRoute } from '@tanstack/react-router'

import { ProjectsPage } from '../../features/placeholders/placeholder-pages'

export const Route = createFileRoute('/_app/projects')({
  component: ProjectsPage,
})
