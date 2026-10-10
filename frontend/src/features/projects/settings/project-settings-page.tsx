import type { ReactNode } from 'react'

import { ErrorState, LoadingState } from '../../../components/ui/states'
import { Tabs, type TabItem } from '../../../components/ui/tabs'
import { canInProject } from '../../../lib/permissions'
import { useDocumentTitle } from '../../../lib/use-document-title'
import { useProjectAccess } from '../project-context'
import { useProjectSettings, type ProjectSettings } from '../projects-api'
import { DangerZoneTab } from './danger-zone-tab'
import { GeneralForm, GeneralReadOnly } from './general-tab'
import { GitTab } from './git-tab'
import { LlmTab } from './llm-tab'
import { MembersTab } from './members-tab'
import { NotificationsTab } from './notifications-tab'
import { settingsTabs, type SettingsTab } from './settings-tabs'

const tabLabels: Record<SettingsTab, string> = {
  general: 'General',
  members: 'Members',
  git: 'Git',
  llm: 'LLM',
  notifications: 'Notifications',
  danger: 'Danger zone',
}

interface ProjectSettingsPageProps {
  tab: SettingsTab | undefined
  onTabChange: (tab: SettingsTab) => void
}

/**
 * /projects/:id/settings (FR-7–FR-10). Owners (and Admins) see every tab; other members see General read-only and
 * Members. The settings GET is Owner-only, so it isn't requested for anyone else.
 */
export function ProjectSettingsPage({ tab, onTabChange }: ProjectSettingsPageProps) {
  const { project, user, role } = useProjectAccess()
  useDocumentTitle(`Settings · ${project.name}`)
  const isOwner = canInProject(user, role, 'changeSettings')
  const settings = useProjectSettings(project.id, isOwner)

  const visible: readonly SettingsTab[] = isOwner ? settingsTabs : ['general', 'members']
  const selected: SettingsTab = tab && visible.includes(tab) ? tab : 'general'
  const items: TabItem<SettingsTab>[] = visible.map((id) => ({ id, label: tabLabels[id] }))

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
  switch (selected) {
    case 'general':
      panel = isOwner ? <GeneralForm project={project} /> : <GeneralReadOnly project={project} />
      break
    case 'members':
      panel = <MembersTab />
      break
    case 'git':
      panel = withSettings((s) => <GitTab projectId={project.id} git={s.git} />)
      break
    case 'llm':
      panel = withSettings((s) => <LlmTab projectId={project.id} llm={s.llm} />)
      break
    case 'notifications':
      panel = withSettings((s) => <NotificationsTab projectId={project.id} notifications={s.notifications} />)
      break
    case 'danger':
      panel = <DangerZoneTab project={project} />
      break
  }

  return (
    <Tabs label="Settings sections" tabs={items} selected={selected} onSelect={onTabChange}>
      {panel}
    </Tabs>
  )
}
