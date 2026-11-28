'use client'

import { PipelineView } from '../../../../../engine/pages/pipeline-view'
import { useProjectAccess } from '../../../../../features/projects/project-context'
import { buttonClasses } from '../../../../../components/ui/button-classes'
import { Link, useSearch, useSetSearch } from '../../../../../lib/navigation'

/** `?run=<id>`: the run the picker shows (the newest when absent). */
function parsePipelineSearch(raw: Record<string, unknown>): { run?: string } {
  return typeof raw.run === 'string' && raw.run !== '' ? { run: raw.run } : {}
}

/** /projects/:id/pipeline (FR-306): the engine's Pipeline view of the project's runs. Picking a run replaces the entry. */
export default function PipelineRoute() {
  const { project, role, user, readOnly } = useProjectAccess()
  const search = useSearch(parsePipelineSearch)
  const setSearch = useSetSearch()
  return (
    <PipelineView
      projectId={project.id}
      access={{ role: readOnly ? 'viewer' : role, userId: user?.id ?? null }}
      runId={search.run}
      documentTitle={`Pipeline · ${project.name}`}
      onRunChange={(run) => {
        void setSearch({ run }, { replace: true })
      }}
      runLink={(runId, label) => (
        <Link to="/projects/$projectId/pipeline" params={{ projectId: project.id }} search={{ run: runId }} className={buttonClasses('secondary')}>
          {label}
        </Link>
      )}
      replayLink={(runId) => (
        <Link
          to="/projects/$projectId/pipeline/replay"
          params={{ projectId: project.id }}
          search={{ runId }}
          className={buttonClasses('secondary')}
        >
          Replay this run
        </Link>
      )}
    />
  )
}
