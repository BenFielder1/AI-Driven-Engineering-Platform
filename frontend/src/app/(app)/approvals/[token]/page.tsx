'use client'

import { ApprovalPage } from '../../../../features/approvals/approval-page'
import { useParams } from '../../../../lib/navigation'

/**
 * FR-48: one-click approval links. Under the authenticated layout, so a signed-out user goes to
 * /login?returnUrl=/approvals/<token> and returns here after signing in.
 */
export default function ApprovalRoute() {
  const { token } = useParams<{ token: string }>()
  // Keyed by token: a different link starts a fresh confirmation.
  return <ApprovalPage key={token} token={token} />
}
