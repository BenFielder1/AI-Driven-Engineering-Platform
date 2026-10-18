import type { Schemas } from '../../api/client'
import { Alert } from '../../components/ui/alert'
import { copyForCode } from '../../lib/errors'
import { PLAN_BLOCK_LANGUAGE, readPlanDocument } from '../artifacts/plan-block'
import { agentLabel, type PresetAgent } from '../presets/presets-api'
import { dependencyWaves } from './plan-waves'

/**
 * FR-55 Planning panel, FR-76: the plan's steps (number, title, specialist, declared files, dependencies, covered
 * requirement ids), a dependency view in waves, and the plan's coverage warnings when it records any.
 */
/** FR-76, §4.11: a title and an ids-list label per plan warning code. */
const warningTitles: Readonly<Record<string, { title: string; idsLabel: string }>> = {
  UNCOVERED_REQUIREMENTS: { title: 'Uncovered requirements', idsLabel: 'Requirements not covered by any step' },
  AGENT_SCOPE: { title: "Files outside the agent's owned paths", idsLabel: 'Steps and files outside their agent’s paths' },
  PLAN_ORDER: { title: 'Plan order', idsLabel: 'Steps out of the recommended order' },
  MISSING_VERCEL_CONFIG: { title: 'No vercel.json', idsLabel: 'Affected items' },
}

/**
 * FR-76, §4.11, §4.13: the API's non-blocking findings about this plan version (UNCOVERED_REQUIREMENTS, and with a
 * preset AGENT_SCOPE and PLAN_ORDER, and for a Vercel preset MISSING_VERCEL_CONFIG, FR-89), each with its copy, the server's specifics and its ids.
 */
function PlanWarnings({ warnings }: { warnings: readonly Schemas['ArtifactWarningDto'][] }) {
  if (warnings.length === 0) {
    return null
  }
  return (
    <div className="flex flex-col gap-2">
      {warnings.map((w) => (
        <Alert
          key={`${w.code}:${w.ids.join(',')}`}
          tone="warning"
          title={warningTitles[w.code]?.title ?? 'Plan warning'}
        >
          <p>{copyForCode(w.code) ?? w.message}</p>
          {copyForCode(w.code) && w.code !== 'UNCOVERED_REQUIREMENTS' && w.message && (
            <p className="mt-1 text-muted-foreground">{w.message}</p>
          )}
          {w.ids.length > 0 && (
            <ul aria-label={warningTitles[w.code]?.idsLabel ?? 'Affected items'} className="mt-1 flex flex-wrap gap-1">
              {w.ids.map((id) => (
                <li key={id}>
                  <code className="rounded border border-border px-1 text-xs">{id}</code>
                </li>
              ))}
            </ul>
          )}
        </Alert>
      ))}
    </div>
  )
}

/** FR-76: the step's specialist: the catalogue agent's display name with its key, or v1's Code Generator. */
function Specialist({ agent, agents }: { agent: string | null; agents: readonly PresetAgent[] | undefined }) {
  if (!agent) {
    return <>Code Generator</>
  }
  const label = agentLabel(agent, agents)
  return label.name ? (
    <span className="flex flex-col">
      <span>{label.name}</span>
      <code className="text-xs text-muted-foreground">{label.key}</code>
    </span>
  ) : (
    <code className="text-xs">{label.key}</code>
  )
}

export function PlanPanel({
  content,
  warnings = [],
  agents,
}: {
  content: string
  warnings?: readonly Schemas['ArtifactWarningDto'][]
  /** FR-76: the plan's preset agent catalogue, for display names (undefined without a preset or while loading). */
  agents?: readonly PresetAgent[]
}) {
  const doc = readPlanDocument(content)
  if (!doc) {
    return (
      <p className="text-sm text-muted-foreground">
        The ```{PLAN_BLOCK_LANGUAGE} block of this version can&apos;t be read, so there is no step table.
      </p>
    )
  }
  const { steps, coverageWarnings } = doc
  const waves = dependencyWaves(steps)
  const th = 'px-3 py-2 font-medium'
  return (
    <div className="flex flex-col gap-3">
      <PlanWarnings warnings={warnings} />
      {coverageWarnings.length > 0 && (
        <Alert tone="warning" title="Coverage warnings">
          <ul className="list-disc pl-5">
            {coverageWarnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
        </Alert>
      )}
      <div className="overflow-x-auto rounded-lg border border-border/60">
        <table className="w-full min-w-[40rem] border-collapse text-sm">
          <caption className="sr-only">Plan steps with their specialist, files, dependencies and covered requirements</caption>
          <thead>
            <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <th scope="col" className={th}>Step</th>
              <th scope="col" className={th}>Title</th>
              <th scope="col" className={th}>Specialist</th>
              <th scope="col" className={th}>Declared files</th>
              <th scope="col" className={th}>Depends on</th>
              <th scope="col" className={th}>Covers</th>
            </tr>
          </thead>
          <tbody>
            {steps.map((s) => (
              <tr key={s.number} className="border-b border-border/40 align-top last:border-b-0">
                <th scope="row" className="px-3 py-2 text-left font-medium">
                  {s.number}
                </th>
                <td className="px-3 py-2 break-words">{s.title}</td>
                <td className="px-3 py-2">
                  <Specialist agent={s.agent} agents={agents} />
                </td>
                <td className="px-3 py-2">
                  <ul className="flex flex-col gap-0.5">
                    {s.files.map((f) => (
                      <li key={`${f.action}:${f.path}`} className="break-all">
                        <code className="text-xs">{f.path}</code> <span className="text-xs text-muted-foreground">({f.action})</span>
                      </li>
                    ))}
                  </ul>
                </td>
                <td className="px-3 py-2">{s.dependencies.length === 0 ? 'None' : s.dependencies.join(', ')}</td>
                <td className="px-3 py-2">{s.covers.length === 0 ? '—' : s.covers.join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <section aria-labelledby="plan-waves-title" className="flex flex-col gap-1">
        <h4 id="plan-waves-title" className="text-sm font-semibold">
          Dependencies
        </h4>
        <ol aria-label="Dependency waves" className="flex flex-col gap-1 text-sm">
          {waves.map((w, i) => (
            <li key={w.join('-')}>
              <span className="font-medium">Wave {i + 1}:</span> {w.length === 1 ? 'step' : 'steps'} {w.join(', ')}
              {i > 0 && <span className="text-muted-foreground"> (after wave {i})</span>}
            </li>
          ))}
        </ol>
      </section>
    </div>
  )
}
