import type { ReactNode } from 'react'

import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { usePipelineDefinition, usePipelineRun } from '../api/hooks'
import type { GateInboxItem, PipelineDefinition, PipelineSummary } from '../api/types'
import { formatAge, formatDateTime } from '../format'
import { definitionLabels } from '../labels'

/** The host's routing for the inbox (FR-304: the engine has no routes of its own). */
export interface GateInboxLinks {
  /** A link to the gate's project, labelled `label` (gates the inbox can't decide are decided there). */
  project: (item: GateInboxItem, label: string) => ReactNode
  /** A link to the gated artifact labelled `label`, or null when the host has no page for it (the label shows as text). */
  artifact: (item: GateInboxItem, label: string) => ReactNode
}

/**
 * A gate the inbox can decide with approve / reject: its type is declared on a stage of the run's definition. Other
 * gates (raised by a guard, such as an escalation, which needs a decision only the project page offers) link to the
 * project, as their notifications do.
 */
export function isStageGate(item: GateInboxItem, definition: PipelineDefinition | undefined): boolean {
  return definition?.stages.some((s) => s.gate?.type === item.gate.type) ?? false
}

/** The gate's title (from the run, which takes it from the definition), with the definition's fallback. */
export function gateTitle(item: GateInboxItem, definition: PipelineDefinition | undefined): string {
  return definitionLabels(definition, [item.gate]).gate(item.gate.id, item.gate.type)
}

/**
 * The definition the gate's run is pinned to (its run's hash); the pipeline's current definition when the run can't
 * load. `pending` until known, so the row doesn't offer actions it may have to take back.
 */
function useRowDefinition(item: GateInboxItem, pipelines: readonly PipelineSummary[] | undefined) {
  const run = usePipelineRun(item.gate.runId)
  const current = pipelines?.find((p) => p.id === item.pipelineId)?.hash
  const hash = run.data?.definitionHash ?? (run.isError ? current : undefined)
  const definition = usePipelineDefinition(item.pipelineId, hash)
  return { definition: definition.data, pending: !definition.data && !definition.isError && !(run.isError && !current) }
}

export function GateInboxRow({
  item,
  pipelines,
  links,
  cannotDecideNote,
  onDecide,
}: {
  item: GateInboxItem
  pipelines: readonly PipelineSummary[] | undefined
  links: GateInboxLinks
  /** Shown instead of the actions on a stage gate the user may not decide (`canDecide` false). */
  cannotDecideNote: string
  onDecide: (action: 'approve' | 'reject', definition: PipelineDefinition | undefined) => void
}) {
  const { gate } = item
  const { definition, pending } = useRowDefinition(item, pipelines)
  const title = gateTitle(item, definition)
  const titleId = `gate-inbox-${gate.id}`
  const artifactLabel = gate.artifactKey
    ? `${definitionLabels(definition).artifact(gate.artifactKey)}${gate.artifactVersion === null ? '' : ` v${String(gate.artifactVersion)}`}`
    : null
  const artifactLink = artifactLabel ? links.artifact(item, artifactLabel) : null

  let actions: ReactNode
  if (pending) {
    actions = <span aria-hidden className="block h-9 w-44 animate-pulse rounded-md bg-muted" data-testid="gate-inbox-actions-loading" />
  } else if (!isStageGate(item, definition)) {
    actions = links.project(item, 'Open project')
  } else if (!item.canDecide) {
    actions = <p className="max-w-56 text-sm text-muted-foreground">{cannotDecideNote}</p>
  } else {
    actions = (
      <>
        <Button
          aria-describedby={titleId}
          onClick={() => {
            onDecide('approve', definition)
          }}
        >
          Approve
        </Button>
        <Button
          variant="danger"
          aria-describedby={titleId}
          onClick={() => {
            onDecide('reject', definition)
          }}
        >
          Reject
        </Button>
      </>
    )
  }

  return (
    <li
      aria-labelledby={titleId}
      data-testid="gate-inbox-row"
      className="flex flex-col gap-3 py-4 first:pt-0 last:pb-0 sm:flex-row sm:items-start sm:justify-between"
    >
      <div className="flex min-w-0 flex-col gap-1 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <h2 id={titleId} className="text-base font-semibold">
            {title}
          </h2>
          {gate.overdue && <Badge tone="warning">Overdue</Badge>}
        </div>
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
          <dt className="text-muted-foreground">Project</dt>
          <dd className="min-w-0 break-words">{item.projectName}</dd>
          <dt className="text-muted-foreground">Pipeline</dt>
          <dd className="min-w-0 break-words">{item.pipelineTitle}</dd>
          {artifactLabel && (
            <>
              <dt className="text-muted-foreground">Artifact</dt>
              <dd className="min-w-0 break-words">{artifactLink ?? artifactLabel}</dd>
            </>
          )}
          <dt className="text-muted-foreground">Waiting</dt>
          <dd>
            <time dateTime={gate.createdAt} title={formatDateTime(gate.createdAt)}>
              {formatAge(gate.createdAt)}
            </time>
          </dd>
        </dl>
      </div>
      {/* The action area keeps its size while the definition loads, so the row doesn't jump. */}
      <div className="flex min-h-9 shrink-0 flex-wrap items-start gap-2 sm:min-w-44 sm:justify-end">{actions}</div>
    </li>
  )
}
