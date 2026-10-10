import { createFileRoute } from '@tanstack/react-router'

import { ArtifactDetailPage } from '../../../../../features/artifacts/artifact-detail-page'
import { ArtifactTypeNotFound } from '../../../../../features/artifacts/artifact-type-not-found'
import { artifactTypes } from '../../../../../features/projects/labels'
import { oneOf } from '../../../../../features/projects/projects-search'

interface ArtifactSearch {
  version?: number
}

export const Route = createFileRoute('/_app/projects/$projectId/artifacts/$type')({
  validateSearch: (search: Record<string, unknown>): ArtifactSearch => {
    const version = typeof search.version === 'number' ? search.version : Number(search.version)
    return { version: Number.isInteger(version) && version > 0 ? version : undefined }
  },
  component: ArtifactRoute,
})

function ArtifactRoute() {
  const params = Route.useParams()
  const { version } = Route.useSearch()
  const navigate = Route.useNavigate()
  const type = oneOf(artifactTypes, params.type)
  if (!type) {
    return <ArtifactTypeNotFound projectId={params.projectId} />
  }
  return (
    <ArtifactDetailPage
      key={type}
      type={type}
      version={version}
      onVersionChange={(next) => {
        void navigate({ search: { version: next } })
      }}
    />
  )
}
