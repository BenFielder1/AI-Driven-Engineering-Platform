import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { usePromptStats } from '../api/hooks'
import type { PromptStats } from '../api/types'
import { formatAverage, formatCost, formatDuration, formatNumber, formatPromptVersion, formatRate } from '../format'

const head = 'px-3 py-2 font-medium'
const num = 'px-3 py-2 text-right tabular-nums'

/** Groups by agent key (in first-seen order), each group's prompt versions in the API's order. */
function byAgent(stats: readonly PromptStats[]): [string, PromptStats[]][] {
  const groups = new Map<string, PromptStats[]>()
  for (const s of stats) {
    const group = groups.get(s.agentKey) ?? []
    group.push(s)
    groups.set(s.agentKey, group)
  }
  return [...groups]
}

function AgentTable({ agentKey, rows }: { agentKey: string; rows: readonly PromptStats[] }) {
  return (
    <div className="overflow-x-auto rounded-lg border border-border/60" tabIndex={0} role="region" aria-label={`${agentKey} prompt versions (scrolls sideways)`}>
      <table className="w-full min-w-[48rem] border-collapse text-sm">
        <caption className="border-b border-border px-3 py-2 text-left font-mono text-sm">{agentKey}</caption>
        <thead>
          <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
            <th scope="col" className={head}>
              Prompt version
            </th>
            <th scope="col" className={`${head} text-right`}>
              Runs
            </th>
            <th scope="col" className={`${head} text-right`}>
              Avg tokens in
            </th>
            <th scope="col" className={`${head} text-right`}>
              Avg tokens out
            </th>
            <th scope="col" className={`${head} text-right`}>
              Avg cost
            </th>
            <th scope="col" className={`${head} text-right`}>
              Avg latency
            </th>
            <th scope="col" className={`${head} text-right`}>
              Avg duration
            </th>
            <th scope="col" className={`${head} text-right`}>
              Failure rate
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.promptVersion} className="border-b border-border last:border-b-0">
              <th scope="row" className="px-3 py-2 text-left font-medium">
                {formatPromptVersion(row.promptVersion)}
              </th>
              <td className={num}>{formatNumber(row.runs)}</td>
              <td className={num}>{formatAverage(row.avgInputTokens)}</td>
              <td className={num}>{formatAverage(row.avgOutputTokens)}</td>
              <td className={num}>{formatCost(row.avgCost)}</td>
              <td className={num}>{row.avgLatencyMs === null ? '—' : formatDuration(row.avgLatencyMs)}</td>
              <td className={num}>{formatDuration(row.avgDurationMs)}</td>
              <td className={num}>{formatRate(row.failureRate)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * FR-298, FR-306 "Prompt versions": the pipeline's agent runs on this project grouped by agent key and prompt version,
 * one table per agent key, so a new prompt version can be compared with the one before it. A run whose output was
 * rejected by validation counts as a failure (D-0.7.0-33).
 */
export function PromptComparison({ pipelineId, projectId }: { pipelineId: string; projectId: string }) {
  const stats = usePromptStats(pipelineId, projectId)
  if (stats.isPending) {
    return <LoadingState label="Loading prompt versions…" />
  }
  if (stats.isError) {
    return <ErrorState error={stats.error} onRetry={() => void stats.refetch()} title="The prompt versions couldn't load" />
  }
  if (stats.data.length === 0) {
    return <EmptyState title="No agent runs yet">Each agent's prompt versions are compared here once it has run.</EmptyState>
  }
  return (
    <div className="flex flex-col gap-4">
      {byAgent(stats.data).map(([agentKey, rows]) => (
        <AgentTable key={agentKey} agentKey={agentKey} rows={rows} />
      ))}
    </div>
  )
}
