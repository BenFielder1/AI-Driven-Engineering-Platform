import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { PlatformLogo } from '../connections/platform-logos'
import { GitHostLink } from '../git-hosts/git-host-link'
import { safeExternalUrl } from './tracker-picks'
import { useTrackers, type TrackerStatus } from './trackers-api'

/** FR-165, FR-167, FR-168: why a tracker can't be browsed yet, in words. */
export function trackerReasonCopy(tracker: Pick<TrackerStatus, 'provider' | 'name' | 'reason'>): string {
  switch (tracker.reason) {
    case 'issues_permission_missing':
      return "Issues permission missing: update the App's permissions in GitHub, then accept them on each installation"
    case 'app_not_registered':
      return "The AIDE GitHub App isn't registered on this server yet. An Admin can register it."
    default:
      switch (tracker.provider) {
        case 'github':
          return 'Connect your GitHub account under Source control to use GitHub Issues.'
        case 'gitlab':
          return 'Connect a GitLab account under Source control to use GitLab Issues.'
        default:
          return `Add a ${tracker.name} connection under Issue trackers.`
      }
  }
}

/** The way out of a not-ready tracker: the reason, and the App settings link when there is one. */
export function TrackerReasonNotice({ tracker }: { tracker: TrackerStatus }) {
  const settings = tracker.settingsUrl ? safeExternalUrl(tracker.settingsUrl) : null
  return (
    <div className="text-sm" data-testid={`tracker-reason-${tracker.provider}`}>
      <p>{trackerReasonCopy(tracker)}</p>
      {settings && (
        <p className="mt-1">
          <GitHostLink site="GitHub" href={settings}>
            Open the GitHub App settings
          </GitHostLink>
        </p>
      )}
    </div>
  )
}

function ReadinessRow({ tracker }: { tracker: TrackerStatus }) {
  const titleId = `${tracker.provider}-tracker-title`
  return (
    <section aria-labelledby={titleId} className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={titleId} className="flex items-center gap-2 font-semibold">
          <PlatformLogo provider={tracker.provider} className="size-5" />
          {tracker.name}
        </h3>
        <Badge tone={tracker.ready ? 'success' : 'warning'}>{tracker.ready ? 'Ready' : 'Not ready'}</Badge>
      </div>
      {tracker.ready ? (
        <p className="text-sm text-muted-foreground">
          Uses your {tracker.provider === 'github' ? 'GitHub' : 'GitLab'} connection, with no further setup
          {tracker.connections.length > 0 ? `: ${tracker.connections.map((c) => c.accountName ?? c.label).join(', ')}.` : '.'}
        </p>
      ) : (
        <TrackerReasonNotice tracker={tracker} />
      )}
    </section>
  )
}

/** FR-167, FR-168: GitHub and GitLab Issues reuse the Git connections, so this section only says whether they're ready. */
export function TrackerReadiness() {
  const trackers = useTrackers()
  if (trackers.isPending) {
    return <LoadingState label="Checking GitHub Issues and GitLab Issues…" />
  }
  if (trackers.isError) {
    return <ErrorState error={trackers.error} title="GitHub Issues and GitLab Issues couldn't be checked" onRetry={() => void trackers.refetch()} />
  }
  const gitTrackers = trackers.data.filter((t) => t.provider === 'github' || t.provider === 'gitlab')
  if (gitTrackers.length === 0) {
    return <Alert tone="info">GitHub Issues and GitLab Issues aren&apos;t available on this server.</Alert>
  }
  return (
    <div className="flex flex-col gap-4">
      {gitTrackers.map((t) => (
        <ReadinessRow key={t.provider} tracker={t} />
      ))}
    </div>
  )
}
