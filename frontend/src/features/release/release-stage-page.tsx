import { EmptyState } from '../../components/shared/empty-state'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState } from '../projects/projects-api'
import { StartVersionButton } from '../versions/start-version-button'
import { useReleaseVersionNames } from '../versions/version-label'
import { VersionSummary } from '../versions/version-summary'
import { IntegrationSlot } from '../stages/integration-slot'
import { StageGates } from '../stages/stage-gates'
import { StageHeader } from '../stages/stage-header'
import { stageGates } from '../stages/stage-model'
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
 * /projects/:id/release (FR-55 stage page): the pre-release gate (decided in place), the release panel (FR-42, FR-43,
 * FR-64: attempts, tag, pull request) and, for Owners, Start new version
 * (FR-60; disabled with the reason until the project is released and merged). Before the project reaches Release, an
 * empty state says what has to happen first. A past version (FR-62) shows its summary and release attempts, read-only.
 */
export function ReleaseStagePage() {
  const { project, can, readOnly, releaseVersionId, view } = useProjectAccess()
  useDocumentTitle(`Release · ${project.name}`)
  const state = useProjectState(project.id)

  if (readOnly && releaseVersionId && view.version) {
    return (
      <section aria-labelledby="release-title" className="flex flex-col gap-5">
        <StageHeader stage="release" />
        <VersionSummary version={view.version} />
        <section aria-labelledby="past-release-title" className="flex flex-col gap-2">
          <h3 id="past-release-title" className="font-semibold">
            Release attempts
          </h3>
          <PastReleases projectId={project.id} releaseVersionId={releaseVersionId} />
        </section>
      </section>
    )
  }

  if (state.isPending) {
    return (
      <section aria-labelledby="release-title" className="flex flex-col gap-5">
        <StageHeader stage="release" />
        <LoadingState label="Loading release…" />
      </section>
    )
  }
  if (state.isError) {
    return (
      <section aria-labelledby="release-title" className="flex flex-col gap-5">
        <StageHeader stage="release" />
        <ErrorState error={state.error} onRetry={() => void state.refetch()} title="The release couldn't load" />
      </section>
    )
  }

  const s = state.data
  const started = s.phase === 'release' || s.latestRelease !== null
  return (
    <section aria-labelledby="release-title" className="flex flex-col gap-5">
      <StageHeader
        stage="release"
        awaitingApproval={s.phase === 'release' && s.pendingGates.some((g) => g.type === 'pre_release')}
        actions={can('startVersion') ? <StartVersionButton projectId={project.id} state={s} /> : undefined}
      />
      <StageGates types={stageGates.release} />
      {started ? (
        // The release panel holds the attempts, the tag and the pull request.
        <ReleasePanel projectId={project.id} state={s} canRelease={can('requestRelease')} />
      ) : (
        <EmptyState
          variant="not_started"
          headingLevel={3}
          title="Release hasn't started"
          description="The release starts once review finds the build release-eligible and the project moves to Release."
        />
      )}
      <IntegrationSlot
        id="release-merge-readiness"
        title="Merge readiness"
        description="Whether the release pull request can merge (checks, reviews, conflicts) will show here once the GitHub integration reports it."
      />
      <IntegrationSlot
        id="release-github-release"
        title="GitHub Release"
        description="The GitHub Release created for this version's tag will be linked here."
      />
      <IntegrationSlot
        id="release-production-deployment"
        title="Production deployment"
        description="The production deployment of this version will show here once a deployment target is set."
      />
    </section>
  )
}
