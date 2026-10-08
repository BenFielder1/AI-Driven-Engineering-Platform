import { Link } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'

import { Alert } from '../../components/ui/alert'
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
    <li aria-labelledby={titleId} className="flex flex-col gap-2 py-3 sm:flex-row sm:items-start sm:justify-between">
      <div className="flex flex-col gap-1 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <h3 id={titleId} className="text-base font-semibold">
            {gateTypeLabels[gate.type]}
          </h3>
          {gate.overdue && <Badge tone="warning">Overdue</Badge>}
        </div>
        <span className="text-muted">
          Created <RelativeTime iso={gate.createdAt} /> ({formatDateTime(gate.createdAt)})
        </span>
        <GateArtifactRef projectId={projectId} gate={gate} />
        {gate.escalationReason && <span>Reason: {escalationReasonLabels[gate.escalationReason]}</span>}
        {gate.reason && <span className="text-muted">{gate.reason}</span>}
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
 * Overview → Pending gates (FR-12–FR-15): type, age, overdue flag and gated artifact. Owners approve or reject
 * (escalations get the decision dialog); other roles see the gates without actions.
 */
export function PendingGatesPanel({
  projectId,
  canDecide,
  currentVersion,
}: {
  projectId: string
  canDecide: boolean
  currentVersion: number
}) {
  const gates = useGates(projectId)
  const [dialog, setDialog] = useState<OpenDialog>(null)
  const [decided, setDecided] = useState<string | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  // NFR-A11Y: the decided gate's item (and its buttons) leaves the list, so focus moves to the panel heading,
  // after the dialog's own focus restore has run.
  useEffect(() => {
    if (decided) {
      headingRef.current?.focus()
    }
  }, [decided])

  function close() {
    setDialog(null)
  }

  function onDecided(message: string) {
    setDialog(null)
    setDecided(message)
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
        <ul className="divide-y divide-border" aria-label="Pending gates">
          {pending.map((gate) => (
            <PendingGateItem
              key={gate.id}
              projectId={projectId}
              gate={gate}
              canDecide={canDecide}
              onOpen={(d) => {
                setDecided(null)
                setDialog(d)
              }}
            />
          ))}
        </ul>
      )
  }

  return (
    <section aria-labelledby="pending-gates-title" className="rounded-lg border border-border bg-surface-raised p-4">
      <h2 id="pending-gates-title" ref={headingRef} tabIndex={-1} className="mb-3 text-lg font-semibold focus:outline-none">
        Pending gates
      </h2>
      {!canDecide && gates.data?.some((g) => g.status === 'pending') && (
        <p className="mb-2 text-sm text-muted">Only project Owners can decide gates.</p>
      )}
      <div role="status" aria-live="polite">
        {decided && (
          <Alert tone="success" className="mb-3">
            {decided}
          </Alert>
        )}
      </div>
      {body}

      {dialog?.kind === 'escalation' && (
        <EscalationDialog
          projectId={projectId}
          gate={dialog.gate}
          currentVersion={currentVersion}
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
