import { createFileRoute } from '@tanstack/react-router'

import { DeploymentsPage } from '../../../../features/deployments/deployments-page'

/** /projects/:id/deployments (FR-91, SPEC §7.2). */
export const Route = createFileRoute('/_app/projects/$projectId/deployments')({
  component: DeploymentsPage,
})
