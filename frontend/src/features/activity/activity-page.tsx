import { useDocumentTitle } from '../../lib/use-document-title'
import { useProjectAccess } from '../projects/project-context'
import { ActivityFeed } from './activity-feed'
import { panelClass } from '../../components/shared/panel-class'
import { cn } from '../../lib/utils'

/** /projects/:id/activity (SPEC §7.2): the full activity feed with its category filter (0.1.0 FR-50). */
export function ActivityPage() {
  const { project, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Activity · ${project.name}`)
  return (
    <section aria-labelledby="activity-title" className="flex flex-col gap-4">
      <h2 id="activity-title" className="text-xl">
        Activity
      </h2>
      <div className={cn(panelClass, 'p-4')}>
        <ActivityFeed projectId={project.id} releaseVersionId={releaseVersionId} />
      </div>
    </section>
  )
}
