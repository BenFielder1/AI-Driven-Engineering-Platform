import { createFileRoute } from '@tanstack/react-router'

import { ProjectPlaceholderPage } from '../../../../features/projects/project-overview-page'

export const Route = createFileRoute('/_app/projects/$projectId/review')({
  component: () => <ProjectPlaceholderPage title="Review" />,
})
