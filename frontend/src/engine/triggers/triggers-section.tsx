import { ErrorState, LoadingState } from '../../components/ui/states'
import { usePipelines } from '../api/hooks'
import { usePipelineDefinitions } from '../api/run-control-hooks'
import type { PipelineDefinition } from '../api/types'
import { SchedulesPanel } from './schedules-panel'
import { WebhooksPanel } from './webhooks-panel'

const declares = (d: PipelineDefinition, kind: 'schedule' | 'webhook') => d.triggers?.some((t) => t.kind === kind) === true

/**
 * FR-366 Triggers (Owner): the project's schedules and webhooks, for the engine-controlled pipelines whose definitions
 * declare those triggers (FR-305: nothing here names a pipeline).
 */
export function TriggersSection({ projectId }: { projectId: string }) {
  const pipelines = usePipelines()
  const engine = (pipelines.data ?? []).filter((p) => p.runControl === 'engine')
  const definitions = usePipelineDefinitions(engine)

  if (pipelines.isPending) {
    return <LoadingState label="Loading pipelines…" />
  }
  if (pipelines.isError) {
    return <ErrorState error={pipelines.error} onRetry={() => void pipelines.refetch()} title="The pipelines couldn't load" />
  }
  if (definitions.isPending) {
    return <LoadingState label="Loading pipelines…" />
  }
  const titles = new Map((pipelines.data).map((p) => [p.id, p.title]))
  const byId = new Map(definitions.definitions.map((d) => [d.pipelineId, d]))
  return (
    <div className="flex flex-col gap-6">
      {definitions.isError && (
        <p role="alert" className="text-sm text-destructive">
          Some pipeline definitions couldn&apos;t load, so their triggers can&apos;t be added here. Reload to try again.
        </p>
      )}
      <section aria-labelledby="triggers-schedules" className="flex flex-col gap-3">
        <h4 id="triggers-schedules" className="text-sm font-semibold">
          Schedules
        </h4>
        <SchedulesPanel
          projectId={projectId}
          pipelines={definitions.definitions.filter((d) => declares(d, 'schedule'))}
          titles={titles}
        />
      </section>
      <section aria-labelledby="triggers-webhooks" className="flex flex-col gap-3">
        <h4 id="triggers-webhooks" className="text-sm font-semibold">
          Webhooks
        </h4>
        <WebhooksPanel
          projectId={projectId}
          pipelines={definitions.definitions.filter((d) => declares(d, 'webhook'))}
          definitions={byId}
          titles={titles}
        />
      </section>
    </div>
  )
}
