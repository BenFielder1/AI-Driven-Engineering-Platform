import { Alert } from '../../components/ui/alert'
import { PLAN_BLOCK_LANGUAGE, readPlanDocument } from '../artifacts/plan-block'
import { dependencyWaves } from './plan-waves'

/**
 * FR-55 Planning panel, FR-76: the plan's steps (number, title, specialist, declared files, dependencies, covered
 * requirement ids), a dependency view in waves, and the plan's coverage warnings when it records any.
 */
export function PlanPanel({ content }: { content: string }) {
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
                <td className="px-3 py-2">{s.agent ?? 'Code Generator'}</td>
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
