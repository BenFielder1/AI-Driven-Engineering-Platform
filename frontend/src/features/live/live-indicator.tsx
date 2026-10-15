import { StatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { useProjectLive, type LiveStatus } from './use-project-live'

/** What assistive tech hears (and the pill's tooltip) when updates aren't arriving. */
const copy: Record<LiveStatus, string | null> = {
  connecting: null,
  live: null,
  reconnecting: 'Live updates interrupted. Reconnecting…',
  offline: 'Live updates are off. Reload the page to see other people’s changes.',
}

const pill: Record<LiveStatus, { tone: StatusTone; label: string; pulse: boolean }> = {
  connecting: { tone: 'neutral', label: 'Connecting', pulse: true },
  live: { tone: 'success', label: 'Live', pulse: true },
  reconnecting: { tone: 'warning', label: 'Reconnecting', pulse: true },
  offline: { tone: 'destructive', label: 'Offline: reload', pulse: false },
}

/**
 * Mounted by the shell header on project pages (SPEC §7.1): keeps the project's live connection open (FR-46) and shows
 * a live status pill. When updates aren't arriving it says so in a polite status, so the page is never silently stale.
 */
export function ProjectLive({ projectId }: { projectId: string }) {
  const status = useProjectLive(projectId)
  const text = copy[status]
  const p = pill[status]
  return (
    <span className="flex items-center" title={text ?? 'Live updates are on'} data-testid="live-indicator" data-status={status}>
      <span aria-hidden>
        <StatusBadge tone={p.tone} pulse={p.pulse}>
          {p.label}
        </StatusBadge>
      </span>
      <span role="status" aria-live="polite" className="sr-only">
        {text}
      </span>
    </span>
  )
}
