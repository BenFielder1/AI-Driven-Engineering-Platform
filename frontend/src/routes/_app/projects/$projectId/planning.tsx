import { createFileRoute } from '@tanstack/react-router'

import { ArtifactDetailPage } from '../../../../features/artifacts/artifact-detail-page'
import { validateArtifactVersionSearch } from '../../../../features/artifacts/artifact-stage-route'

/** FR-54 stage route: the implementation plan (the v1 artifact view, with its steps) until the FR-55 stage page. */
export const Route = createFileRoute('/_app/projects/$projectId/planning')({
  validateSearch: validateArtifactVersionSearch,
  component: PlanningRoute,
})

function PlanningRoute() {
  const { version } = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <ArtifactDetailPage
      type="implementation_plan"
      version={version}
      onVersionChange={(next) => {
        void navigate({ search: { version: next } })
      }}
    />
  )
}
