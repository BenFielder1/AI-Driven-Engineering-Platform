import { Link } from '@tanstack/react-router'

import { useDocumentTitle } from '../../lib/use-document-title'

/** /projects/:id/artifacts/:type with a type that isn't one of the four artifact types. */
export function ArtifactTypeNotFound({ projectId }: { projectId: string }) {
  useDocumentTitle('Artifact not found')
  return (
    <section aria-labelledby="artifact-not-found" className="py-2">
      <h2 id="artifact-not-found" className="text-xl">
        There&apos;s no artifact of that type
      </h2>
      <p className="mt-2 text-muted-foreground">Artifacts are the project request, specification, implementation plan and optimisation plan.</p>
      <Link to="/projects/$projectId/artifacts" params={{ projectId }} className="mt-3 inline-block text-primary underline">
        All artifacts
      </Link>
    </section>
  )
}
