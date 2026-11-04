import { Check } from 'lucide-react'

import { LifecycleStatusBadge, StatusBadge } from '../../../../components/shared/status-badge'
import { cn } from '../../../../lib/utils'
import { DiffView } from '../../../artifacts/diff-view'
import { phaseLabels } from '../../../projects/phase-labels'
import { previewArtifactHash, previewDiff, previewGate, previewLabels, previewRail } from '../../content/previews'
import { FakeButton } from './preview-frame'

/**
 * FR-220: preview building blocks. The rail is a slim presentational copy of the stage stepper
 * (features/lifecycle/stage-stepper.tsx), which is wired to the router and project routes; badges and the diff are the
 * real components (StatusBadge, LifecycleStatusBadge, DiffView) with fixture data.
 */
export function RailPreview() {
  return (
    <ol className="flex flex-wrap items-center gap-1">
      {previewRail.map((s, i) => (
        <li key={s.phase} className="flex items-center gap-1">
          {i > 0 && <span className={cn('h-px w-2 sm:w-4', s.state === 'upcoming' ? 'bg-border' : 'bg-primary/60')} />}
          <span
            className={cn(
              'relative inline-flex h-7 items-center gap-1.5 rounded-full border px-2.5 text-xs whitespace-nowrap',
              s.state === 'current' && 'border-primary/60 bg-primary/10 font-medium text-foreground',
              s.state === 'done' && 'border-border bg-card text-foreground',
              s.state === 'upcoming' && 'border-dashed border-border text-muted-foreground',
            )}
          >
            {s.state === 'done' && <Check className="size-3 text-success" />}
            {s.state === 'current' && <span className="bg-brand-gradient size-1.5 rounded-full motion-safe:animate-pulse" />}
            {phaseLabels[s.phase]}
          </span>
        </li>
      ))}
    </ol>
  )
}

export function GatePreview() {
  return (
    <div className="rounded-lg border border-warning/50 bg-warning/5 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm font-semibold">{previewGate.title}</p>
        <StatusBadge tone="warning">{previewLabels.awaitingApproval}</StatusBadge>
      </div>
      <p className="mt-1 text-xs text-muted-foreground">
        {previewGate.artifact} · {previewLabels.from} {previewGate.requestedBy}
      </p>
      <p className="mt-2 text-xs">{previewGate.reason}</p>
      <div className="mt-3 flex gap-2">
        <FakeButton primary>{previewLabels.approve}</FakeButton>
        <FakeButton>{previewLabels.reject}</FakeButton>
      </div>
    </div>
  )
}

export function DiffPreview() {
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <span className="font-medium">{previewLabels.artifactType}</span>
        <LifecycleStatusBadge status="in_review">{previewLabels.draft}</LifecycleStatusBadge>
        <LifecycleStatusBadge status="locked">{previewLabels.locked}</LifecycleStatusBadge>
        <span className="font-mono text-muted-foreground">{previewArtifactHash}</span>
      </div>
      <div className="text-xs [&_caption]:text-xs [&_table]:min-w-0 [&_table]:text-xs">
        <DiffView diff={previewDiff} />
      </div>
    </div>
  )
}
