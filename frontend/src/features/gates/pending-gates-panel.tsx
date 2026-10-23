import { Link } from '../../lib/navigation'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'

import { panelClass } from '../../components/shared/panel-class'

import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { artifactTypeLabels, escalationReasonLabels, formatDateTime, gateTypeLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { EscalationDialog, GateCommentDialog } from './gate-dialogs'
import { useGates, type Gate } from './gates-api'

type OpenDialog = { kind: 'approve' | 'reject' | 'escalation'; gate: Gate } | null

/** The gated artifact (spec and plan gates), linking to the artifacts page. */
export function GateArtifactRef({ projectId, gate }: { projectId: string; gate: Gate }) {
  if (!gate.artifactType) {
    return null
  }
  const label = `${artifactTypeLabels[gate.artifactType]}${gate.artifactVersion === null ? '' : ` v${String(gate.artifactVersion)}`}`
  return (
    <span>
      Artifact:{' '}
      <Link to="/projects/$projectId/artifacts" params={{ projectId }} className="font-medium text-primary underline">
        {label}
      </Link>
    </span>
  )
}

/** FR-33: the blocked execution step an escalation is about, linking to the execution page. */
export function GateStepRef({ projectId, gate }: { projectId: string; gate: Gate }) {
  if (gate.stepNumber === null) {
    return null
  }
  return (
    <span>
      Step:{' '}
      <Link to="/projects/$projectId/execution" params={{ projectId }} className="font-medium text-primary underline">
        Step {gate.stepNumber}
      </Link>
    </span>
  )
}

function PendingGateItem({
  projectId,
  gate,
  canDecide,
  onOpen,
}: {
  projectId: string
  gate: Gate
  canDecide: boolean
  onOpen: (dialog: NonNullable<OpenDialog>) => void
}) {
  const titleId = `pending-gate-${gate.id}`
  return (
    <li
      aria-labelledby={titleId}
      className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-start sm:justify-between"
    >
      <div className="flex flex-col gap-1 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <h3 id={titleId} className="text-base font-semibold">
            {gateTypeLabels[gate.type]}
          </h3>
          {gate.overdue && <Badge tone="warning">Overdue</Badge>}
        </div>
        <span className="text-muted-foreground">
          Created <RelativeTime iso={gate.createdAt} /> ({formatDateTime(gate.createdAt)})
        </span>
        {gate.remindedAt && (
          <span className="text-muted-foreground">
            Reminded <RelativeTime iso={gate.remindedAt} /> ({formatDateTime(gate.remindedAt)})
          </span>
        )}
        <GateArtifactRef projectId={projectId} gate={gate} />
        <GateStepRef projectId={projectId} gate={gate} />
        {gate.escalationReason && <span>Reason: {escalationReasonLabels[gate.escalationReason]}</span>}
        {gate.reason && <span className="text-muted-foreground">{gate.reason}</span>}
      </div>
      {canDecide && (
        <div className="flex flex-wrap gap-2">
          {gate.type === 'escalation' ? (
            <Button
              onClick={() => {
                onOpen({ kind: 'escalation', gate })
              }}
            >
              Decide
            </Button>
          ) : (
            <>
              <Button
                onClick={() => {
                  onOpen({ kind: 'approve', gate })
                }}
              >
                Approve
              </Button>
              <Button
                variant="danger"
                onClick={() => {
                  onOpen({ kind: 'reject', gate })
                }}
              >
                Reject
              </Button>
            </>
          )}
        </div>
      )}
    </li>
  )
}

/**
 * Overview → Pending gates (FR-12–FR-15): type, age, reminder time, overdue flag and gated artifact. Owners approve or reject
 * (escalations get the decision dialog); other roles see the gates without actions.
 */
export function PendingGatesPanel({
  projectId,
  canDecide,
  currentCheckpoint,
}: {
  projectId: string
  canDecide: boolean
  currentCheckpoint: number
}) {
  const gates = useGates(projectId)
  const [dialog, setDialog] = useState<OpenDialog>(null)
  // Bumped on every decision, so focus moves even when two gates are decided in a row.
  const [decided, setDecided] = useState(0)
  const headingRef = useRef<HTMLHeadingElement>(null)

  // NFR-A11Y: the decided gate's item (and its buttons) leaves the list, so focus moves to the panel heading,
  // after the dialog's own focus restore has run.
  useEffect(() => {
    if (decided > 0) {
      headingRef.current?.focus()
    }
  }, [decided])

  function close() {
    setDialog(null)
  }

  // SPEC §7.1: async results are toasts (sonner announces them politely).
  function onDecided(message: string) {
    setDialog(null)
    setDecided((n) => n + 1)
    toast.success(message)
  }

  let body
  if (gates.isPending) {
    body = <LoadingState label="Loading gates…" />
  } else if (gates.isError) {
    body = <ErrorState error={gates.error} onRetry={() => void gates.refetch()} title="The gates couldn't load" />
  } else {
    const pending = gates.data.filter((g) => g.status === 'pending')
    body =
      pending.length === 0 ? (
        <EmptyState title="No gates are waiting for a decision" />
      ) : (
        <ul className="divide-y divide-border/60" aria-label="Pending gates">
          {pending.map((gate) => (
            <PendingGateItem
              key={gate.id}
              projectId={projectId}
              gate={gate}
              canDecide={canDecide}
              onOpen={(d) => {
                setDialog(d)
              }}
            />
          ))}
        </ul>
      )
  }

  return (
    <section id="pending-gates" aria-labelledby="pending-gates-title" className={`${panelClass} scroll-mt-24 p-4`}>
      <h2 id="pending-gates-title" ref={headingRef} tabIndex={-1} className="mb-3 text-base focus:outline-none">
        Pending gates
      </h2>
      {!canDecide && gates.data?.some((g) => g.status === 'pending') && (
        <p className="mb-2 text-sm text-muted-foreground">Only project Owners can decide gates.</p>
      )}
      {body}

      {dialog?.kind === 'escalation' && (
        <EscalationDialog
          projectId={projectId}
          gate={dialog.gate}
          currentCheckpoint={currentCheckpoint}
          onClose={close}
          onDecided={onDecided}
          fallbackFocusRef={headingRef}
        />
      )}
      {(dialog?.kind === 'approve' || dialog?.kind === 'reject') && dialog.gate.type !== 'escalation' && (
        <GateCommentDialog
          projectId={projectId}
          gate={{ ...dialog.gate, type: dialog.gate.type }}
          action={dialog.kind}
          onClose={close}
          onDecided={onDecided}
          fallbackFocusRef={headingRef}
        />
      )}
    </section>
  )
}
