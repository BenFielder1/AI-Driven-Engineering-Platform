import { Badge, type BadgeTone } from '../../components/ui/badge'
import { runStatusLabels } from '../projects/labels'
import type { AgentRunStatus } from './runs-api'

const tones: Record<AgentRunStatus, BadgeTone> = {
  queued: 'neutral',
  running: 'info',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'warning',
}

/** FR-23: run status as text (colour is decorative). A running run shows "Running…": nothing streams in v1. */
export function RunStatusBadge({ status }: { status: AgentRunStatus }) {
  return <Badge tone={tones[status]}>{runStatusLabels[status]}</Badge>
}
