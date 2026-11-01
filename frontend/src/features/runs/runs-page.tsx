import { useRef } from 'react'

import { Button } from '../../components/ui/button'
import { DetailSheet } from '../../components/ui/modal-dialog'
import { Pagination } from '../../components/ui/pagination'
import { resultSummary } from '../../components/ui/result-summary'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import {
  agentKindLabels,
  agentKinds,
  formatCost,
  formatDateTime,
  formatDuration,
  formatNumber,
  llmProviderLabels,
} from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { oneOf } from '../projects/projects-search'
import { RelativeTime } from '../projects/relative-time'
import { specialistText } from '../stack/legacy-preset'
import { RunStatusBadge } from './run-badges'
import { RunDetails } from './run-details'
import { isUnfinished } from './run-format'
import { checkpointLabel, useReleaseVersionNames } from '../versions/version-label'
import { useRun, useRuns, type AgentRun } from './runs-api'
import type { RunsSearch } from './runs-search'

interface RunsPageProps {
  search: RunsSearch
  /** Filter and page changes (replace the history entry). */
  onSearchChange: (next: RunsSearch) => void
  /** Opens the detail drawer for a run (pushes a history entry, so Back closes it). */
  onOpenRun: (runId: string) => void
  /** Closes the detail drawer (Close button or Escape). */
  onCloseRun: () => void
}

/** SPEC §7.2: "1.2.0 · checkpoint 4" (every list shows its release version). */
function runVersion(run: AgentRun, versionName: string | undefined): string {
  if (run.checkpoint !== null) {
    return checkpointLabel(versionName, run.checkpoint)
  }
  return versionName ?? '—'
}

function RunRow({ run, onOpen, versionName }: { run: AgentRun; onOpen: () => void; versionName: string | undefined }) {
  const unfinished = isUnfinished(run)
  return (
    <tr className="border-b border-border last:border-b-0">
      <th scope="row" className="px-3 py-2 text-left font-medium">
        <button
          type="button"
          onClick={onOpen}
          aria-label={`${agentKindLabels[run.agent]} run, started ${formatDateTime(run.createdAt)}`}
          className="min-h-8 text-left text-primary underline underline-offset-4"
        >
          {agentKindLabels[run.agent]}
        </button>
      </th>
      <td className="px-3 py-2">{specialistText(run.agentKey, run.legacyAgentKey) ?? '—'}</td>
      <td className="px-3 py-2">
        <code className="text-xs">{run.model}</code>
      </td>
      <td className="px-3 py-2">{llmProviderLabels[run.provider]}</td>
      <td className="px-3 py-2">{run.credentialLabel ?? '—'}</td>
      <td className="px-3 py-2 whitespace-nowrap" data-testid="run-version">
        {runVersion(run, versionName)}
      </td>
      <td className="px-3 py-2 text-right tabular-nums">
        {unfinished ? '—' : `${formatNumber(run.inputTokens)} / ${formatNumber(run.outputTokens)}`}
      </td>
      <td className="px-3 py-2 text-right tabular-nums">{formatNumber(run.toolCallCount)}</td>
      <td className="px-3 py-2 text-right tabular-nums">{unfinished ? '—' : formatCost(run.costEstimate)}</td>
      <td className="px-3 py-2 text-right tabular-nums">{unfinished ? 'Running…' : formatDuration(run.durationMs)}</td>
      <td className="px-3 py-2">
        <RunStatusBadge status={run.status} />
      </td>
      <td className="py-2">
        <RelativeTime iso={run.createdAt} />
      </td>
    </tr>
  )
}

function RunsTable({ projectId, runs, onOpen }: { projectId: string; runs: AgentRun[]; onOpen: (run: AgentRun) => void }) {
  const head = 'px-3 py-2 font-medium'
  const versionName = useReleaseVersionNames(projectId)
  return (
    <div className="overflow-x-auto rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md">
      <table className="w-full min-w-[70rem] border-collapse text-sm">
        <caption className="sr-only">Agent runs, newest first. Open a run to see all its details.</caption>
        <thead>
          <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
            <th scope="col" className={head}>
              Agent
            </th>
            <th scope="col" className={head}>
              Specialist
            </th>
            <th scope="col" className={head}>
              Model
            </th>
            <th scope="col" className={head}>
              Provider
            </th>
            <th scope="col" className={head}>
              Credential
            </th>
            <th scope="col" className={head}>
              Version
            </th>
            <th scope="col" className={`${head} text-right`}>
              Tokens in / out
            </th>
            <th scope="col" className={`${head} text-right`}>
              Tool calls
            </th>
            <th scope="col" className={`${head} text-right`}>
              Est. cost
            </th>
            <th scope="col" className={`${head} text-right`}>
              Duration
            </th>
            <th scope="col" className={head}>
              Status
            </th>
            <th scope="col" className="px-3 py-2 font-medium">
              Started
            </th>
          </tr>
        </thead>
        <tbody>
          {runs.map((run) => (
            <RunRow
              key={run.id}
              run={run}
              versionName={versionName(run.releaseVersionId)}
              onOpen={() => {
                onOpen(run)
              }}
            />
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * /projects/:id/runs (FR-23, SPEC §7): the project's agent runs, newest first, filterable by agent and paged; a row
 * opens a detail drawer with every field, the input artifact versions and the output. Running runs show "Running…"
 * (nothing streams in v1); live `run` events refresh the list (FR-46).
 */
export function RunsPage({ search, onSearchChange, onOpenRun, onCloseRun }: RunsPageProps) {
  const { project, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Runs · ${project.name}`)
  const page = search.page ?? 1
  const agent = search.agent ?? null
  const runs = useRuns(project.id, agent, page, releaseVersionId)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const openRunId = search.run ?? null
  const openSummary = runs.data?.items.find((r) => r.id === openRunId)
  // Same query as the drawer content (deduplicated): names the drawer when the run isn't on the current page.
  const openDetail = useRun(project.id, openRunId)
  const openRun = openDetail.data ?? openSummary

  let body
  if (runs.isPending) {
    body = <LoadingState label="Loading runs…" />
  } else if (runs.isError) {
    body = <ErrorState error={runs.error} onRetry={() => void runs.refetch()} title="The runs couldn't load" />
  } else if (runs.data.total === 0 && agent) {
    body = (
      <EmptyState title={`No ${agentKindLabels[agent].toLowerCase()} runs yet`}>
        <Button
          variant="secondary"
          className="mt-3"
          onClick={() => {
            onSearchChange({})
          }}
        >
          Show all agents
        </Button>
      </EmptyState>
    )
  } else if (runs.data.total === 0) {
    body = (
      <EmptyState title="No agent runs yet">
        Runs appear here when an agent works on this project, starting with refinement.
      </EmptyState>
    )
  } else {
    const d = runs.data
    body = (
      <>
        <p role="status" aria-live="polite" className="mb-3 text-sm text-muted-foreground">
          {resultSummary(d.page, d.limit, d.items.length, d.total, 'run')}
        </p>
        <div aria-busy={runs.isPlaceholderData || undefined}>
          <RunsTable
            projectId={project.id}
            runs={d.items}
            onOpen={(run) => {
              onOpenRun(run.id)
            }}
          />
        </div>
        <Pagination
          page={d.page}
          pageCount={Math.max(1, Math.ceil(d.total / d.limit))}
          onPage={(next) => {
            onSearchChange({ ...search, page: next > 1 ? next : undefined, run: undefined })
          }}
        />
      </>
    )
  }

  return (
    <section aria-labelledby="runs-title">
      <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
        <h2 id="runs-title" ref={headingRef} tabIndex={-1} className="text-xl">
          Agent runs
        </h2>
        <SelectField
          id="runs-agent"
          label="Agent"
          value={agent ?? ''}
          className="w-full sm:w-60"
          onChange={(e) => {
            onSearchChange({ agent: oneOf(agentKinds, e.target.value) })
          }}
        >
          <option value="">All agents</option>
          {agentKinds.map((k) => (
            <option key={k} value={k}>
              {agentKindLabels[k]}
            </option>
          ))}
        </SelectField>
      </div>
      {body}
      <DetailSheet
        open={openRunId !== null}
        title={openRun ? `${agentKindLabels[openRun.agent]} run` : 'Agent run'}
        fallbackFocusRef={headingRef}
        onClose={onCloseRun}
      >
        {openRunId && <RunDetails projectId={project.id} runId={openRunId} summary={openSummary} />}
      </DetailSheet>
    </section>
  )
}
