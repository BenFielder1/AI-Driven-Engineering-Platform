import { useState } from 'react'

import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { usePipelineDefinition, usePipelines } from '../api/hooks'
import type { PipelineAccess } from '../run-control/access'
import { FeedbackSection } from './feedback-section'

/**
 * FR-365 Feedback at Pipeline-tab level (SPEC §7), with or without runs: a pipeline picker (the registered pipelines,
 * starting on the shown run's), then that pipeline's agents and their feedback from its current definition.
 */
export function PipelineFeedback({
  projectId,
  access,
  defaultPipelineId,
}: {
  projectId: string
  access: PipelineAccess
  /** The pipeline of the run on screen, if any. */
  defaultPipelineId: string | undefined
}) {
  const pipelines = usePipelines()
  const [picked, setPicked] = useState<string | null>(null)
  const list = pipelines.data ?? []
  const pipeline =
    list.find((p) => p.id === picked) ?? list.find((p) => p.id === defaultPipelineId) ?? list[0]
  const definition = usePipelineDefinition(pipeline?.id, pipeline?.hash)

  if (pipelines.isPending) {
    return <LoadingState label="Loading pipelines…" />
  }
  if (pipelines.isError) {
    return <ErrorState error={pipelines.error} onRetry={() => void pipelines.refetch()} title="The pipelines couldn't load" />
  }
  if (!pipeline) {
    return <EmptyState title="No pipelines">Feedback is given to a pipeline’s agents once one is registered.</EmptyState>
  }
  return (
    <div className="flex flex-col gap-4">
      {list.length > 1 && (
        <SelectField
          label="Pipeline"
          className="max-w-sm"
          value={pipeline.id}
          onChange={(e) => {
            setPicked(e.target.value)
          }}
        >
          {list.map((p) => (
            <option key={p.id} value={p.id}>
              {p.title}
            </option>
          ))}
        </SelectField>
      )}
      {definition.isPending ? (
        <LoadingState label="Loading the pipeline…" />
      ) : definition.isError ? (
        <ErrorState error={definition.error} onRetry={() => void definition.refetch()} title="The pipeline couldn't load" />
      ) : (
        <FeedbackSection key={pipeline.id} projectId={projectId} definition={definition.data} access={access} />
      )}
    </div>
  )
}
