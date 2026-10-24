'use client'

import { parseSettingsTab } from '../../../../../features/projects/settings/settings-tabs'
import { Navigate, useParams, useSearch } from '../../../../../lib/navigation'

/** `/settings` → `/settings/general`; v1 `?tab=<section>` links still land on their section. */
export default function SettingsIndexRoute() {
  const { projectId } = useParams<{ projectId: string }>()
  const { tab } = useSearch((search) => ({ tab: parseSettingsTab(search.tab) }))
  return <Navigate to="/projects/$projectId/settings/$section" params={{ projectId, section: tab ?? 'general' }} replace />
}
