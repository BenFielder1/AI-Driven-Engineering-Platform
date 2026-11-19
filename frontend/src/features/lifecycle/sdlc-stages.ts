import { usePipelineDefinition, usePipelines } from '../../engine/api/hooks'
import type { PipelineDefinition } from '../../engine/api/types'
import { allPhases, phaseLabels } from '../projects/labels'
import type { Phase } from '../shell/nav-config'

/** The SDLC pack's pipeline id (backend src/Tevrin.Pack.Sdlc/Definitions/sdlc.pipeline.yaml). */
export const SDLC_PIPELINE_ID = 'sdlc'

/** One lifecycle stage as the stepper and stage navigation show it: its phase (and route) and its title. */
export interface SdlcStage {
  stage: Phase
  title: string
}

/**
 * The hard-coded list the stepper used before 0.7.0: Analysis, then the six phases, with the 0.6.1 labels. Used while
 * the definition loads, if it fails, or if it names no known phase, so the stepper never disappears (FR-308).
 */
export const fallbackSdlcStages: readonly SdlcStage[] = allPhases.map((stage) => ({ stage, title: phaseLabels[stage] }))

const isPhase = (id: string): id is Phase => (allPhases as readonly string[]).includes(id)

/**
 * FR-308: the lifecycle stages, in order, with their titles, from the SDLC definition. Stages whose id isn't a phase
 * (so has no page) are left out; a definition without any known phase gives the fallback.
 */
export function sdlcStagesFromDefinition(definition: PipelineDefinition | undefined): readonly SdlcStage[] {
  const stages = (definition?.stages ?? []).flatMap((s) => (isPhase(s.id) ? [{ stage: s.id, title: s.title }] : []))
  return stages.length > 0 ? stages : fallbackSdlcStages
}

/**
 * FR-308: the SDLC definition's stages. The definition is the one registered now (ListPipelines → its hash →
 * GetPipelineDefinition); the stage list is the same for every SDLC definition hash, and both queries are shared by
 * every project page. While loading or on error: `fallbackSdlcStages`.
 */
export function useSdlcStages(): readonly SdlcStage[] {
  const pipelines = usePipelines()
  const hash = pipelines.data?.find((p) => p.id === SDLC_PIPELINE_ID)?.hash
  const definition = usePipelineDefinition(SDLC_PIPELINE_ID, hash)
  return sdlcStagesFromDefinition(definition.data)
}

/** The title of `stage` in `stages`, or its 0.6.1 label. */
export function stageTitle(stages: readonly SdlcStage[], stage: Phase): string {
  return stages.find((s) => s.stage === stage)?.title ?? phaseLabels[stage]
}
