import { EmptyState } from '../../components/shared/empty-state'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState } from '../projects/projects-api'
import { StartVersionButton } from '../versions/start-version-button'
import { useReleaseVersionNames } from '../versions/version-label'
import { VersionSummary } from '../versions/version-summary'
import { ReleaseHistory, ReleasePanel } from './release-panel'
import { useReleases } from './release-api'

/** A past release version's attempts (FR-62 read-only view). */
function PastReleases({ projectId, releaseVersionId }: { projectId: string; releaseVersionId: string }) {
  const releases = useReleases(projectId, true, releaseVersionId)
  const names = useReleaseVersionNames(projectId)
  if (releases.isPending) {
    return <LoadingState label="Loading releases…" />
  }
  if (releases.isError) {
    return <ErrorState error={releases.error} title="The releases couldn't load" onRetry={() => void releases.refetch()} />
  }
  if (releases.data.length === 0) {
    return <p className="text-sm text-muted-foreground">This version has no release attempts.</p>
  }
  return <ReleaseHistory releases={releases.data} versionOf={(id) => names(id)} />
}

/**
 * /projects/:id/release (FR-54 stage route): the release panel (FR-42, FR-43, FR-64) and, for Owners, Start new version
 * (FR-60; disabled with the reason until the project is released and merged). Before the project reaches Release, an
 * empty state says what has to happen first. A past version (FR-62) shows its summary and release attempts, read-only.
 */
export function ReleaseStagePage() {
  const { project, can, readOnly, releaseVersionId, view } = useProjectAccess()
  useDocumentTitle(`Release · ${project.name}`)
  const state = useProjectState(project.id)

  if (readOnly && releaseVersionId && view.version) {
    return (
      <div className="flex flex-col gap-5">
        <h2 className="text-xl">Release</h2>
        <VersionSummary version={view.version} />
        <section aria-labelledby="past-release-title" className="flex flex-col gap-2">
          <h3 id="past-release-title" className="font-semibold">
            Release attempts
          </h3>
          <PastReleases projectId={project.id} releaseVersionId={releaseVersionId} />
        </section>
      </div>
    )
  }

  if (state.isPending) {
    return <LoadingState label="Loading release…" />
  }
  if (state.isError) {
    return <ErrorState error={state.error} onRetry={() => void state.refetch()} title="The release couldn't load" />
  }

  const s = state.data
  const started = s.phase === 'release' || s.latestRelease !== null
  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <h2 className="text-xl">Release</h2>
        {can('startVersion') && <StartVersionButton projectId={project.id} state={s} />}
      </div>
      {started ? (
        <ReleasePanel projectId={project.id} state={s} canRelease={can('requestRelease')} />
      ) : (
        <EmptyState
          variant="not_started"
          headingLevel={3}
          title="Release hasn't started"
          description="The release starts once review finds the build release-eligible and the project moves to Release."
        />
      )}
    </div>
  )
}
