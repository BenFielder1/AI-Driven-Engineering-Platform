import { createFileRoute } from '@tanstack/react-router'

import { ApprovalPage } from '../../../features/approvals/approval-page'

/**
 * FR-48: one-click approval links. Under the authenticated layout, so a signed-out user goes to
 * /login?returnUrl=/approvals/<token> and returns here after signing in.
 */
export const Route = createFileRoute('/_app/approvals/$token')({
  component: ApprovalRoute,
})

function ApprovalRoute() {
  const { token } = Route.useParams()
  // Keyed by token: a different link starts a fresh confirmation.
  return <ApprovalPage key={token} token={token} />
}
