'use client'

import { validateArtifactVersionSearch } from '../../../../../features/artifacts/artifact-stage-route'
import { SpecificationPage } from '../../../../../features/stages/artifact-stage-page'
import { useSearch, useSetSearch } from '../../../../../lib/navigation'

/** /projects/:id/specification: the specification stage page (FR-55); `?version=N` picks the artifact version shown. */
export default function SpecificationPageRoute() {
  const { version } = useSearch(validateArtifactVersionSearch)
  const setSearch = useSetSearch()
  return (
    <SpecificationPage
      version={version}
      onVersionChange={(next) => {
        void setSearch((prev) => ({ ...prev, version: next }))
      }}
    />
  )
}
