import { EmptyState } from '../../components/shared/empty-state'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { canInProject } from '../../lib/permissions'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState } from '../projects/projects-api'
import { ReleasePanel } from './release-panel'

/**
 * /projects/:id/release (FR-54 stage route): the v1 release panel (FR-42, FR-43), until the
 * FR-55 stage page replaces it. Before the project reaches Release, an empty state says what has to happen first.
 */
export function ReleaseStagePage() {
  const { project, user, role } = useProjectAccess()
  useDocumentTitle(`Release · ${project.name}`)
  const state = useProjectState(project.id)

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
      <h2 className="text-xl">Release</h2>
      {started ? (
        <ReleasePanel projectId={project.id} state={s} canRelease={canInProject(user, role, 'requestRelease')} />
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
