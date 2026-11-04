'use client'

import { NewProjectWizard } from '../../../../features/projects/new-project-wizard'
import { useSearch } from '../../../../lib/navigation'

/** /projects/new: the new-project wizard. `?source=import` starts on "Import an existing repository" (FR-207, FR-209). */
export default function NewProjectRoute() {
  const { source } = useSearch((raw) => ({ source: raw.source === 'import' ? ('import' as const) : undefined }))
  return <NewProjectWizard initialSource={source} />
}
