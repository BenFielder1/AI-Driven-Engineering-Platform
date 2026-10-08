import { createFileRoute } from '@tanstack/react-router'

import { ProjectSettingsPage } from '../../../../features/projects/settings/project-settings-page'
import { settingsTabs, type SettingsTab } from '../../../../features/projects/settings/settings-tabs'

interface SettingsSearch {
  tab?: SettingsTab
}

export const Route = createFileRoute('/_app/projects/$projectId/settings')({
  validateSearch: (search: Record<string, unknown>): SettingsSearch => ({
    tab: settingsTabs.find((t) => t === search.tab),
  }),
  component: SettingsRoute,
})

function SettingsRoute() {
  const { tab } = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <ProjectSettingsPage
      tab={tab}
      onTabChange={(next) => {
        void navigate({ search: { tab: next === 'general' ? undefined : next }, replace: true })
      }}
    />
  )
}
