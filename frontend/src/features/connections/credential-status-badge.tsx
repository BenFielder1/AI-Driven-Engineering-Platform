import type { Schemas } from '../../api/client'
import { StatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { credentialStatusLabels } from './connection-format'

type Status = Schemas['LlmCredentialStatus'] | Schemas['PlatformConnectionStatus']

const statusTones: Record<Status, StatusTone> = {
  valid: 'success',
  invalid: 'destructive',
  insufficient_scope: 'warning',
  unchecked: 'neutral',
}

/** FR-78, FR-142: a connection's validation status as a badge (text, not colour alone). */
export function CredentialStatusBadge({ status }: { status: Status }) {
  return (
    <StatusBadge tone={statusTones[status]}>
      <span data-testid="credential-status">{credentialStatusLabels[status]}</span>
    </StatusBadge>
  )
}
