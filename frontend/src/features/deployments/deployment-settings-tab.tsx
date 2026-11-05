import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import type { Project } from '../projects/projects-api'
import { deploymentTargetLabels, originModelLabel, originModelLabels } from './deployment-format'
import { DatabaseCardView } from './database-card'
import { DeploymentGroupCard } from './deployment-group-card'
import { DeploymentTargetCard } from './deployment-target-card'
import { useDeploymentSettings, type DeploymentGroup, type DeploymentSettings } from './deployments-api'
import { EnvInventorySection } from './env-inventory-section'

/** FR-146: the composition's origin model, the stack-packs vocabulary and the explanation. */
function OriginModelCard({ projectId, origin }: { projectId: string; origin: DeploymentSettings['originModel'] }) {
  const others = origin.options.filter((o) => o !== origin.model)
  return (
    <section aria-labelledby="origin-model-title" data-testid="origin-model" className={cn(panelClass, 'flex flex-col gap-2 p-4 text-sm')}>
      <h3 id="origin-model-title" className="text-lg">
        Origin model
      </h3>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        <dt className="text-muted-foreground">Model</dt>
        <dd data-testid="origin-model-value" className="font-medium">
          {originModelLabel(origin.model)}
        </dd>
        <dt className="text-muted-foreground">Stack packs call it</dt>
        <dd data-testid="origin-pack-model">{origin.packModel ?? 'Not applicable'}</dd>
      </dl>
      {origin.explanation && <p>{origin.explanation}</p>}
      {others.length > 0 && (
        <p className="text-muted-foreground">
          This stack also allows {others.map((o) => originModelLabels[o] ?? o).join(', ')}. Change it in{' '}
          <Link
            to="/projects/$projectId/settings/$section"
            params={{ projectId, section: 'stack' }}
            className="font-medium text-primary underline underline-offset-2"
          >
            Settings → Stack
          </Link>
          .
        </p>
      )}
    </section>
  )
}

/** FR-152: one database card per environment (production first), when the composition has a database host. */
function DatabaseCards({ project, databases, canManage }: { project: Project; databases: DeploymentSettings['databases']; canManage: boolean }) {
  if (databases.length === 0) {
    return null
  }
  return (
    <section aria-labelledby="databases-title" className="flex flex-col gap-3">
      <h3 id="databases-title" className="text-lg">
        Databases
      </h3>
      <ul aria-label="Databases" className="flex flex-col gap-3">
        {databases.map((db) => (
          <li key={db.environment} aria-label={`${deploymentTargetLabels[db.environment]} database`}>
            <DatabaseCardView project={project} database={db} canManage={canManage} />
          </li>
        ))}
      </ul>
    </section>
  )
}

const partOfTarget = (t: DeploymentSettings['targets'][number]) => t.component
const setUp = (row: { resourceId: string | null; binding: unknown }) => row.resourceId !== null || row.binding !== null

/**
 * FR-191: which cards go where. A set-up group holds the rows tied to it by `groupId`, and any of its parts that left it
 * and aren't set up (which can rejoin). A part set up on its own stays outside. A group with no row yet whose parts were
 * set up alone in v4 isn't shown at all: those rows look as they did, since Tevrin never moves platform resources (D-V5-9).
 */
export function layoutOf(s: DeploymentSettings) {
  const inGroup = (group: DeploymentGroup) => ({
    targets: s.targets.filter((t) => t.groupId === group.id || (!t.groupId && group.parts.includes(partOfTarget(t)) && !setUp(t))),
    databases: s.databases.filter((d) => d.groupId === group.id || (!d.groupId && group.parts.includes('database') && !setUp(d))),
  })
  const alone = (group: DeploymentGroup) =>
    group.id === null &&
    (s.targets.some((t) => group.parts.includes(partOfTarget(t)) && setUp(t)) || s.databases.some((d) => group.parts.includes('database') && setUp(d)))
  const groups = s.groups.filter((g) => !alone(g))
  const members = groups.map((group) => ({ group, ...inGroup(group) }))
  const takenTargets = new Set(members.flatMap((m) => m.targets))
  const takenDatabases = new Set(members.flatMap((m) => m.databases))
  return {
    // A group with no row yet shows its form and part statuses: its parts have nothing of their own to show.
    held: members.map((m) => (m.group.id === null ? { ...m, targets: [], databases: [] } : m)),
    targets: s.targets.filter((t) => !takenTargets.has(t)),
    databases: s.databases.filter((d) => !takenDatabases.has(d)),
  }
}

/**
 * Settings → Deployment (FR-143–FR-146; replaces v2 FR-88's single Vercel setting). Every member reads it: the
 * origin model, one target card per component (whose connection it uses, resource, URLs, status, readiness), database
 * cards per environment and the environment inventory. FR-191: group cards come first and hold their parts' cards. Owners
 * (`canManage`) set targets up, verify, unbind and edit user variables.
 */
export function DeploymentSettingsTab({ project }: { project: Project; canEdit?: boolean }) {
  const settings = useDeploymentSettings(project.id)

  let body
  if (settings.isPending) {
    body = <LoadingState label="Loading the deployment settings…" />
  } else if (settings.isError) {
    body = <ErrorState error={settings.error} onRetry={() => void settings.refetch()} title="The deployment settings couldn't load" />
  } else {
    const s = settings.data
    const { held, targets, databases } = layoutOf(s)
    body = (
      <div className="flex flex-col gap-6">
        {s.fakeMode && (
          <Alert tone="info" title="Fake mode">
            <p>This Tevrin server deploys with its Fake provider: setup and deployments are simulated, and nothing is created on the platforms.</p>
          </Alert>
        )}
        {!s.canManage && <p className="text-sm text-muted-foreground">Only project Owners can change the deployment settings.</p>}
        <OriginModelCard projectId={project.id} origin={s.originModel} />
        {held.map(({ group, targets: groupTargets, databases: groupDatabases }) => (
          <DeploymentGroupCard key={group.provider} projectId={project.id} group={group} canManage={s.canManage} suggestedName={project.slug}>
            {groupTargets.map((t) => (
              <DeploymentTargetCard
                key={t.component}
                project={project}
                target={t}
                canManage={s.canManage}
                joinGroup={!t.groupId && group.id ? { id: group.id } : undefined}
              />
            ))}
            {groupDatabases.map((d) => (
              <DatabaseCardView
                key={d.environment}
                project={project}
                database={d}
                canManage={s.canManage}
                joinGroup={!d.groupId && group.id ? { id: group.id } : undefined}
              />
            ))}
          </DeploymentGroupCard>
        ))}
        {(targets.length > 0 || held.length === 0) && (
          <section aria-labelledby="targets-title" className="flex flex-col gap-3">
            <h3 id="targets-title" className="text-lg">
              Targets
            </h3>
            {targets.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                The stack has no deployment hosts. Choose a frontend or backend host in Settings → Stack to deploy.
              </p>
            ) : (
              targets.map((t) => <DeploymentTargetCard key={t.component} project={project} target={t} canManage={s.canManage} />)
            )}
          </section>
        )}
        <DatabaseCards project={project} databases={databases} canManage={s.canManage} />
        <EnvInventorySection projectId={project.id} inventory={s.env} canManage={s.canManage} />
      </div>
    )
  }

  return (
    <section aria-labelledby="deployment-title" className="max-w-3xl min-w-0">
      <h2 id="deployment-title" className="mb-4 text-xl">
        Deployment
      </h2>
      {body}
    </section>
  )
}
