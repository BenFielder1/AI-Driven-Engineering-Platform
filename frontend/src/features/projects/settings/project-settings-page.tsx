import { Link, Navigate } from '../../../lib/navigation'
import type { ReactNode } from 'react'

import { panelClass } from '../../../components/shared/panel-class'
import { ErrorState, LoadingState } from '../../../components/ui/states'
import { canInProject } from '../../../lib/permissions'
import { useDocumentTitle } from '../../../lib/use-document-title'
import { cn } from '../../../lib/utils'
import { useProjectAccess } from '../project-context'
import { useProjectSettings, type ProjectSettings } from '../projects-api'
import { PresetSettingsSection } from '../../presets/preset-settings'
import { DeploymentSettingsTab } from '../../deployments/deployment-settings-tab'
import { DangerZoneTab } from './danger-zone-tab'
import { GeneralForm, GeneralReadOnly } from './general-tab'
import { GitTab } from './git-tab'
import { LlmTab } from './llm-tab'
import { MembersTab } from './members-tab'
import { NotificationsTab } from './notifications-tab'
import { settingsTabLabels, settingsTabs, type SettingsTab } from './settings-tabs'

/**
 * The settings secondary nav (Space Worm settings pattern, SPEC §7.2): one list of links that is a vertical left nav
 * from `lg` and a scrolling row of tabs below it. The current section is `aria-current="page"`.
 */
function SettingsNav({
  projectId,
  sections,
  selected,
}: {
  projectId: string
  sections: readonly SettingsTab[]
  selected: SettingsTab
}) {
  return (
    <nav aria-label="Settings sections" className="min-w-0 lg:w-52 lg:shrink-0">
      <ul className="flex gap-1 overflow-x-auto border-b border-border/60 lg:flex-col lg:border-b-0">
        {sections.map((id) => {
          const active = id === selected
          return (
            <li key={id} className="shrink-0">
              <Link
                to="/projects/$projectId/settings/$section"
                params={{ projectId, section: id }}
                aria-current={active ? 'page' : undefined}
                className={cn(
                  'relative flex min-h-11 items-center rounded-t-lg px-3 text-sm font-medium whitespace-nowrap transition-colors lg:rounded-lg',
                  'outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                  active
                    ? 'border-b-2 border-primary text-foreground lg:border-b-0 lg:bg-sidebar-accent'
                    : 'border-b-2 border-transparent text-muted-foreground hover:text-foreground lg:border-b-0 lg:hover:bg-accent/60',
                )}
              >
                {active && (
                  <span
                    aria-hidden
                    className="absolute top-1/2 left-1 hidden size-1.5 -translate-y-1/2 rounded-full bg-primary lg:block"
                  />
                )}
                <span className="lg:pl-2">{settingsTabLabels[id]}</span>
              </Link>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}

/**
 * /projects/:id/settings/{section} (FR-7–FR-10). Owners (and Admins) see every section; other members see General
 * read-only, Members, LLM (FR-79) and Deployment (FR-88) read-only, and any other section sends them to General. The settings GET is Owner-only, so it isn't
 * requested for anyone else.
 */
export function ProjectSettingsPage({ section }: { section: SettingsTab | undefined }) {
  const { project, user, role } = useProjectAccess()
  useDocumentTitle(`Settings · ${project.name}`)
  const isOwner = canInProject(user, role, 'changeSettings')
  const settings = useProjectSettings(project.id, isOwner)

  // FR-79, FR-88: every member reads Settings → LLM and Deployment ("connected by <name>"); their GETs aren't Owner-only.
  const visible: readonly SettingsTab[] = isOwner ? settingsTabs : ['general', 'members', 'llm', 'deployment']
  if (!section || !visible.includes(section)) {
    return (
      <Navigate to="/projects/$projectId/settings/$section" params={{ projectId: project.id, section: 'general' }} replace />
    )
  }

  const withSettings = (render: (s: ProjectSettings) => ReactNode) => {
    if (settings.isPending) {
      return <LoadingState label="Loading settings…" />
    }
    if (settings.isError) {
      return <ErrorState error={settings.error} onRetry={() => void settings.refetch()} title="The settings couldn't load" />
    }
    return render(settings.data)
  }

  let panel: ReactNode
  switch (section) {
    case 'general':
      panel = (
        <>
          {isOwner ? <GeneralForm project={project} /> : <GeneralReadOnly project={project} />}
          <PresetSettingsSection project={project} canChange={canInProject(user, role, 'changePreset')} />
        </>
      )
      break
    case 'members':
      panel = <MembersTab />
      break
    case 'git':
      panel = withSettings((s) => <GitTab projectId={project.id} git={s.git} />)
      break
    case 'llm':
      panel = <LlmTab projectId={project.id} canEdit={isOwner} />
      break
    case 'deployment':
      panel = <DeploymentSettingsTab project={project} canEdit={isOwner} />
      break
    case 'notifications':
      panel = withSettings((s) => <NotificationsTab projectId={project.id} notifications={s.notifications} />)
      break
    case 'danger':
      panel = <DangerZoneTab project={project} />
      break
  }

  return (
    <div className="flex flex-col gap-5 lg:flex-row lg:items-start lg:gap-8">
      <SettingsNav projectId={project.id} sections={visible} selected={section} />
      <div className={cn(panelClass, 'min-w-0 flex-1 p-4 sm:p-6')}>
        {panel}
      </div>
    </div>
  )
}
