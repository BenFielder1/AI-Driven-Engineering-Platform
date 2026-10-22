import { StatusBadge as SharedStatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { statusLabels } from './labels'
import type { ProjectStatus } from './projects-api'

export { PhaseBadge } from '../../components/shared/phase-badge'

const statusTones: Record<ProjectStatus, StatusTone> = {
  active: 'neutral',
  building: 'info',
  reviewing: 'warning',
  releasing: 'warning',
  // FR-65: waiting on a merge on GitHub (the merge poll is running).
  awaiting_merge: 'warning',
  released: 'success',
  rolled_back: 'destructive',
}

/** The project status as a StatusBadge (SPEC §7.1); running states pulse. */
export function StatusBadge({ status }: { status: ProjectStatus }) {
  return (
    <SharedStatusBadge tone={statusTones[status]} pulse={status === 'building' || status === 'reviewing' || status === 'releasing' || status === 'awaiting_merge'}>
      {statusLabels[status]}
    </SharedStatusBadge>
  )
}
