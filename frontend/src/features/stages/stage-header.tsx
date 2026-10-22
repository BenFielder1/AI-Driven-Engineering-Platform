import type { ReactNode } from 'react'

import type { Schemas } from '../../api/client'
import { StatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { phaseLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { stageBadge, stageProgress, type StageBadge } from './stage-model'

type Phase = Schemas['Phase']

const badgeCopy: Record<StageBadge, { label: string; tone: StatusTone }> = {
  not_started: { label: 'Not started', tone: 'neutral' },
  in_progress: { label: 'In progress', tone: 'info' },
  awaiting_approval: { label: 'Awaiting approval', tone: 'warning' },
  done: { label: 'Done', tone: 'success' },
  read_only: { label: 'Read-only', tone: 'neutral' },
}

/**
 * FR-55: the stage page heading: the stage name, its state badge, the release version on screen (SPEC §7.2: every
 * view says which release version it belongs to) and any stage-level actions.
 */
export function StageHeader({
  stage,
  awaitingApproval = false,
  description,
  actions,
}: {
  stage: Phase
  awaitingApproval?: boolean
  description?: ReactNode
  actions?: ReactNode
}) {
  const { project, readOnly, view } = useProjectAccess()
  const badge = badgeCopy[stageBadge(stageProgress(stage, project, readOnly), readOnly, awaitingApproval)]
  const release = view.version?.version ?? project.developmentVersion ?? project.releasedVersion
  return (
    <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
      <div className="flex min-w-0 flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <h2 id={`${stage}-title`} className="text-xl">
            {phaseLabels[stage]}
          </h2>
          <span data-testid="stage-status">
            <StatusBadge tone={badge.tone} pulse={badge.label === 'In progress'}>
              {badge.label}
            </StatusBadge>
          </span>
        </div>
        {release && <p className="text-sm text-muted-foreground">Release version {release}</p>}
        {description && <div className="text-sm text-muted-foreground">{description}</div>}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </div>
  )
}
