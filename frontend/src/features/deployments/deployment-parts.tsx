import { ExternalLink } from 'lucide-react'

import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { cn } from '../../lib/utils'
import { RelativeTime } from '../projects/relative-time'
import {
  deploymentFailure,
  deploymentStatusLabels,
  deploymentStatusTones,
  deploymentTargetLabels,
  safeUrl,
  shortSha,
  triggeredByLabel,
} from './deployment-format'
import { isActiveDeployment, type Deployment, type DeploymentStatus } from './deployments-api'

/**
 * SPEC §7.2 UI rule: long-running external work shows a live status pill. Queued and building pulse and are announced
 * politely when they change; final states are plain pills.
 */
export function DeploymentStatusPill({ status, className }: { status: DeploymentStatus; className?: string }) {
  const active = isActiveDeployment(status)
  return (
    <StatusBadge tone={deploymentStatusTones[status]} pulse={active} className={className}>
      <span data-testid="deployment-status" role={active ? 'status' : undefined}>
        {deploymentStatusLabels[status]}
        {active && <span className="sr-only"> (in progress)</span>}
      </span>
    </StatusBadge>
  )
}

/** The deployment URL as an external link (https only), or "No URL yet". */
export function DeploymentLink({ url, label }: { url: string | null; label?: string }) {
  const href = safeUrl(url)
  if (!href) {
    return <span className="text-muted-foreground">No URL yet</span>
  }
  return (
    <a
      href={href}
      target="_blank"
      rel="noreferrer noopener"
      className="inline-flex min-w-0 items-center gap-1 break-all text-primary underline underline-offset-2"
    >
      {label ?? href.replace(/^https:\/\//, '')}
      <ExternalLink aria-hidden className="size-3.5" />
      <span className="sr-only">(opens in a new tab)</span>
    </a>
  )
}

/** FR-90: the end of the build log, collapsed by default, in monospace. */
export function BuildLogTail({ log, defaultOpen = false }: { log: string | null; defaultOpen?: boolean }) {
  if (!log?.trim()) {
    return null
  }
  return (
    <details open={defaultOpen} className="rounded-lg border border-border/60 bg-background/40">
      <summary className="min-h-10 cursor-pointer px-3 py-2 text-sm font-medium">Build log (last lines)</summary>
      <pre
        data-testid="build-log-tail"
        tabIndex={0}
        aria-label="Build log tail"
        className="max-h-80 overflow-auto border-t border-border/60 px-3 py-2 font-mono text-xs whitespace-pre-wrap break-words"
      >
        {log}
      </pre>
    </details>
  )
}

/** The facts of one deployment as a definition list: target, version, commit, trigger, times, and why it failed. */
export function DeploymentFacts({ deployment, className }: { deployment: Deployment; className?: string }) {
  const d = deployment
  const failure = d.status === 'error' ? deploymentFailure(d) : null
  return (
    <div className={cn('flex flex-col gap-3', className)}>
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">URL</dt>
          <dd className="min-w-0">
            <DeploymentLink url={d.url} />
          </dd>
        </div>
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Target</dt>
          <dd>{deploymentTargetLabels[d.target]}</dd>
        </div>
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Version</dt>
          <dd className="font-mono">{d.releaseVersion}</dd>
        </div>
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Commit</dt>
          <dd>
            <code className="font-mono text-xs" title={d.gitSha}>
              {shortSha(d.gitSha)}
            </code>{' '}
            <span className="text-muted-foreground">on {d.gitRef}</span>
          </dd>
        </div>
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Triggered by</dt>
          <dd>{triggeredByLabel(d)}</dd>
        </div>
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">{d.readyAt ? 'Ready' : 'Created'}</dt>
          <dd>
            <RelativeTime iso={d.readyAt ?? d.createdAt} />
          </dd>
        </div>
      </dl>
      {failure && (
        <Alert tone="error" title="The deployment failed">
          <p data-testid="deployment-failure">{failure}</p>
        </Alert>
      )}
      {d.status === 'canceled' && (
        <p className="text-sm text-muted-foreground">This deployment was canceled (a newer one replaced it, or Vercel canceled it).</p>
      )}
      <BuildLogTail log={d.buildLogTail} defaultOpen={d.status === 'error'} />
    </div>
  )
}
