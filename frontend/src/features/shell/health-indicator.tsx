import type { Schemas } from '../../api/client'
import { useApiReadiness } from './use-api-readiness'

const dotStyles: Record<Schemas['HealthState'] | 'Unreachable', string> = {
  Healthy: 'bg-success',
  Degraded: 'bg-warning',
  Unhealthy: 'bg-destructive',
  Unreachable: 'bg-destructive',
}

/** Unobtrusive API readiness indicator for the footer (NFR-OBS). */
export function HealthIndicator() {
  const readiness = useApiReadiness()
  const state = readiness.isError ? 'Unreachable' : readiness.data?.status
  const label = readiness.isPending ? 'Checking…' : state === 'Unreachable' ? 'API unreachable' : (state ?? '')

  return (
    <p role="status" aria-live="polite" className="flex items-center gap-2 text-xs text-muted-foreground">
      {state && <span aria-hidden="true" className={`inline-block h-2 w-2 rounded-full ${dotStyles[state]}`} />}
      <span>
        API status: <span className="font-medium text-foreground">{label}</span>
      </span>
    </p>
  )
}
