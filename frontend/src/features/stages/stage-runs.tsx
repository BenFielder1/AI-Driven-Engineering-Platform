import { Link } from '../../lib/navigation'

import type { Schemas } from '../../api/client'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { agentKindLabels, formatDateTime, formatNumber } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { RunStatusBadge } from '../runs/run-badges'
import { isUnfinished } from '../runs/run-format'
import { useRuns } from '../runs/runs-api'
import { StageSection } from './stage-section'

/** Runs listed on a stage page; the Agent runs page has the rest. */
const STAGE_RUNS_SHOWN = 5

/** FR-55: the stage's agent runs (newest first) for the release version on screen, each linking to its details. */
export function StageRuns({ agent }: { agent: Schemas['AgentKind'] }) {
  const { project, releaseVersionId } = useProjectAccess()
  const runs = useRuns(project.id, agent, 1, releaseVersionId)
  const label = agentKindLabels[agent]

  let body
  if (runs.isPending) {
    body = <LoadingState label="Loading agent runs…" />
  } else if (runs.isError) {
    body = <ErrorState error={runs.error} title="The agent runs couldn't load" onRetry={() => void runs.refetch()} />
  } else if (runs.data.items.length === 0) {
    body = <p className="text-sm text-muted-foreground">No {label} runs yet.</p>
  } else {
    body = (
      <div className="overflow-x-auto">
        <table className="w-full min-w-[36rem] border-collapse text-sm">
          <caption className="sr-only">{label} runs, newest first</caption>
          <thead>
            <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <th scope="col" className="px-2 py-1 font-medium">Run</th>
              <th scope="col" className="px-2 py-1 font-medium">Model</th>
              <th scope="col" className="px-2 py-1 font-medium">Status</th>
              <th scope="col" className="px-2 py-1 text-right font-medium">Tokens</th>
              <th scope="col" className="px-2 py-1 font-medium">Started</th>
            </tr>
          </thead>
          <tbody>
            {runs.data.items.slice(0, STAGE_RUNS_SHOWN).map((run) => (
              <tr key={run.id} className="border-b border-border/40 last:border-b-0">
                <th scope="row" className="px-2 py-1 text-left font-normal">
                  <Link
                    to="/projects/$projectId/runs"
                    params={{ projectId: project.id }}
                    search={{ run: run.id }}
                    className="text-primary underline"
                    aria-label={`${label} run, started ${formatDateTime(run.createdAt)}`}
                  >
                    {run.agentKey ?? label}
                  </Link>
                </th>
                <td className="px-2 py-1">
                  <code className="text-xs">{run.model}</code>
                </td>
                <td className="px-2 py-1">
                  <RunStatusBadge status={run.status} />
                </td>
                <td className="px-2 py-1 text-right tabular-nums">
                  {isUnfinished(run) ? '—' : formatNumber(run.inputTokens + run.outputTokens)}
                </td>
                <td className="px-2 py-1">
                  <RelativeTime iso={run.createdAt} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    )
  }

  return (
    <StageSection
      id="stage-runs"
      title="Agent runs"
      actions={
        <Link to="/projects/$projectId/runs" params={{ projectId: project.id }} search={{ agent }} className="text-sm text-primary underline">
          All agent runs
        </Link>
      }
    >
      {body}
    </StageSection>
  )
}
