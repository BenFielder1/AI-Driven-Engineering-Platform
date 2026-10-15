import { createFileRoute } from '@tanstack/react-router'

import { ProjectSettingsPage } from '../../../../../features/projects/settings/project-settings-page'
import { parseSettingsTab } from '../../../../../features/projects/settings/settings-tabs'

/** /projects/:id/settings/{general,members,git,llm,notifications,danger} (SPEC §7.2). Unknown sections go to General. */
export const Route = createFileRoute('/_app/projects/$projectId/settings/$section')({
  component: SettingsSectionRoute,
})

function SettingsSectionRoute() {
  const { section } = Route.useParams()
  return <ProjectSettingsPage section={parseSettingsTab(section)} />
}
