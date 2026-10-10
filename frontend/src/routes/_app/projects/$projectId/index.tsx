import { createFileRoute } from '@tanstack/react-router'

import { ProjectOverviewPage } from '../../../../features/projects/project-overview-page'

export const Route = createFileRoute('/_app/projects/$projectId/')({
  component: ProjectOverviewPage,
})
