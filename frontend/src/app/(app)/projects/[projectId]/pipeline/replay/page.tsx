'use client'

import { ReplayView } from '../../../../../../engine/pages/replay-view'
import { useProjectAccess } from '../../../../../../features/projects/project-context'
import { buttonClasses } from '../../../../../../components/ui/button-classes'
import { Link, useSearch } from '../../../../../../lib/navigation'

function parseReplaySearch(raw: Record<string, unknown>): { runId?: string } {
  return typeof raw.runId === 'string' && raw.runId !== '' ? { runId: raw.runId } : {}
}

/** /projects/:id/pipeline/replay?runId= (FR-306): step through one run's timeline. */
export default function ReplayRoute() {
  const { project } = useProjectAccess()
  const { runId } = useSearch(parseReplaySearch)
  return (
    <ReplayView
      projectId={project.id}
      runId={runId}
      documentTitle={`Replay · ${project.name}`}
      backLink={
        <Link
          to="/projects/$projectId/pipeline"
          params={{ projectId: project.id }}
          search={runId ? { run: runId } : {}}
          className={buttonClasses('secondary')}
        >
          Back to the pipeline
        </Link>
      }
    />
  )
}
