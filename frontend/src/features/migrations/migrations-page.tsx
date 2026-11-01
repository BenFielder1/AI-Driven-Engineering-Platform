import { useState } from 'react'

import { EmptyState } from '../../components/shared/empty-state'
import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { withJobDetail } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { deploymentTargetLabels, safeUrl, shortSha } from '../deployments/deployment-format'
import { formatDuration } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { useReleaseVersions } from '../versions/versions-api'
import { useMigrationRuns, type AdvisorFinding, type MigrationRun, type MigrationRunStatus } from './migrations-api'

export const migrationStatusLabels: Record<MigrationRunStatus, string> = {
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
}

const migrationStatusTones: Record<MigrationRunStatus, StatusTone> = {
  running: 'info',
  succeeded: 'success',
  failed: 'destructive',
}

/** Supabase advisor levels (`ERROR`, `WARN`, `INFO`), in words. */
const advisorLevelLabel = (level: string) => {
  const l = level.toUpperCase()
  return l === 'ERROR' ? 'Error' : l === 'WARN' || l === 'WARNING' ? 'Warning' : l === 'INFO' ? 'Info' : level
}

const advisorLevelTone = (level: string): StatusTone => {
  const l = level.toUpperCase()
  return l === 'ERROR' ? 'destructive' : l === 'WARN' || l === 'WARNING' ? 'warning' : 'info'
}

function Remediation({ value }: { value: string }) {
  const url = safeUrl(value)
  if (url) {
    return (
      <a href={url} target="_blank" rel="noreferrer noopener" className="font-medium text-primary underline underline-offset-2 break-all">
        How to fix it
        <span className="sr-only"> (opens in a new tab)</span>
      </a>
    )
  }
  return <span>{value}</span>
}

function AdvisorFindings({ findings }: { findings: readonly AdvisorFinding[] }) {
  if (findings.length === 0) {
    return null
  }
  return (
    <div className="flex flex-col gap-2">
      <h4 className="text-sm font-semibold">Security advisor findings</h4>
      <ul aria-label="Security advisor findings" className="flex flex-col gap-2">
        {findings.map((f, i) => (
          <li key={`${f.name}-${String(i)}`} data-testid="advisor-finding" className="flex flex-col gap-1 rounded-lg border border-border/60 p-3 text-sm">
            <span className="flex flex-wrap items-center gap-2">
              <StatusBadge tone={advisorLevelTone(f.level)}>
                <span data-testid="advisor-level">{advisorLevelLabel(f.level)}</span>
              </StatusBadge>
              <span className="font-medium">{f.title}</span>
            </span>
            {f.detail && <span className="text-muted-foreground">{f.detail}</span>}
            {f.remediation && (
              <span data-testid="advisor-remediation">
                <span className="font-medium">Fix: </span>
                <Remediation value={f.remediation} />
              </span>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}

function RunCard({ run, version }: { run: MigrationRun; version: string | undefined }) {
  const env = deploymentTargetLabels[run.environment]
  const error = run.status === 'failed' ? (withJobDetail(run.errorCode, run.provider, run.errorMessage) ?? run.errorMessage ?? 'The migration run failed.') : null
  return (
    <article
      aria-label={`${env} migration run ${shortSha(run.gitSha)}`}
      data-testid="migration-run"
      className={cn(panelClass, 'flex min-w-0 flex-col gap-3 p-4')}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="flex flex-wrap items-baseline gap-2 text-base font-semibold">
          {env}
          <code className="font-mono text-sm font-normal">{shortSha(run.gitSha)}</code>
          {version && <span className="text-sm font-normal text-muted-foreground">version {version}</span>}
        </h3>
        <StatusBadge tone={migrationStatusTones[run.status]} pulse={run.status === 'running'}>
          <span data-testid="migration-status">{migrationStatusLabels[run.status]}</span>
        </StatusBadge>
      </div>
      <p className="text-sm text-muted-foreground">
        Started <RelativeTime iso={run.startedAt} />
        {run.finishedAt && <> · took {formatDuration(run.totalMs)}</>}
        {' · '}
        {run.files.length === 1 ? '1 file' : `${String(run.files.length)} files`}
      </p>
      {error && (
        <p role="alert" data-testid="migration-error" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm">
          {error}
        </p>
      )}
      {run.files.length > 0 && (
        <ul aria-label="Files applied" className="divide-y divide-border/60 rounded-lg border border-border/60 text-sm">
          {run.files.map((f) => (
            <li key={f.version} className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1 px-3 py-2">
              <span className="min-w-0 break-all">
                <code className="font-mono text-xs">{f.version}</code> {f.name}
              </span>
              <span className="text-muted-foreground">{formatDuration(f.ms)}</span>
            </li>
          ))}
        </ul>
      )}
      <AdvisorFindings findings={run.advisorFindings} />
    </article>
  )
}

/**
 * /projects/:id/migrations (FR-153, SPEC §7): the Supabase BaaS migration runs, newest first, with environment, commit,
 * files and durations, status, error copy and the security advisor findings; an optional release-version filter.
 */
export function MigrationsPage() {
  const { project } = useProjectAccess()
  useDocumentTitle(`Migrations · ${project.name}`)
  const [versionId, setVersionId] = useState('')
  const versions = useReleaseVersions(project.id)
  const runs = useMigrationRuns(project.id, versionId || undefined)
  const versionOf = (id: string) => versions.data?.find((v) => v.id === id)?.version

  let body
  if (runs.isPending) {
    body = <LoadingState label="Loading migration runs…" />
  } else if (runs.isError) {
    body = <ErrorState error={runs.error} title="The migration runs couldn't load" onRetry={() => void runs.refetch()} />
  } else if (runs.data.length === 0) {
    body = (
      <EmptyState
        variant="no_data"
        headingLevel={3}
        title={versionId ? 'No migration runs for this version' : 'No migration runs yet'}
        description="AIDE applies supabase/migrations to the preview database during preview rollout, and to production in the release job."
      />
    )
  } else {
    body = (
      <ol aria-label="Migration runs, newest first" className="flex flex-col gap-3">
        {runs.data.map((run) => (
          <li key={run.id}>
            <RunCard run={run} version={versionOf(run.releaseVersionId)} />
          </li>
        ))}
      </ol>
    )
  }

  return (
    <section aria-labelledby="migrations-title" className="flex min-w-0 flex-col gap-4">
      <div>
        <h2 id="migrations-title" className="text-xl">
          Migrations
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          The Supabase migration runs AIDE applied to the preview and production databases.
        </p>
      </div>
      <div className="sm:max-w-xs">
        <SelectField
          id="migrations-version"
          label="Release version"
          value={versionId}
          onChange={(e) => {
            setVersionId(e.target.value)
          }}
        >
          <option value="">All versions</option>
          {(versions.data ?? []).map((v) => (
            <option key={v.id} value={v.id}>
              {v.version}
            </option>
          ))}
        </SelectField>
      </div>
      {body}
    </section>
  )
}
