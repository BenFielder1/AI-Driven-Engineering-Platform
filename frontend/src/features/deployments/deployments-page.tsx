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
import { canPromote, deploymentTargetLabels, liveProduction, shortSha, triggeredByLabel } from './deployment-format'
import { DeploymentLink, DeploymentStatusPill } from './deployment-parts'
import { deploymentTargets, useDeployments, type Deployment, type DeploymentTarget } from './deployments-api'
import { PromoteDeploymentDialog } from './promote-dialog'

type TargetFilter = DeploymentTarget | 'all'

/**
 * /projects/:id/deployments (FR-90, FR-91, SPEC §7.2): every deployment with target, version, SHA, state (a live pill
 * while queued or building), URL, time and who triggered it; a target filter; Promote (Owners, ready production) and
 * Redeploy (previews: writers; production: Owners). Not configured: an empty state pointing at Settings → Deployment.
 */
export function DeploymentsPage() {
  const { project, can, readOnly, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Deployments · ${project.name}`)
  const [target, setTarget] = useState<TargetFilter>('all')
  const [promoting, setPromoting] = useState<Deployment | null>(null)
  const enabled = project.deploymentsEnabled
  // A past version (FR-62) lists only its own deployments.
  const deployments = useDeployments(
    project.id,
    { target: target === 'all' ? undefined : target, releaseVersionId: readOnly ? releaseVersionId : undefined },
    enabled,
  )
  // "Live" is known from the production rows whatever the filter shows.
  const production = useDeployments(
    project.id,
    { target: 'production', releaseVersionId: readOnly ? releaseVersionId : undefined },
    enabled && target === 'preview',
  )
  const owner = can('manageDeployment')
  const writer = can('deployPreview')

  const header = (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h2 id="deployments-title" className="text-xl">
          Deployments
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Preview deployments of the version branch and production deployments of each release.
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
              ? 'Set up Vercel in Settings → Deployment to deploy previews of each run and production on every release.'
              : 'An Owner can set up Vercel in Settings → Deployment.'
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

  const rows = deployments.data ?? []
  const live = liveProduction(target === 'preview' ? (production.data ?? []) : rows)

  const columns: DataTableColumn<Deployment>[] = [
    {
      key: 'target',
      header: 'Target',
      primary: true,
      cell: (d) => (
        <span className="flex flex-wrap items-center gap-2">
          {deploymentTargetLabels[d.target]}
          {d.id === live?.id && (
            <span className="rounded-full bg-success/15 px-2 text-xs font-medium" data-testid="deployment-live">
              Live
            </span>
          )}
        </span>
      ),
      sortValue: (d) => d.target,
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
        return (
          <span className="flex flex-wrap gap-2">
            {owner && canPromote(d, live) && (
              <Button
                variant="secondary"
                aria-label={`Promote ${d.releaseVersion} (${shortSha(d.gitSha)})`}
                onClick={() => {
                  setPromoting(d)
                }}
              >
                Promote
              </Button>
            )}
            {mayRedeploy && !active && <RedeployButton projectId={project.id} deployment={d} />}
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
            title={target === 'all' ? 'No deployments yet' : `No ${deploymentTargetLabels[target].toLowerCase()} deployments yet`}
            description="AIDE deploys a preview when execution opens or updates the pull request, and production after each release."
          />
        }
      />
    )
  }

  return (
    <section aria-labelledby="deployments-title" className="flex flex-col gap-4">
      {header}
      <div className="max-w-xs">
        <SelectField
          id="deployments-target"
          label="Target"
          value={target}
          onChange={(e) => {
            const value = e.target.value
            setTarget(value === 'preview' || value === 'production' ? value : 'all')
          }}
        >
          <option value="all">All targets</option>
          {deploymentTargets.map((t) => (
            <option key={t} value={t}>
              {deploymentTargetLabels[t]}
            </option>
          ))}
        </SelectField>
      </div>
      {body}
      {promoting && (
        <PromoteDeploymentDialog
          projectId={project.id}
          deployment={promoting}
          current={live}
          onClose={() => {
            setPromoting(null)
          }}
        />
      )}
    </section>
  )
}
