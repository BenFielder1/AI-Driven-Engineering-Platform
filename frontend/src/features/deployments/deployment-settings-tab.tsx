import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import type { Project } from '../projects/projects-api'
import { deploymentTargetLabels, originModelLabel, originModelLabels } from './deployment-format'
import { DeploymentTargetCard } from './deployment-target-card'
import { useDeploymentSettings, type DeploymentSettings } from './deployments-api'
import { EnvInventorySection } from './env-inventory-section'

const databaseProviderLabels: Readonly<Record<string, string>> = {
  supabase: 'Supabase',
  'render-postgres': 'Render Postgres',
  'railway-postgres': 'Railway Postgres',
  external: 'External database',
}

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

/** FR-144: database cards per environment; placeholders until managed databases (FR-152). */
function DatabaseCards({ databases }: { databases: DeploymentSettings['databases'] }) {
  if (databases.length === 0) {
    return null
  }
  return (
    <section aria-labelledby="databases-title" className="flex flex-col gap-3">
      <h3 id="databases-title" className="text-lg">
        Databases
      </h3>
      <ul aria-label="Databases" className="grid gap-3 sm:grid-cols-2">
        {databases.map((db) => (
          <li
            key={db.environment}
            aria-label={`${deploymentTargetLabels[db.environment]} database`}
            className={cn(panelClass, 'flex flex-col gap-2 p-4 text-sm')}
          >
            <span className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-semibold">{deploymentTargetLabels[db.environment]} database</span>
              <StatusBadge tone="neutral">Not set up</StatusBadge>
            </span>
            <span>{databaseProviderLabels[db.provider] ?? db.provider}</span>
            <span className="text-muted-foreground">Database setup arrives with managed databases.</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

/**
 * Settings → Deployment (FR-143–FR-146; replaces v2 FR-88's single Vercel setting). Every member reads it: the
 * origin model, one target card per component (whose connection it uses, resource, URLs, status, readiness), database
 * cards per environment and the environment inventory. Owners (`canManage`) set targets up, verify, unbind and edit
 * user variables.
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
    body = (
      <div className="flex flex-col gap-6">
        {s.fakeMode && (
          <Alert tone="info" title="Fake mode">
            <p>This AIDE server deploys with its Fake provider: setup and deployments are simulated, and nothing is created on the platforms.</p>
          </Alert>
        )}
        {!s.canManage && <p className="text-sm text-muted-foreground">Only project Owners can change the deployment settings.</p>}
        <OriginModelCard projectId={project.id} origin={s.originModel} />
        <section aria-labelledby="targets-title" className="flex flex-col gap-3">
          <h3 id="targets-title" className="text-lg">
            Targets
          </h3>
          {s.targets.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              The stack has no deployment hosts. Choose a frontend or backend host in Settings → Stack to deploy.
            </p>
          ) : (
            s.targets.map((t) => <DeploymentTargetCard key={t.component} project={project} target={t} canManage={s.canManage} />)
          )}
        </section>
        <DatabaseCards databases={s.databases} />
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
