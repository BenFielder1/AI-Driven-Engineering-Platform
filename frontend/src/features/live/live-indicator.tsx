import { useProjectLive, type LiveStatus } from './use-project-live'

const copy: Record<LiveStatus, string | null> = {
  connecting: null,
  live: null,
  reconnecting: 'Live updates interrupted. Reconnecting…',
  offline: 'Live updates are off. Reload the page to see other people’s changes.',
}

/**
 * Mounted by the project layout: keeps the project's live connection open (FR-46) and says so only when updates
 * aren't arriving, so the page never silently shows stale data.
 */
export function ProjectLive({ projectId }: { projectId: string }) {
  const status = useProjectLive(projectId)
  const text = copy[status]
  return (
    <p role="status" aria-live="polite" className="text-sm text-muted empty:hidden">
      {text}
    </p>
  )
}
