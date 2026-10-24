'use client'

import { AdminIntegrationsPage } from '../../../../features/admin/admin-integrations-page'
import { useSearch } from '../../../../lib/navigation'

/** FR-80: the manifest callback redirects here with `?github=configured` or `?error=GITHUB_AUTH_FAILED`. */
interface IntegrationsSearch {
  github?: 'configured'
  error?: 'GITHUB_AUTH_FAILED'
}

function parseIntegrationsSearch(search: Record<string, unknown>): IntegrationsSearch {
  return {
    github: search.github === 'configured' ? 'configured' : undefined,
    error: search.error === 'GITHUB_AUTH_FAILED' ? 'GITHUB_AUTH_FAILED' : undefined,
  }
}

export default function IntegrationsRoute() {
  const { github, error } = useSearch(parseIntegrationsSearch)
  return (
    <AdminIntegrationsPage
      callback={error === 'GITHUB_AUTH_FAILED' ? 'failed' : github === 'configured' ? 'configured' : undefined}
    />
  )
}
