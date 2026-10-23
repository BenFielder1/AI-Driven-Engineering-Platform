'use client'

import { ProjectSettingsPage } from '../../../../../../features/projects/settings/project-settings-page'
import { parseSettingsTab } from '../../../../../../features/projects/settings/settings-tabs'
import { useParams } from '../../../../../../lib/navigation'

/** /projects/:id/settings/{general,members,git,llm,deployment,notifications,danger} (SPEC §7.2). Unknown sections go to General. */
export default function SettingsSectionRoute() {
  const { section } = useParams<{ section: string }>()
  return <ProjectSettingsPage section={parseSettingsTab(section)} />
}
