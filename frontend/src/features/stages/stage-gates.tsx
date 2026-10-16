import { useRef, useState } from 'react'
import { toast } from 'sonner'

import type { Schemas } from '../../api/client'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { EscalationDialog, GateCommentDialog } from '../gates/gate-dialogs'
import { useGates, type Gate } from '../gates/gates-api'
import { GateArtifactRef, GateStepRef } from '../gates/pending-gates-panel'
import { escalationReasonLabels, gateStatusLabels, gateTypeLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { StageSection } from './stage-section'

type GateType = Schemas['GateType']
type OpenDialog = { kind: 'approve' | 'reject' | 'escalation'; gate: Gate } | null

const statusTones: Record<Gate['status'], BadgeTone> = {
  pending: 'info',
  approved: 'success',
  rejected: 'danger',
  cancelled: 'neutral',
}

/** Decided gates shown per stage (the overview keeps the full history). */
const RECENT_DECIDED = 5

/**
 * FR-55: the stage's gates of the release version on screen, decided in place: pending ones first with Approve/Reject
 * (escalations: Decide) for Owners, then the most recent decided ones, each with its release version. A gate belongs to
 * the release version it was created in (`GateDto.releaseVersionId`); a past version (FR-62) lists its gates read-only.
 */
export function StageGates({ types, filter }: { types: readonly GateType[]; filter?: (gate: Gate) => boolean }) {
  const { project, can, readOnly, view } = useProjectAccess()
  const gates = useGates(project.id)
  const [dialog, setDialog] = useState<OpenDialog>(null)
  const headingRef = useRef<HTMLParagraphElement>(null)
  if (types.length === 0) {
    return null
  }
  const canDecide = can('decideGates')
  const versionOf = (gate: Gate) => view.versions.find((v) => v.id === gate.releaseVersionId)
  const shownVersionId = view.version?.id ?? null
  const inShownVersion = (gate: Gate) => shownVersionId === null || gate.releaseVersionId === shownVersionId
  const mine = (gates.data ?? []).filter((g) => types.includes(g.type) && inShownVersion(g) && (filter?.(g) ?? true))
  const pending = mine.filter((g) => g.status === 'pending')
  const decided = mine.filter((g) => g.status !== 'pending').slice(0, RECENT_DECIDED)

  const onDecided = (message: string) => {
    setDialog(null)
    toast.success(message)
    headingRef.current?.focus()
  }

  let body
  if (gates.isPending) {
    body = <LoadingState label="Loading gates…" />
  } else if (gates.isError) {
    body = <ErrorState error={gates.error} title="The gates couldn't load" onRetry={() => void gates.refetch()} />
  } else if (mine.length === 0) {
    body = <p className="text-sm text-muted-foreground">No gates for this stage yet.</p>
  } else {
    body = (
      <>
        {pending.length > 0 && (
          <ul aria-label="Pending stage gates" className="divide-y divide-border/60">
            {pending.map((gate) => (
              <li
                key={gate.id}
                aria-label={gateTypeLabels[gate.type]}
                className="flex flex-col gap-2 py-3 first:pt-0 sm:flex-row sm:items-start sm:justify-between"
              >
                <div className="flex flex-col gap-1 text-sm">
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="font-semibold">{gateTypeLabels[gate.type]}</span>
                    <Badge tone="info">Pending</Badge>
                    {gate.overdue && <Badge tone="warning">Overdue</Badge>}
                  </span>
                  <span className="text-muted-foreground">
                    Created <RelativeTime iso={gate.createdAt} />
                  </span>
                  <GateArtifactRef projectId={project.id} gate={gate} />
                  <GateStepRef projectId={project.id} gate={gate} />
                  {gate.escalationReason && <span>Reason: {escalationReasonLabels[gate.escalationReason]}</span>}
                  {gate.reason && <span className="text-muted-foreground">{gate.reason}</span>}
                </div>
                {canDecide ? (
                  <div className="flex flex-wrap gap-2">
                    {gate.type === 'escalation' ? (
                      <Button
                        onClick={() => {
                          setDialog({ kind: 'escalation', gate })
                        }}
                      >
                        Decide
                      </Button>
                    ) : (
                      <>
                        <Button
                          onClick={() => {
                            setDialog({ kind: 'approve', gate })
                          }}
                        >
                          Approve
                        </Button>
                        <Button
                          variant="danger"
                          onClick={() => {
                            setDialog({ kind: 'reject', gate })
                          }}
                        >
                          Reject
                        </Button>
                      </>
                    )}
                  </div>
                ) : (
                  <p className="text-sm text-muted-foreground">
                    {readOnly ? 'This release version is read-only.' : 'Only project Owners can decide gates.'}
                  </p>
                )}
              </li>
            ))}
          </ul>
        )}
        {decided.length > 0 && (
          <ul aria-label="Decided stage gates" className="flex flex-col gap-1 text-sm">
            {decided.map((gate) => (
              <li key={gate.id} className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{gateTypeLabels[gate.type]}</span>
                <Badge tone={statusTones[gate.status]}>{gateStatusLabels[gate.status]}</Badge>
                {versionOf(gate) && (
                  <span className="font-mono text-xs text-muted-foreground" data-testid="gate-release-version">
                    {versionOf(gate)?.version}
                  </span>
                )}
                <span className="text-muted-foreground">
                  {gate.decidedBy ? `${gate.decidedBy.name}, ` : ''}
                  <RelativeTime iso={gate.decidedAt ?? gate.createdAt} />
                </span>
              </li>
            ))}
          </ul>
        )}
      </>
    )
  }

  return (
    <StageSection id="stage-gates" title="Gates">
      <p ref={headingRef} tabIndex={-1} className="sr-only">
        Gates of this stage
      </p>
      {body}
      {dialog?.kind === 'escalation' && (
        <EscalationDialog
          projectId={project.id}
          gate={dialog.gate}
          currentCheckpoint={project.checkpoint}
          onClose={() => {
            setDialog(null)
          }}
          onDecided={onDecided}
          fallbackFocusRef={headingRef}
        />
      )}
      {(dialog?.kind === 'approve' || dialog?.kind === 'reject') && dialog.gate.type !== 'escalation' && (
        <GateCommentDialog
          projectId={project.id}
          gate={{ ...dialog.gate, type: dialog.gate.type }}
          action={dialog.kind}
          onClose={() => {
            setDialog(null)
          }}
          onDecided={onDecided}
          fallbackFocusRef={headingRef}
        />
      )}
    </StageSection>
  )
}
