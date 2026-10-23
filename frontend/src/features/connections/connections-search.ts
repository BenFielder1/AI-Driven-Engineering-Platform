/**
 * Search params of /connections and its /account/connections alias (FR-81): Connect GitHub comes back with
 * `?error=GITHUB_AUTH_FAILED&reason=…`, and the GitHub App's setup URL with `?github=installed`.
 */
export interface ConnectionsSearch {
  github?: 'installed'
  error?: string
  reason?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export function parseConnectionsSearch(search: Record<string, unknown>): ConnectionsSearch {
  return {
    github: search.github === 'installed' ? 'installed' : undefined,
    error: str(search.error),
    reason: str(search.reason),
  }
}
