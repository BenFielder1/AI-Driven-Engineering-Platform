import type { ReactNode } from 'react'

import { Panel } from '../../components/shared/panel'
import { LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { usePipelineDefinition, usePipelineRun, useRunGates } from '../api/hooks'
import { RunReplay } from '../components/run-replay'
import { RunStatusBadge } from '../components/run-summary'
import { formatDateTime } from '../format'
import { definitionLabels } from '../labels'
import { usePipelineLive } from '../live/live-bus'
import { LoadFailure, RunNotFound } from './run-states'

export interface ReplayViewProps {
  projectId: string
  runId: string | undefined
  /** The host's link back to the Pipeline tab (routing belongs to the host). */
  backLink: ReactNode
  documentTitle: string
}

/**
 * FR-306 Replay (`?runId=`): the run's timeline one entry at a time, with each agent run's input and output and each
 * artifact version's diff to the previous version. States: loading, error and not found (no run id, an unknown run, or
 * a run of another project).
 */
export function ReplayView({ projectId, runId, backLink, documentTitle }: ReplayViewProps) {
  useDocumentTitle(documentTitle)
  usePipelineLive(projectId)
  const run = usePipelineRun(runId)
  const definition = usePipelineDefinition(run.data?.pipelineId, run.data?.definitionHash)
  const gates = useRunGates(runId)
  const labels = definitionLabels(definition.data, gates.data)

  let body: ReactNode
  if (!runId) {
    body = <RunNotFound action={backLink} />
  } else if (run.isPending) {
    body = <LoadingState label="Loading the run…" />
  } else if (run.isError) {
    body = <LoadFailure error={run.error} onRetry={() => void run.refetch()} title="The run couldn't load" action={backLink} />
  } else if (run.data.projectId !== projectId) {
    body = <RunNotFound action={backLink} />
  } else if (definition.isPending) {
    body = <LoadingState label="Loading the pipeline definition…" />
  } else {
    body = (
      <Panel className="flex min-w-0 flex-col gap-4 p-4">
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <span className="font-medium text-foreground">{definition.data?.title ?? run.data.pipelineTitle}</span>
          <RunStatusBadge status={run.data.status} />
          <span>
            started <time dateTime={run.data.startedAt}>{formatDateTime(run.data.startedAt)}</time>
          </span>
        </div>
        <RunReplay runId={run.data.id} definition={definition.data} labels={labels} />
      </Panel>
    )
  }

  return (
    <section aria-labelledby="replay-title" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="replay-title" className="text-xl">
          Replay
        </h2>
        {runId && backLink}
      </div>
      {body}
    </section>
  )
}
