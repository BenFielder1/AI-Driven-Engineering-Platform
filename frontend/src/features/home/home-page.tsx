import type { Schemas } from '../../api/client'
import { useApiReadiness } from './use-api-readiness'

const stateStyles: Record<Schemas['HealthState'], string> = {
  Healthy: 'bg-emerald-100 text-emerald-900 dark:bg-emerald-900/40 dark:text-emerald-100',
  Degraded: 'bg-amber-100 text-amber-900 dark:bg-amber-900/40 dark:text-amber-100',
  Unhealthy: 'bg-red-100 text-red-900 dark:bg-red-900/40 dark:text-red-100',
}

export function HomePage() {
  const readiness = useApiReadiness()

  return (
    <section className="mx-auto flex max-w-2xl flex-col gap-6 px-4 py-12">
      <h1 className="text-3xl font-semibold tracking-tight">AIDE Platform</h1>
      <p className="text-slate-600 dark:text-slate-300">
        AI-driven engineering, from idea to release, with approval gates and an audit trail.
      </p>

      <div
        role="status"
        aria-live="polite"
        className="rounded-lg border border-slate-200 p-4 dark:border-slate-700"
      >
        {readiness.isPending && <p>Checking the API...</p>}

        {readiness.isError && (
          <p className={`rounded px-2 py-1 ${stateStyles.Unhealthy}`}>API unreachable</p>
        )}

        {readiness.isSuccess && (
          <>
            <p>
              API status:{' '}
              <span className={`rounded px-2 py-1 font-medium ${stateStyles[readiness.data.status]}`}>
                {readiness.data.status}
              </span>
            </p>
            {readiness.data.checks.length > 0 && (
              <ul className="mt-3 list-disc pl-6 text-sm text-slate-600 dark:text-slate-300">
                {readiness.data.checks.map((check) => (
                  <li key={check.name}>
                    {check.name}: {check.status}
                    {check.description ? ` (${check.description})` : null}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </div>
    </section>
  )
}
