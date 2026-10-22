import { useDocumentTitle } from '../../lib/use-document-title'
import { phaseLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState } from '../projects/projects-api'
import { usePreset } from '../presets/presets-api'
import { useReleaseVersions } from '../versions/versions-api'
import type { Schemas } from '../../api/client'
import { PlanPanel } from './plan-panel'
import { SpecPanel } from './spec-panel'
import { StageArtifact } from './stage-artifact'
import { StageGates } from './stage-gates'
import { StageHeader } from './stage-header'
import { stageGates } from './stage-model'
import { StageRuns } from './stage-runs'
import { useProducingRunActive } from './use-producing-run-active'

interface ArtifactStageProps {
  /** `?version=N`: the artifact version on screen (the newest when absent). */
  version: number | undefined
  onVersionChange: (version: number | undefined) => void
}

/** A stage awaits approval while one of its gates is pending or its artifact's latest version is a draft. */
function useAwaitingApproval(stage: 'specification' | 'planning', type: 'technical_specification' | 'implementation_plan') {
  const { project } = useProjectAccess()
  const state = useProjectState(project.id)
  const s = state.data
  if (s?.phase !== stage) {
    return false
  }
  return (
    s.pendingGates.some((g) => stageGates[stage].includes(g.type)) ||
    s.artifacts.some((a) => a.type === type && a.latestStatus === 'draft')
  )
}

/**
 * /projects/:id/specification (FR-55): the technical specification in full with the section outline and, for
 * iterations, the "Changes in this version" filter; the spec approval gate and the Spec Generator's runs.
 */
export function SpecificationPage({ version, onVersionChange }: ArtifactStageProps) {
  const { project } = useProjectAccess()
  useDocumentTitle(`${phaseLabels.specification} · ${project.name}`)
  const awaiting = useAwaitingApproval('specification', 'technical_specification')
  const drafting = useProducingRunActive('spec_generator')
  return (
    <section aria-labelledby="specification-title" className="flex flex-col gap-5">
      <StageHeader stage="specification" awaitingApproval={awaiting} />
      <StageArtifact
        stage="specification"
        type="technical_specification"
        version={version}
        onVersionChange={onVersionChange}
        awaitingDraft={drafting}
        notStarted="The Spec Generator writes the technical specification from the approved project request. Refinement has to finish first: approve the project request and advance to Specification."
      >
        {(artifact) => <SpecPanel content={artifact.content} />}
      </StageArtifact>
      <StageGates types={stageGates.specification} />
      <StageRuns agent="spec_generator" />
    </section>
  )
}

/** FR-76: the plan with its specialist agents named from the preset pinned on the plan's release version. */
function PlanWithAgents({ artifact, projectId }: { artifact: Schemas['ArtifactDto']; projectId: string }) {
  const versions = useReleaseVersions(projectId)
  const presetId = versions.data?.find((v) => v.id === artifact.releaseVersionId)?.presetId ?? null
  const preset = usePreset(presetId)
  return <PlanPanel content={artifact.content} warnings={artifact.warnings} agents={preset.data?.agents} />
}

/**
 * /projects/:id/planning (FR-55, FR-76): the implementation plan in full with its step table (specialist, files,
 * dependencies, covered requirements), the dependency waves and coverage warnings; the plan gate and the Plan
 * Generator's runs.
 */
export function PlanningPage({ version, onVersionChange }: ArtifactStageProps) {
  const { project } = useProjectAccess()
  useDocumentTitle(`${phaseLabels.planning} · ${project.name}`)
  const awaiting = useAwaitingApproval('planning', 'implementation_plan')
  const drafting = useProducingRunActive('plan_generator')
  return (
    <section aria-labelledby="planning-title" className="flex flex-col gap-5">
      <StageHeader stage="planning" awaitingApproval={awaiting} />
      <StageArtifact
        stage="planning"
        type="implementation_plan"
        version={version}
        onVersionChange={onVersionChange}
        awaitingDraft={drafting}
        notStarted="The Plan Generator writes the implementation plan from the approved specification. The specification has to be approved and the project advanced to Planning first."
      >
        {(artifact) => <PlanWithAgents artifact={artifact} projectId={project.id} />}
      </StageArtifact>
      <StageGates types={stageGates.planning} />
      <StageRuns agent="plan_generator" />
    </section>
  )
}
