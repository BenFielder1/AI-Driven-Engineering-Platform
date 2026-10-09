import { Badge, type BadgeTone } from '../../components/ui/badge'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { escalationDecisionLabels, escalationReasonLabels, gateStatusLabels, gateTypeLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { GateArtifactRef, GateStepRef } from './pending-gates-panel'
import { useGates, type Gate } from './gates-api'

const statusTones: Record<Gate['status'], BadgeTone> = {
  pending: 'info',
  approved: 'success',
  rejected: 'danger',
  cancelled: 'neutral',
}

function decisionText(gate: Gate): string | null {
  if (!gate.decision) {
    return null
  }
  const label = escalationDecisionLabels[gate.decision]
  return gate.decision === 'rollback' && gate.targetVersion !== null
    ? `${label} to version ${String(gate.targetVersion)}`
    : label
}

/** Overview → Gate history (FR-13, FR-14): decided and cancelled gates, newest first, with decision and comment. */
export function GateHistory({ projectId }: { projectId: string }) {
  const gates = useGates(projectId)

  if (gates.isPending) {
    return <LoadingState label="Loading gate history…" />
  }
  if (gates.isError) {
    return <ErrorState error={gates.error} onRetry={() => void gates.refetch()} title="The gate history couldn't load" />
  }
  const decided = gates.data.filter((g) => g.status !== 'pending')
  if (decided.length === 0) {
    return <EmptyState title="No gates have been decided yet" />
  }
  return (
    <ul aria-label="Gate history" className="divide-y divide-border">
      {decided.map((gate) => {
        const decision = decisionText(gate)
        return (
          <li key={gate.id} className="flex flex-col gap-1 py-3 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-semibold">{gateTypeLabels[gate.type]}</span>
              <Badge tone={statusTones[gate.status]}>{gateStatusLabels[gate.status]}</Badge>
              {decision && <span>Decision: {decision}</span>}
            </div>
            <span className="text-muted">
              {gate.decidedBy ? `${gate.decidedBy.name}, ` : ''}
              {gate.decidedAt ? <RelativeTime iso={gate.decidedAt} /> : <>created <RelativeTime iso={gate.createdAt} /></>}
            </span>
            <GateArtifactRef projectId={projectId} gate={gate} />
            <GateStepRef projectId={projectId} gate={gate} />
            {gate.escalationReason && <span>Reason: {escalationReasonLabels[gate.escalationReason]}</span>}
            {gate.comment && (
              <blockquote className="border-l-2 border-border pl-3 break-words whitespace-pre-wrap">
                {gate.comment}
              </blockquote>
            )}
          </li>
        )
      })}
    </ul>
  )
}
