import { createFileRoute, redirect } from '@tanstack/react-router'

import { parseSettingsTab } from '../../../../../features/projects/settings/settings-tabs'

/** `/settings` → `/settings/general`; v1 `?tab=<section>` links still land on their section. */
export const Route = createFileRoute('/_app/projects/$projectId/settings/')({
  validateSearch: (search: Record<string, unknown>): { tab?: string } => ({
    tab: parseSettingsTab(search.tab),
  }),
  beforeLoad: ({ params, search }) => {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- TanStack Router redirects are thrown by design.
    throw redirect({
      to: '/projects/$projectId/settings/$section',
      params: { projectId: params.projectId, section: parseSettingsTab(search.tab) ?? 'general' },
      replace: true,
    })
  },
})
