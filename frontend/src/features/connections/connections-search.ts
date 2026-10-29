/**
 * Search params of /connections and its /account/connections alias: Connect GitHub comes back with
 * `?error=GITHUB_AUTH_FAILED&reason=…` (FR-81), the GitHub App's setup URL with `?github=installed`, and Connect GitLab
 * with `?gitlab=connected` or `?error=GIT_AUTH_FAILED&reason=…&provider=gitlab` (FR-131).
 */
export interface ConnectionsSearch {
  github?: 'installed'
  gitlab?: 'connected'
  error?: string
  reason?: string
  provider?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export function parseConnectionsSearch(search: Record<string, unknown>): ConnectionsSearch {
  return {
    github: search.github === 'installed' ? 'installed' : undefined,
    gitlab: search.gitlab === 'connected' ? 'connected' : undefined,
    error: str(search.error),
    reason: str(search.reason),
    provider: str(search.provider),
  }
}
