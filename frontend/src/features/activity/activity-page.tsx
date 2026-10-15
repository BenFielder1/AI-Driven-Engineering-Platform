import { useDocumentTitle } from '../../lib/use-document-title'
import { useProjectAccess } from '../projects/project-context'
import { ActivityFeed } from './activity-feed'

/** /projects/:id/activity (SPEC §7.2): the full activity feed with its category filter (v1 FR-50). */
export function ActivityPage() {
  const { project } = useProjectAccess()
  useDocumentTitle(`Activity · ${project.name}`)
  return (
    <section aria-labelledby="activity-title" className="flex flex-col gap-4">
      <h2 id="activity-title" className="text-xl">
        Activity
      </h2>
      <div className="rounded-xl border border-border/60 bg-card/70 p-4 shadow-sm backdrop-blur-md">
        <ActivityFeed projectId={project.id} />
      </div>
    </section>
  )
}
