import { Badge, type BadgeTone } from '../../components/ui/badge'
import { artifactStatusLabels } from '../projects/labels'
import type { ArtifactStatus, ArtifactSummary } from './artifacts-api'

const statusTones: Record<ArtifactStatus, BadgeTone> = {
  draft: 'info',
  locked: 'success',
  rejected: 'danger',
  superseded: 'neutral',
  invalidated: 'warning',
  corrupted: 'danger',
}

export function ArtifactStatusBadge({ status }: { status: ArtifactSummary['status'] }) {
  if (!status) {
    return <Badge>Unknown</Badge>
  }
  return <Badge tone={statusTones[status]}>{artifactStatusLabels[status]}</Badge>
}

/**
 * FR-20: the result of the server's hash re-check on this read. Corrupted content is flagged with text (not colour
 * alone) and a warning sign.
 */
export function IntegrityBadge({ integrity }: { integrity: ArtifactSummary['integrity'] }) {
  return integrity === 'corrupted' ? (
    <Badge tone="danger">
      <span aria-hidden="true">⚠&nbsp;</span>Integrity check failed
    </Badge>
  ) : (
    <Badge tone="success">Integrity verified</Badge>
  )
}
