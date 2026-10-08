import { createFileRoute, Outlet } from '@tanstack/react-router'

import { ProjectLayout } from '../../../features/projects/project-layout'

export const Route = createFileRoute('/_app/projects/$projectId')({
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
