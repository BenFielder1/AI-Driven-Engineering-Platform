'use client'

import { ArtifactDetailPage } from '../../../../../../features/artifacts/artifact-detail-page'
import { validateArtifactVersionSearch } from '../../../../../../features/artifacts/artifact-stage-route'
import { ArtifactTypeNotFound } from '../../../../../../features/artifacts/artifact-type-not-found'
import { artifactTypes } from '../../../../../../features/projects/labels'
import { oneOf } from '../../../../../../features/projects/projects-search'
import { useParams, useSearch, useSetSearch } from '../../../../../../lib/navigation'

/** /projects/:id/artifacts/:type: one artifact's versions; `?version=N` picks the version shown. */
export default function ArtifactRoute() {
  const params = useParams<{ projectId: string; type: string }>()
  const { version } = useSearch(validateArtifactVersionSearch)
  const setSearch = useSetSearch()
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
        void setSearch({ version: next })
      }}
    />
  )
}
