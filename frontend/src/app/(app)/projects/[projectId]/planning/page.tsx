'use client'

import { validateArtifactVersionSearch } from '../../../../../features/artifacts/artifact-stage-route'
import { PlanningPage } from '../../../../../features/stages/artifact-stage-page'
import { useSearch, useSetSearch } from '../../../../../lib/navigation'

/** /projects/:id/planning: the planning stage page (FR-55); `?version=N` picks the artifact version shown. */
export default function PlanningPageRoute() {
  const { version } = useSearch(validateArtifactVersionSearch)
  const setSearch = useSetSearch()
  return (
    <PlanningPage
      version={version}
      onVersionChange={(next) => {
        void setSearch((prev) => ({ ...prev, version: next }))
      }}
    />
  )
}
