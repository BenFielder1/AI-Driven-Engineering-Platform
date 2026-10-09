import { createFileRoute } from '@tanstack/react-router'

import { RefinementPage } from '../../../../features/refinement/refinement-page'

export const Route = createFileRoute('/_app/projects/$projectId/refinement')({
  component: RefinementPage,
})
