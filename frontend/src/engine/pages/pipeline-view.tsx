import type { ReactNode } from 'react'

import { EmptyState as PageEmptyState } from '../../components/shared/empty-state'
import { Alert } from '../../components/ui/alert'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { usePipelineDefinition, usePipelineRun, usePipelineRuns, useRunGates, useRunStages } from '../api/hooks'
import type { PipelineRun } from '../api/types'
import { PromptComparison } from '../components/prompt-comparison'
import { FeedbackSection } from '../feedback/feedback-section'
import { canManageTriggers, noAccess, type PipelineAccess } from '../run-control/access'
import { StartRunAction, SubmitInputAction } from '../run-control/run-control-actions'
import { TriggersSection } from '../triggers/triggers-section'
import { RunArtifacts } from '../components/run-artifacts'
import { RunSummary } from '../components/run-summary'
import { RunTimeline } from '../components/run-timeline'
import { StageList } from '../components/stage-list'
import { formatDateTime } from '../format'
import { definitionLabels, runStatusLabels } from '../labels'
import { usePipelineLive } from '../live/live-bus'
import { LoadFailure, RunNotFound, Section } from './run-states'

export interface PipelineViewProps {
  projectId: string
  /** The run picked in the URL; the newest run when absent. An unknown run shows "Run not found". */
  runId: string | undefined
  onRunChange: (runId: string) => void
  /** The host's link to the replay of a run (routing belongs to the host). */
  replayLink: (runId: string) => ReactNode
  /** The host's link to the Pipeline tab showing a run, labelled `label`. */
  runLink: (runId: string, label: string) => ReactNode
  documentTitle: string
  /**
   * The viewer's effective project role and user id, from the host (FR-362, FR-365, FR-366). Start run, Submit input,
   * adding and deleting feedback and the Triggers section show only for the roles SPEC §2 allows; without it, none do.
   */
  access?: PipelineAccess
}

const runOption = (run: PipelineRun) =>
  `${run.pipelineTitle} · started ${formatDateTime(run.startedAt)} · ${runStatusLabels[run.status]}`

function RunDetail({
  projectId,
  listed,
  replayLink,
  access,
}: {
  projectId: string
  listed: PipelineRun
  replayLink: (runId: string) => ReactNode
  access: PipelineAccess
}) {
  // The run's own query: live `pipelineRunUpdated` messages land here.
  const detail = usePipelineRun(listed.id)
  const run = detail.data ?? listed
  const definition = usePipelineDefinition(run.pipelineId, run.definitionHash)
  const stages = useRunStages(run.id)
  const gates = useRunGates(run.id)
  const labels = definitionLabels(definition.data, gates.data)

  return (
    <div className="flex flex-col gap-4">
      {definition.isError && (
        <Alert tone="warning" title="The pipeline definition couldn't load">
          <p>Stage and artifact names show as keys until it loads.</p>
        </Alert>
      )}
      <Section
        id="pipeline-run"
        title="Run"
        actions={
          <div className="flex flex-wrap gap-2">
            <SubmitInputAction run={run} definition={definition.data} access={access} />
            {replayLink(run.id)}
          </div>
        }
      >
        <RunSummary run={run} definition={definition.data} />
      </Section>
      <Section id="pipeline-stages" title="Stages">
        {stages.isPending ? (
          <LoadingState label="Loading stages…" />
        ) : stages.isError ? (
          <ErrorState error={stages.error} onRetry={() => void stages.refetch()} title="The stages couldn't load" />
        ) : (
          <StageList stages={stages.data} definition={definition.data} />
        )}
      </Section>
      <div className="grid gap-4 xl:grid-cols-2">
        <Section id="pipeline-timeline" title="Timeline">
          <RunTimeline runId={run.id} labels={labels} />
        </Section>
        <Section id="pipeline-artifacts" title="Artifacts">
          <RunArtifacts runId={run.id} definition={definition.data} />
        </Section>
      </div>
      <Section id="pipeline-prompts" title="Prompt versions">
        <PromptComparison pipelineId={run.pipelineId} projectId={projectId} />
      </Section>
      {definition.data && (
        <Section id="pipeline-feedback" title="Feedback">
          <FeedbackSection projectId={projectId} definition={definition.data} access={access} />
        </Section>
      )}
    </div>
  )
}

/**
 * FR-306 Pipeline tab: a run picker (newest first), the run summary, its stage list, timeline and artifacts, and the
 * pipeline's prompt versions. Everything is rendered from the run's definition and data (FR-305), so any pipeline
 * renders here unchanged. States: loading, empty ("No runs yet"), error and not found. 0.8.0: Start run and Submit
 * input (FR-362), Feedback (FR-365) and Triggers (FR-366), each shown only to the roles `access` allows.
 */
export function PipelineView({
  projectId,
  runId,
  onRunChange,
  replayLink,
  runLink,
  documentTitle,
  access = noAccess,
}: PipelineViewProps) {
  useDocumentTitle(documentTitle)
  usePipelineLive(projectId)
  const runs = usePipelineRuns(projectId)

  let body: ReactNode
  if (runs.isPending) {
    body = <LoadingState label="Loading runs…" />
  } else if (runs.isError) {
    body = <LoadFailure error={runs.error} onRetry={() => void runs.refetch()} title="The pipeline runs couldn't load" />
  } else if (runs.data.length === 0) {
    body = (
      <PageEmptyState
        variant="not_started"
        headingLevel={3}
        title="No runs yet"
        description="A run starts when the project's pipeline does, or with Start run. Its stages, timeline and artifacts show here."
      />
    )
  } else if (runId !== undefined && !runs.data.some((r) => r.id === runId)) {
    // An unknown `?run=` (removed, or of another project) says so rather than silently showing another run.
    const latest = runs.data[0]
    body = <RunNotFound action={latest && runLink(latest.id, 'Show the latest run')} />
  } else {
    const selected = runs.data.find((r) => r.id === runId) ?? runs.data[0]
    body = selected && (
      <div className="flex flex-col gap-4">
        <SelectField
          label="Run"
          className="max-w-xl"
          value={selected.id}
          onChange={(e) => {
            onRunChange(e.target.value)
          }}
        >
          {runs.data.map((run) => (
            <option key={run.id} value={run.id}>
              {runOption(run)}
            </option>
          ))}
        </SelectField>
        <RunDetail key={selected.id} projectId={projectId} listed={selected} replayLink={replayLink} access={access} />
      </div>
    )
  }

  return (
    <section aria-labelledby="pipeline-title" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="pipeline-title" className="text-xl">
          Pipeline
        </h2>
        <StartRunAction projectId={projectId} access={access} onStarted={onRunChange} />
      </div>
      {body}
      {canManageTriggers(access) && (
        <Section id="pipeline-triggers" title="Triggers">
          <TriggersSection projectId={projectId} />
        </Section>
      )}
    </section>
  )
}
