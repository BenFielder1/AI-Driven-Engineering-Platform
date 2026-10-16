import { createFileRoute } from '@tanstack/react-router'

import { AdminIntegrationsPage } from '../../../features/admin/admin-integrations-page'

/** FR-80: the manifest callback redirects here with `?github=configured` or `?error=GITHUB_AUTH_FAILED`. */
export interface IntegrationsSearch {
  github?: 'configured'
  error?: 'GITHUB_AUTH_FAILED'
}

export const Route = createFileRoute('/_app/admin/integrations')({
  validateSearch: (search: Record<string, unknown>): IntegrationsSearch => ({
    github: search.github === 'configured' ? 'configured' : undefined,
    error: search.error === 'GITHUB_AUTH_FAILED' ? 'GITHUB_AUTH_FAILED' : undefined,
  }),
  component: IntegrationsRoute,
})

function IntegrationsRoute() {
  const { github, error } = Route.useSearch()
  return (
    <AdminIntegrationsPage
      callback={error === 'GITHUB_AUTH_FAILED' ? 'failed' : github === 'configured' ? 'configured' : undefined}
    />
  )
}
