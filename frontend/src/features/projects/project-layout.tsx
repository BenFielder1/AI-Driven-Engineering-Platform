import { Link, useMatchRoute, useRouterState } from '@tanstack/react-router'
import type { ReactNode } from 'react'

import { EmptyState } from '../../components/shared/empty-state'
import { panelClass } from '../../components/shared/panel-class'
import { SkeletonPage } from '../../components/shared/skeletons'
import { buttonClasses } from '../../components/ui/button-classes'
import { Skeleton } from '../../components/ui/skeleton'
import { ErrorState, ForbiddenState } from '../../components/ui/states'
import { isApiError } from '../../lib/errors'
import { canInProject, effectiveProjectRole } from '../../lib/permissions'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { useCurrentUser } from '../auth/auth-api'
import { actionHref, nextAction } from '../lifecycle/next-action'
import { NextActionButton } from '../lifecycle/next-action-button'
import { stageFromPath } from '../lifecycle/stage-nav'
import { StageNavButtons, StageStepper } from '../lifecycle/stage-stepper'
import { matchesHref } from '../shell/nav-config'
import { stageStatuses } from '../shell/stage-states'
import { PhaseBadge, StatusBadge } from './project-badges'
import { currentView, ProjectContext, ReleaseViewContext } from './project-context'
import { ProjectVersionBadges } from '../../components/shared/version-badge'
import { PresetChip } from '../presets/preset-chip'
import { ReadOnlyBanner } from '../versions/read-only-banner'
import { useSelectedReleaseVersion } from '../versions/use-selected-release-version'
import { checkpointLabel } from '../versions/version-label'
import { useProject, useProjectState, type Project } from './projects-api'

function ProjectNotFound() {
  useDocumentTitle('Project not found')
  return (
    <EmptyState
      variant="no_data"
      headingLevel={1}
      title="Project not found"
      description="It may have been deleted, or you may not be a member. Ask a project Owner to add you."
      action={
        <Link to="/projects" className={buttonClasses('secondary')}>
          Back to projects
        </Link>
      }
      className="my-6"
    />
  )
}

/**
 * FR-54 project header: name, phase and status, the version badges and checkpoint (FR-62, FR-63; the version switcher
 * is in the shell header), the primary next-action button, and the horizontal stage stepper with Previous / Next stage. The Overview shows the
 * next action in its own card, so the header leaves the button out there.
 */
function ProjectHeader({
  project,
  onOverview,
  readOnly,
  preset,
}: {
  project: Project
  onOverview: boolean
  readOnly: boolean
  /** FR-74: the preset of the release version on screen. */
  preset: { name: string | null; version: string | null }
}) {
  const state = useProjectState(project.id)
  const user = useCurrentUser()
  const role = effectiveProjectRole(user, project.myRole)
  const pathname = useRouterState({ select: (s) => s.location.pathname })
  const viewing = stageFromPath(pathname)
  // A past release version is read-only (FR-62): no next action is offered.
  const action = state.data && !readOnly ? nextAction(state.data, (c) => canInProject(user, role, c)) : null
  // The page on screen already holds the control the action links to (e.g. "Start execution" on Execution).
  const actionHere =
    action !== null && action.key !== 'advance' && matchesHref(pathname, actionHref(action, project.id), true)

  return (
    <header className="mb-6 flex flex-col gap-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0">
          <h1 className="text-2xl break-words sm:text-3xl">{project.name}</h1>
          <div className="mt-2 flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
            <PhaseBadge phase={project.currentPhase} />
            <StatusBadge status={project.status} />
            <ProjectVersionBadges released={project.releasedVersion} development={project.developmentVersion} />
            <PresetChip name={preset.name} version={preset.version} hideWhenNone />
            <span className="font-mono tabular-nums">{checkpointLabel(project.developmentVersion ?? project.releasedVersion, project.checkpoint)}</span>
          </div>
        </div>
        {!onOverview && action && !actionHere && <NextActionButton projectId={project.id} action={action} className="shrink-0" />}
      </div>
      <div className={cn(panelClass, 'flex flex-col gap-2 p-2 md:flex-row md:items-center md:justify-between')}>
        {state.data ? (
          <StageStepper projectId={project.id} stages={stageStatuses(state.data)} viewing={viewing} />
        ) : (
          <Skeleton className="h-11 w-full max-w-2xl" />
        )}
        <StageNavButtons projectId={project.id} from={viewing ?? state.data?.phase ?? project.currentPhase} />
      </div>
    </header>
  )
}

/**
 * /projects/:id layout: loads the project, shows its header (FR-54) and handles 403/404. The section links live in
 * the sidebar project mode; the live indicator sits in the shell header.
 */
export function ProjectLayout({ projectId, children }: { projectId: string; children: ReactNode }) {
  const project = useProject(projectId)
  const onOverview = Boolean(useMatchRoute()({ to: '/projects/$projectId', params: { projectId } }))
  const view = useSelectedReleaseVersion(projectId, project.data?.releaseVersionId ?? null)
  const pathname = useRouterState({ select: (s) => s.location.pathname })
  // Settings aren't per release version: they stay editable and carry no banner.
  const onSettings = /\/settings(\/|$)/.test(pathname)

  if (project.isPending) {
    return <SkeletonPage />
  }
  if (project.isError) {
    if (isApiError(project.error, 'NOT_FOUND')) {
      return <ProjectNotFound />
    }
    if (isApiError(project.error, 'FORBIDDEN')) {
      return <ForbiddenState>You aren&apos;t a member of this project. Ask a project Owner to add you.</ForbiddenState>
    }
    return <ErrorState error={project.error} onRetry={() => void project.refetch()} title="The project couldn't load" />
  }

  return (
    <ProjectContext value={project.data}>
      <ReleaseViewContext value={onSettings ? currentView : view}>
        <ProjectHeader
          project={project.data}
          onOverview={onOverview}
          readOnly={view.readOnly && !onSettings}
          preset={
            view.readOnly && view.version && !onSettings
              ? { name: view.version.presetName, version: view.version.presetVersion }
              : { name: project.data.presetName, version: project.data.presetVersion }
          }
        />
        {view.readOnly && view.version && !onSettings && <ReadOnlyBanner version={view.version} />}
        {view.resolving ? <SkeletonPage /> : children}
      </ReleaseViewContext>
    </ProjectContext>
  )
}
