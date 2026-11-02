import { useState, type ReactNode } from 'react'

import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { DatabaseCardView } from '../deployments/database-card'
import { deploymentProviderLabels, deploymentTargetLabels } from '../deployments/deployment-format'
import { DeploymentGroupCard } from '../deployments/deployment-group-card'
import { deploymentPartsTitle } from '../deployments/deployment-group-format'
import { layoutOf } from '../deployments/deployment-settings-tab'
import { DeploymentTargetCard } from '../deployments/deployment-target-card'
import { useDeploymentSettings } from '../deployments/deployments-api'
import { useProjectAccess } from '../projects/project-context'
import { StageSection } from '../stages/stage-section'

/** One setup window with its "Skip for now" (FR-184): skipped windows stay in Settings → Deployment. */
function Window({ name, skipped, onSkip, onShow, children }: { name: string; skipped: boolean; onSkip: () => void; onShow: () => void; children: ReactNode }) {
  if (skipped) {
    return (
      <li className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-dashed border-border/60 p-3 text-sm">
        <span>
          <span className="font-medium">{name}:</span> skipped for now. You can set it up later in Settings → Deployment.
        </span>
        <Button variant="ghost" onClick={onShow}>
          Set it up now<span className="sr-only">: {name}</span>
        </Button>
      </li>
    )
  }
  return (
    <li className="flex flex-col gap-2">
      {children}
      <div>
        <Button variant="ghost" onClick={onSkip}>
          Skip for now<span className="sr-only">: {name}</span>
        </Button>
      </div>
    </li>
  )
}

/**
 * FR-184 after approval: the same create or link windows as the wizard, from Settings → Deployment (FR-188 groups first,
 * one window per group; then the targets and databases on their own), each with "Skip for now". Running or skipped setup
 * never blocks Continue to Refinement (FR-185). Settings → Deployment stays the alternative.
 */
export function AnalysisDeploymentSetup() {
  const { project } = useProjectAccess()
  const settings = useDeploymentSettings(project.id)
  const [skipped, setSkipped] = useState<ReadonlySet<string>>(new Set())
  const toggle = (key: string, on: boolean) => {
    setSkipped((current) => {
      const next = new Set(current)
      if (on) {
        next.add(key)
      } else {
        next.delete(key)
      }
      return next
    })
  }

  let body: ReactNode
  if (settings.isPending) {
    body = <LoadingState label="Loading the deployment setup…" />
  } else if (settings.isError) {
    body = <ErrorState error={settings.error} title="The deployment setup couldn't load" onRetry={() => void settings.refetch()} />
  } else {
    const s = settings.data
    const { held, targets, databases } = layoutOf(s)
    const windows: { key: string; name: string; node: ReactNode }[] = [
      ...held.map(({ group, targets: groupTargets, databases: groupDatabases }) => ({
        key: `group-${group.provider}`,
        name: deploymentPartsTitle(group.parts, deploymentProviderLabels[group.provider]),
        node: (
          <DeploymentGroupCard projectId={project.id} group={group} canManage={s.canManage} suggestedName={project.slug}>
            {groupTargets.map((t) => (
              <DeploymentTargetCard key={t.component} project={project} target={t} canManage={s.canManage} joinGroup={!t.groupId && group.id ? { id: group.id } : undefined} />
            ))}
            {groupDatabases.map((d) => (
              <DatabaseCardView key={d.environment} project={project} database={d} canManage={s.canManage} joinGroup={!d.groupId && group.id ? { id: group.id } : undefined} />
            ))}
          </DeploymentGroupCard>
        ),
      })),
      ...targets.map((t) => ({
        key: `target-${t.component}`,
        name: `${t.component === 'frontend' ? 'Frontend' : 'Backend'} on ${deploymentProviderLabels[t.provider]}`,
        node: <DeploymentTargetCard project={project} target={t} canManage={s.canManage} />,
      })),
      ...databases.map((d) => ({
        key: `database-${d.environment}`,
        name: `${deploymentTargetLabels[d.environment]} database`,
        node: <DatabaseCardView project={project} database={d} canManage={s.canManage} />,
      })),
    ]
    body =
      windows.length === 0 ? (
        <p className="text-sm text-muted-foreground">The stack has no hosts to set up.</p>
      ) : (
        <>
          {!s.canManage && <p className="text-sm text-muted-foreground">Only project Owners can set up deployment.</p>}
          <ul aria-label="Deployment setup windows" className="flex flex-col gap-4">
            {windows.map((w) => (
              <Window
                key={w.key}
                name={w.name}
                skipped={skipped.has(w.key)}
                onSkip={() => {
                  toggle(w.key, true)
                }}
                onShow={() => {
                  toggle(w.key, false)
                }}
              >
                {w.node}
              </Window>
            ))}
          </ul>
        </>
      )
  }

  return (
    <StageSection
      id="analysis-deployment"
      title="Deployment setup"
      actions={
        <Link
          to="/projects/$projectId/settings/$section"
          params={{ projectId: project.id, section: 'deployment' }}
          className="text-sm text-primary underline"
        >
          Settings → Deployment
        </Link>
      }
    >
      <p className="text-sm text-muted-foreground">
        Create or link where each part runs. Setup runs in the background and never blocks Continue to Refinement; skip any
        window and finish it later.
      </p>
      {body}
    </StageSection>
  )
}
