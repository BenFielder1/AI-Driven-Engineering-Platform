import { Link } from '@tanstack/react-router'

import { Alert } from '../../components/ui/alert'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { artifactTypeLabels, artifactTypes } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { authorName } from './artifact-author'
import { ArtifactStatusBadge, IntegrityBadge } from './artifact-badges'
import { corruptedCopy } from './artifact-integrity'
import { useArtifacts, type ArtifactSummary, type ArtifactType } from './artifacts-api'

/** Which phase produces each type, for the empty state (SPEC §4.1). */
const producedIn: Record<ArtifactType, string> = {
  project_request: 'Refinement produces the project request.',
  technical_specification: 'The Specification phase produces the technical specification.',
  implementation_plan: 'The Planning phase produces the implementation plan.',
  optimization_plan: 'A review cycle that needs more work produces an optimisation plan.',
  codebase_summary: 'The Repository Analyst produces a codebase summary when a new version starts.',
}

/**
 * The card shows the latest version only; it counts as current for the FR-20 copy (nothing newer replaced it), see
 * `isCurrentVersion`.
 */
function ArtifactCard({ projectId, type, latest }: { projectId: string; type: ArtifactType; latest: ArtifactSummary | undefined }) {
  const headingId = `artifact-card-${type}`
  return (
    <li>
      <section
        aria-labelledby={headingId}
        className={
          'flex h-full flex-col gap-3 rounded-xl border bg-card/70 p-4 shadow-sm backdrop-blur-md ' +
          (latest?.integrity === 'corrupted' ? 'border-destructive' : 'border-border/60')
        }
      >
        <h2 id={headingId} className="text-lg">
          {artifactTypeLabels[type]}
        </h2>
        {latest ? (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-mono font-medium tabular-nums">Version {latest.version}</span>
              <ArtifactStatusBadge status={latest.status} />
              <IntegrityBadge integrity={latest.integrity} />
            </div>
            {latest.integrity === 'corrupted' && (
              <Alert tone="error">{corruptedCopy(latest, true).body}</Alert>
            )}
            <p className="text-sm text-muted-foreground">
              By {authorName(latest.author)} · <RelativeTime iso={latest.createdAt} />
            </p>
            <Link
              to="/projects/$projectId/artifacts/$type"
              params={{ projectId, type }}
              className="mt-auto self-start text-sm font-medium text-primary underline"
            >
              Open {artifactTypeLabels[type].toLowerCase()}
            </Link>
          </>
        ) : (
          <EmptyState title="No versions yet">{producedIn[type]}</EmptyState>
        )}
      </section>
    </li>
  )
}

/** /projects/:id/artifacts (FR-21, SPEC §7): a card per type with its latest version, status and integrity. */
export function ArtifactsPage() {
  const { project, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Artifacts · ${project.name}`)
  const artifacts = useArtifacts(project.id, releaseVersionId)

  let body
  if (artifacts.isPending) {
    body = <LoadingState label="Loading artifacts…" />
  } else if (artifacts.isError) {
    body = (
      <ErrorState error={artifacts.error} onRetry={() => void artifacts.refetch()} title="The artifacts couldn't load" />
    )
  } else {
    const data = artifacts.data
    body = (
      <ul aria-label="Artifact types" className="grid gap-4 sm:grid-cols-2">
        {artifactTypes.map((type) => (
          <ArtifactCard key={type} projectId={project.id} type={type} latest={data.find((a) => a.type === type)} />
        ))}
      </ul>
    )
  }

  return body
}
