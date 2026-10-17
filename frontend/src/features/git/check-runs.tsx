import { ExternalLink } from 'lucide-react'

import type { Schemas } from '../../api/client'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { isFailingCheck, type CheckRunLike } from './check-run-rules'
import { checkRunConclusionLabels, checkRunStatusLabels } from '../projects/labels'

type Conclusion = NonNullable<Schemas['CheckRunConclusion']>

const conclusionTones: Record<Conclusion, BadgeTone> = {
  success: 'success',
  failure: 'danger',
  timed_out: 'danger',
  action_required: 'warning',
  cancelled: 'neutral',
  neutral: 'neutral',
  skipped: 'neutral',
}

function CheckBadge({ check }: { check: CheckRunLike }) {
  if (check.conclusion) {
    return <Badge tone={conclusionTones[check.conclusion]}>{checkRunConclusionLabels[check.conclusion]}</Badge>
  }
  if (check.status && check.status !== 'completed') {
    return <Badge tone="info">{checkRunStatusLabels[check.status]}</Badge>
  }
  return <Badge tone="warning">Pending</Badge>
}

/**
 * FR-84, FR-85: the PR head's check runs and commit statuses: state or conclusion, whether the default branch's rules
 * require it, and a link to it on GitHub (or the CI provider). Failing required checks are listed first.
 */
export function CheckRunList({ checks, label, empty }: { checks: readonly CheckRunLike[]; label: string; empty: string }) {
  if (checks.length === 0) {
    return <p className="text-sm text-muted-foreground">{empty}</p>
  }
  const rank = (c: CheckRunLike) => (c.required && isFailingCheck(c) ? 0 : isFailingCheck(c) ? 1 : c.required ? 2 : 3)
  const sorted = [...checks].sort((a, b) => rank(a) - rank(b))
  return (
    <ul aria-label={label} className="divide-y divide-border/60 text-sm">
      {sorted.map((check) => (
        <li key={check.name} aria-label={check.name} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
          <CheckBadge check={check} />
          <span className="min-w-0 break-all font-medium">{check.name}</span>
          {check.required ? (
            <Badge tone={isFailingCheck(check) ? 'danger' : 'neutral'}>Required</Badge>
          ) : (
            <span className="text-muted-foreground">Optional</span>
          )}
          {check.detailsUrl && (
            <span className="ml-auto">
              <a
                href={check.detailsUrl}
                target="_blank"
                rel="noreferrer noopener"
                aria-label={`Details of ${check.name} (opens in a new tab)`}
                className="inline-flex items-center gap-1 text-primary underline underline-offset-2"
              >
                Details
                <ExternalLink aria-hidden className="size-3.5" />
              </a>
            </span>
          )}
        </li>
      ))}
    </ul>
  )
}
