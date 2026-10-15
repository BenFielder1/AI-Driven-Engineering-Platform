import { Link } from '@tanstack/react-router'
import type { ReactNode } from 'react'

import { Alert } from '../../components/ui/alert'
import { ErrorState, LoadingState } from '../../components/ui/states'
import {
  agentKindLabels,
  artifactTypeLabels,
  formatCost,
  formatDateTime,
  formatDuration,
  formatNumber,
  llmProviderLabels,
  phaseLabels,
} from '../projects/labels'
import { useReleaseVersionNames } from '../versions/version-label'
import { RunStatusBadge } from './run-badges'
import { describeRunError, formatPromptVersion, isUnfinished } from './run-format'
import { useRun, type AgentRun } from './runs-api'

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-1.5 text-sm sm:grid-cols-3">
      <dt className="font-medium text-muted-foreground">{term}</dt>
      <dd className="break-words sm:col-span-2">{children}</dd>
    </div>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mt-5">
      <h3 className="mb-2 text-base font-semibold">{title}</h3>
      {children}
    </section>
  )
}

/** The fields every run has (shown from the list row at once, then from the detail response). */
function RunFields({ projectId, run }: { projectId: string; run: AgentRun }) {
  const unfinished = isUnfinished(run)
  const versionName = useReleaseVersionNames(projectId)(run.releaseVersionId)
  return (
    <>
      <dl>
        <Row term="Status">
          <RunStatusBadge status={run.status} />
        </Row>
        <Row term="Agent">{agentKindLabels[run.agent]}</Row>
        <Row term="Phase">{phaseLabels[run.phase]}</Row>
        {versionName && <Row term="Release version">{versionName}</Row>}
        {run.stepId && (
          <Row term="Execution step">
            <code className="text-xs">{run.stepId}</code>
          </Row>
        )}
        <Row term="Provider">{llmProviderLabels[run.provider]}</Row>
        <Row term="Model">
          <code>{run.model}</code>
        </Row>
        <Row term="Prompt template">{`${agentKindLabels[run.agent]} ${formatPromptVersion(run.promptVersion)}`}</Row>
        <Row term="Attempts">{run.attempts}</Row>
        <Row term="Input tokens">{unfinished ? 'Not known yet' : formatNumber(run.inputTokens)}</Row>
        <Row term="Output tokens">{unfinished ? 'Not known yet' : formatNumber(run.outputTokens)}</Row>
        <Row term="Total tokens">{unfinished ? 'Not known yet' : formatNumber(run.inputTokens + run.outputTokens)}</Row>
        <Row term="Estimated cost">{unfinished ? 'Not known yet' : formatCost(run.costEstimate)}</Row>
        <Row term="Duration">{unfinished ? 'Running…' : formatDuration(run.durationMs)}</Row>
        <Row term="Started">
          <time dateTime={run.createdAt}>{formatDateTime(run.createdAt)}</time>
        </Row>
        <Row term="Completed">
          {run.completedAt ? <time dateTime={run.completedAt}>{formatDateTime(run.completedAt)}</time> : 'Not yet'}
        </Row>
      </dl>

      <Section title="Input artifacts">
        {run.inputArtifacts.length === 0 ? (
          <p className="text-sm text-muted-foreground">This run used no artifacts as input.</p>
        ) : (
          <ul className="flex flex-col gap-1 text-sm">
            {run.inputArtifacts.map((a) => (
              <li key={`${a.type}-${String(a.version)}`}>
                <Link
                  to="/projects/$projectId/artifacts/$type"
                  params={{ projectId, type: a.type }}
                  search={{ version: a.version }}
                  className="text-primary underline"
                >
                  {artifactTypeLabels[a.type]} v{a.version}
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Section>

      {run.userFeedback && (
        <Section title="Feedback and answers given to the agent">
          <p className="whitespace-pre-wrap break-words rounded-lg border border-border/60 bg-muted/60 p-3 text-sm">
            {run.userFeedback}
          </p>
        </Section>
      )}

      {run.error && <RunError error={run.error} attempts={run.attempts} retrying={isUnfinished(run)} />}
    </>
  )
}

/**
 * The stored error. While the run is still unfinished it is the last failed attempt and the worker is retrying, so it
 * is a neutral note rather than a failure (FR-25).
 */
function RunError({ error, attempts, retrying }: { error: string; attempts: number; retrying: boolean }) {
  const { title, message } = describeRunError(error)
  if (retrying) {
    return (
      <Section title="Last attempt">
        <Alert tone="info" title="Last attempt failed — retrying">
          {title && <p>{title}</p>}
          <p className="whitespace-pre-wrap break-words">{message}</p>
          <p className="mt-1 text-muted-foreground">
            {attempts} attempt{attempts === 1 ? '' : 's'} so far.
          </p>
        </Alert>
      </Section>
    )
  }
  return (
    <Section title="Error">
      <Alert tone="error" title={title ?? 'The run failed'}>
        <p className="whitespace-pre-wrap break-words">{message}</p>
        <p className="mt-1 text-muted-foreground">
          After {attempts} attempt{attempts === 1 ? '' : 's'}.
        </p>
      </Alert>
    </Section>
  )
}

function RunOutput({ projectId, runId, run }: { projectId: string; runId: string; run: AgentRun }) {
  const detail = useRun(projectId, runId)
  if (detail.isPending) {
    return <LoadingState label="Loading the run output…" />
  }
  if (detail.isError) {
    return <ErrorState error={detail.error} onRetry={() => void detail.refetch()} title="The run output couldn't load" />
  }
  const d = detail.data
  const status = run.status
  if (d.output === null) {
    return (
      <p className="text-sm text-muted-foreground">
        {status === 'running' || status === 'queued'
          ? 'Running… The output appears here when the run finishes (agent output isn’t streamed).'
          : 'This run produced no output.'}
      </p>
    )
  }
  return (
    <>
      {d.outputTruncated && (
        <Alert tone="info" className="mb-2">
          The output is truncated: only the first 200,000 characters are shown.
        </Alert>
      )}
      <pre
        tabIndex={0}
        aria-label="Run output"
        className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-words rounded-lg border border-border/60 bg-muted/60 p-3 text-xs"
      >
        {d.output}
      </pre>
    </>
  )
}

/**
 * FR-23: every field of one agent run, its input artifact versions (linked), error and output. `summary` is the list
 * row (shown immediately); the detail request adds the output, or stands in when the run isn't on the current page.
 */
export function RunDetails({ projectId, runId, summary }: { projectId: string; runId: string; summary: AgentRun | undefined }) {
  const detail = useRun(projectId, runId)
  const run = detail.data ?? summary
  if (!run) {
    if (detail.isError) {
      return <ErrorState error={detail.error} onRetry={() => void detail.refetch()} title="The run couldn't load" />
    }
    return <LoadingState label="Loading the run…" />
  }
  return (
    <>
      <RunFields projectId={projectId} run={run} />
      <Section title="Output">
        <RunOutput projectId={projectId} runId={runId} run={run} />
      </Section>
    </>
  )
}
