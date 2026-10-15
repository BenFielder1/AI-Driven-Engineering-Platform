import { createFileRoute } from '@tanstack/react-router'

import { ArtifactDetailPage } from '../../../../features/artifacts/artifact-detail-page'
import { validateArtifactVersionSearch } from '../../../../features/artifacts/artifact-stage-route'

/** FR-54 stage route: the specification artifact (the v1 artifact view) until the FR-55 stage page replaces it. */
export const Route = createFileRoute('/_app/projects/$projectId/specification')({
  validateSearch: validateArtifactVersionSearch,
  component: SpecificationRoute,
})

function SpecificationRoute() {
  const { version } = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <ArtifactDetailPage
      type="technical_specification"
      version={version}
      onVersionChange={(next) => {
        void navigate({ search: { version: next } })
      }}
    />
  )
}
