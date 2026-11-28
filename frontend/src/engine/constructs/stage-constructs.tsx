import type { PipelineDefinition, RunStage, StageDefinition } from '../api/types'
import { definitionLabels, type DefinitionLabels } from '../labels'
import { MapProgressView } from './map-progress'
import { lanesOf, StageLanes } from './stage-lanes'
import { StageTranscript } from './transcript'
import { VariantTabs } from './variant-tabs'

/** FR-363: whether a definition stage declares a construct the stage list shows (lanes, map, variants, conversation). */
export function hasConstruct(stage: StageDefinition): boolean {
  return lanesOf(stage).length > 0 || Boolean(stage.map) || (stage.variants?.labels.length ?? 0) > 0 || Boolean(stage.conversation)
}

function ConstructBody({
  stage,
  def,
  definition,
  labels,
}: {
  stage: RunStage
  def: StageDefinition
  definition: PipelineDefinition | undefined
  labels: DefinitionLabels
}) {
  const title = stage.title || labels.stage(stage.id)
  if (lanesOf(def).length > 0) {
    return <StageLanes runId={stage.runId} stage={def} labels={labels} />
  }
  if (def.map) {
    return stage.map ? (
      <MapProgressView runId={stage.runId} progress={stage.map} title={title} />
    ) : (
      <p className="text-sm text-muted-foreground">Its items open when the stage starts.</p>
    )
  }
  if (def.variants) {
    return <VariantTabs runId={stage.runId} stage={def} chosen={stage.chosenVariant} definition={definition} labels={labels} />
  }
  if (def.conversation) {
    return <StageTranscript runId={stage.runId} stage={def} labels={labels} />
  }
  return null
}

/**
 * FR-363: the constructs of the run's stages, under the stage list, each under its stage's title: lanes for parallel
 * groups and fan-outs, a progress bar with an item drill-down for maps, tabs for variants and a chat for conversations.
 * Everything comes from the definition and run data (FR-305); stages without a construct show nothing here.
 */
export function StageConstructs({ stages, definition }: { stages: readonly RunStage[]; definition: PipelineDefinition | undefined }) {
  const labels = definitionLabels(definition)
  const withConstructs = stages.flatMap((stage) => {
    const def = definition?.stages.find((s) => s.id === stage.id)
    return def && hasConstruct(def) ? [{ stage, def }] : []
  })
  if (withConstructs.length === 0) {
    return null
  }
  return (
    <div className="mt-4 flex flex-col gap-4">
      {withConstructs.map(({ stage, def }) => {
        const headingId = `stage-construct-${stage.runId}-${stage.id}`
        return (
          <section key={stage.id} aria-labelledby={headingId} className="flex min-w-0 flex-col gap-2" data-stage={stage.id}>
            <h4 id={headingId} className="text-sm font-semibold">
              {stage.title || labels.stage(stage.id)}
            </h4>
            <ConstructBody stage={stage} def={def} definition={definition} labels={labels} />
          </section>
        )
      })}
    </div>
  )
}
