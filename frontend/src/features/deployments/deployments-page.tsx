import { Link } from '../../lib/navigation'
import { useState } from 'react'

import { DataTable, type DataTableColumn } from '../../components/shared/data-table'
import { EmptyState } from '../../components/shared/empty-state'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { useProjectAccess } from '../projects/project-context'
import { RelativeTime } from '../projects/relative-time'
import { DeployPreviewButton, RedeployButton } from './deploy-buttons'
import {
  componentLabels,
  currentProduction,
  isCloudflarePages,
  deploymentProviderLabels,
  deploymentTargetLabels,
  rollbackBlockedCopy,
  rollbackWording,
  shortSha,
  triggeredByLabel,
} from './deployment-format'
import { DeploymentLink, DeploymentStatusPill } from './deployment-parts'
import { DeploymentProviderLogo } from './deployment-provider-logo'
import {
  deploymentComponents,
  deploymentTargets,
  useDeploymentSettings,
  useDeployments,
  type Deployment,
  type DeploymentComponent,
  type DeploymentTarget,
} from './deployments-api'
import { RollbackDeploymentDialog } from './rollback-dialog'

type EnvironmentFilter = DeploymentTarget | 'all'
type ComponentFilter = DeploymentComponent | 'all'

/**
 * /projects/:id/deployments (FR-90, FR-91, FR-150, FR-151, SPEC §7): every deployment with component, environment,
 * provider, version, SHA, state (a live pill while queued or building), URL, time, who triggered it and a "Current"
 * badge; component and environment filters (sent to the API); rollback per provider capability (Owners, worded as the
 * platform words it; a production row that can't be rolled back to says why, as text) and Redeploy (previews:
 * writers; production: Owners). Not configured: an empty state pointing at Settings → Deployment.
 */
export function DeploymentsPage() {
  const { project, can, readOnly, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Deployments · ${project.name}`)
  const [environment, setEnvironment] = useState<EnvironmentFilter>('all')
  const [component, setComponent] = useState<ComponentFilter>('all')
  const [rollingBack, setRollingBack] = useState<Deployment | null>(null)
  const enabled = project.deploymentsEnabled
  // A past version (FR-62) lists only its own deployments.
  const deployments = useDeployments(
    project.id,
    {
      component: component === 'all' ? undefined : component,
      environment: environment === 'all' ? undefined : environment,
      releaseVersionId: readOnly ? releaseVersionId : undefined,
    },
    enabled,
  )
  const rows = deployments.data ?? []
  // FR-150: Cloudflare Workers and Pages roll back differently; the target's resource id (`pages:<project>`) says which.
  const settings = useDeploymentSettings(project.id, enabled && rows.some((d) => d.platform === 'cloudflare'))
  const cloudflarePages = (c: DeploymentComponent) =>
    settings.data?.targets.some((t) => t.component === c && t.provider === 'cloudflare' && isCloudflarePages(t.resourceId)) ?? false
  const owner = can('manageDeployment')
  const writer = can('deployPreview')

  const header = (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h2 id="deployments-title" className="text-xl">
          Deployments
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Preview deployments of the version branch and production deployments of each release, per component.
        </p>
      </div>
      {enabled && writer && <DeployPreviewButton projectId={project.id} />}
    </div>
  )

  if (!enabled) {
    return (
      <section aria-labelledby="deployments-title" className="flex flex-col gap-4">
        {header}
        <EmptyState
          variant="not_connected"
          headingLevel={3}
          title="Deployment isn't set up"
          description={
            owner
              ? 'Set up the deployment targets in Settings → Deployment to deploy previews of each run and production on every release.'
              : 'An Owner can set up the deployment targets in Settings → Deployment.'
          }
          action={
            owner ? (
              <Link
                to="/projects/$projectId/settings/$section"
                params={{ projectId: project.id, section: 'deployment' }}
                className={buttonClasses('primary')}
              >
                Set up deployment
              </Link>
            ) : undefined
          }
        />
      </section>
    )
  }

  const columns: DataTableColumn<Deployment>[] = [
    {
      key: 'component',
      header: 'Component',
      primary: true,
      cell: (d) => (
        <span className="flex flex-wrap items-center gap-2">
          {componentLabels[d.component]}
          {d.current && (
            <span className="rounded-full bg-success/15 px-2 text-xs font-medium" data-testid="deployment-current">
              Current
            </span>
          )}
        </span>
      ),
      sortValue: (d) => d.component,
    },
    { key: 'environment', header: 'Environment', cell: (d) => deploymentTargetLabels[d.target], sortValue: (d) => d.target },
    {
      key: 'provider',
      header: 'Provider',
      cell: (d) => (
        <span className="inline-flex items-center gap-1.5">
          <DeploymentProviderLogo provider={d.platform} />
          {deploymentProviderLabels[d.platform]}
        </span>
      ),
      sortValue: (d) => d.platform,
    },
    { key: 'version', header: 'Version', cell: (d) => <span className="font-mono">{d.releaseVersion}</span>, sortValue: (d) => d.releaseVersion },
    {
      key: 'sha',
      header: 'Commit',
      cell: (d) => (
        <code className="font-mono text-xs" title={d.gitSha}>
          {shortSha(d.gitSha)}
        </code>
      ),
    },
    { key: 'status', header: 'State', cell: (d) => <DeploymentStatusPill status={d.status} />, sortValue: (d) => d.status },
    { key: 'url', header: 'URL', cell: (d) => <DeploymentLink url={d.url} /> },
    { key: 'created', header: 'Time', cell: (d) => <RelativeTime iso={d.createdAt} />, sortValue: (d) => d.createdAt },
    { key: 'triggeredBy', header: 'Triggered by', cell: (d) => triggeredByLabel(d) },
    {
      key: 'actions',
      header: 'Actions',
      cell: (d) => {
        const mayRedeploy = d.target === 'preview' ? writer : owner
        const active = d.status === 'queued' || d.status === 'building'
        const action = rollbackWording(d.platform, cloudflarePages(d.component)).action
        // FR-151: reasons are always visible text, never hover-only.
        const blocked = d.target === 'production' && !d.canRollback ? rollbackBlockedCopy(d) : null
        return (
          <span className="flex flex-wrap items-center gap-2">
            {owner && d.canRollback && (
              <Button
                variant="secondary"
                aria-label={`${action} ${d.releaseVersion} (${shortSha(d.gitSha)})`}
                onClick={() => {
                  setRollingBack(d)
                }}
              >
                {action}
              </Button>
            )}
            {mayRedeploy && !active && <RedeployButton projectId={project.id} deployment={d} />}
            {blocked && (
              <span className="text-xs text-muted-foreground" data-testid="rollback-blocked">
                {blocked}
              </span>
            )}
          </span>
        )
      },
    },
  ]

  let body
  if (deployments.isPending) {
    body = <LoadingState label="Loading deployments…" />
  } else if (deployments.isError) {
    body = <ErrorState error={deployments.error} title="The deployments couldn't load" onRetry={() => void deployments.refetch()} />
  } else {
    body = (
      <DataTable
        columns={columns}
        rows={rows}
        rowKey={(d) => d.id}
        caption="Deployments, newest first"
        pageSize={20}
        empty={
          <EmptyState
            variant="no_data"
            headingLevel={3}
            title={environment === 'all' && component === 'all' ? 'No deployments yet' : 'No deployments match these filters'}
            description="AIDE deploys a preview when execution opens or updates the pull request, and production after each release."
          />
        }
      />
    )
  }

  return (
    <section aria-labelledby="deployments-title" className="flex flex-col gap-4">
      {header}
      <div role="group" aria-label="Filter deployments" className="grid gap-3 sm:max-w-lg sm:grid-cols-2">
        <SelectField
          id="deployments-component"
          label="Component"
          value={component}
          onChange={(e) => {
            const value = e.target.value
            setComponent(value === 'frontend' || value === 'backend' ? value : 'all')
          }}
        >
          <option value="all">All components</option>
          {deploymentComponents.map((c) => (
            <option key={c} value={c}>
              {componentLabels[c]}
            </option>
          ))}
        </SelectField>
        <SelectField
          id="deployments-environment"
          label="Environment"
          value={environment}
          onChange={(e) => {
            const value = e.target.value
            setEnvironment(value === 'preview' || value === 'production' ? value : 'all')
          }}
        >
          <option value="all">All environments</option>
          {deploymentTargets.map((t) => (
            <option key={t} value={t}>
              {deploymentTargetLabels[t]}
            </option>
          ))}
        </SelectField>
      </div>
      {body}
      {rollingBack && (
        <RollbackDeploymentDialog
          projectId={project.id}
          deployment={rollingBack}
          cloudflarePages={cloudflarePages(rollingBack.component)}
          current={currentProduction(rows, rollingBack.component)}
          onClose={() => {
            setRollingBack(null)
          }}
        />
      )}
    </section>
  )
}
