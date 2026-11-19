import type { ReactNode } from 'react'

import { Badge } from '../../components/ui/badge'
import type { PipelineDefinition, PipelineRun } from '../api/types'
import { formatCost, formatDateTime, formatNumber } from '../format'
import { definitionLabels, runStatusLabels, runStatusTones } from '../labels'

/** FR-300: a run's generic status as text (colour is decorative). */
export function RunStatusBadge({ status }: { status: PipelineRun['status'] }) {
  return <Badge tone={runStatusTones[status]}>{runStatusLabels[status]}</Badge>
}

function Fact({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted-foreground">{term}</dt>
      <dd className="mt-0.5 break-words tabular-nums">{children}</dd>
    </div>
  )
}

/**
 * FR-306 run summary: the pipeline title, generic status, current stage (its definition title), checkpoint, cost,
 * tokens and the pinned definition's short hash.
 */
export function RunSummary({ run, definition }: { run: PipelineRun; definition: PipelineDefinition | undefined }) {
  const labels = definitionLabels(definition)
  const stage = run.currentStageTitle ?? labels.stage(run.currentStage)
  return (
    <section aria-labelledby="run-summary-title" className="flex flex-col gap-3" data-testid="run-summary">
      <div className="flex flex-wrap items-center gap-2">
        <h4 id="run-summary-title" className="text-base">
          {definition?.title ?? run.pipelineTitle}
        </h4>
        <RunStatusBadge status={run.status} />
      </div>
      <dl className="grid grid-cols-2 gap-x-4 gap-y-3 text-sm sm:grid-cols-3 lg:grid-cols-6">
        <Fact term="Stage">{stage}</Fact>
        <Fact term="Checkpoint">{run.checkpoint}</Fact>
        <Fact term="Cost">{formatCost(run.cost)}</Fact>
        <Fact term="Tokens">
          {formatNumber(run.tokens)}
          <span className="block text-xs text-muted-foreground">
            {formatNumber(run.inputTokens)} in · {formatNumber(run.outputTokens)} out
          </span>
        </Fact>
        <Fact term="Started">
          <time dateTime={run.startedAt}>{formatDateTime(run.startedAt)}</time>
        </Fact>
        <Fact term="Definition">
          {definition && <span>v{definition.version} · </span>}
          <code className="font-mono text-xs" title={run.definitionHash}>
            {run.hashShort}
          </code>
        </Fact>
      </dl>
    </section>
  )
}
