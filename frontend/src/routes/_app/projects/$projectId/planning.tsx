import { createFileRoute } from '@tanstack/react-router'

import { validateArtifactVersionSearch } from '../../../../features/artifacts/artifact-stage-route'
import { PlanningPage } from '../../../../features/stages/artifact-stage-page'

/** /projects/:id/planning: the planning stage page (FR-55); `?version=N` picks the artifact version shown. */
export const Route = createFileRoute('/_app/projects/$projectId/planning')({
  validateSearch: validateArtifactVersionSearch,
  component: StageRoute,
})

function StageRoute() {
  const { version } = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <PlanningPage
      version={version}
      onVersionChange={(next) => {
        void navigate({ search: (prev) => ({ ...prev, version: next }) })
      }}
    />
  )
}
