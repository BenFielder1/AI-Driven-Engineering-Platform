/**
 * /login search params (v2 route validateSearch). FR-131: GitLab sign-in failures add `provider=gitlab` to
 * `error=GIT_AUTH_FAILED&reason=…`, and a GitLab sign-in waiting to be linked to an existing account comes back with
 * `link=gitlab` (FR-134: `provider=bitbucket` and `link=bitbucket` for Bitbucket).
 */
export interface LoginSearch {
  returnUrl?: string
  error?: string
  reason?: string
  provider?: string
  link?: GitHostLinkProvider
}

/** The Git hosts whose sign-in can wait to be linked to an existing account (FR-131, FR-134). */
export type GitHostLinkProvider = 'gitlab' | 'bitbucket'

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export function parseLoginSearch(search: Record<string, unknown>): LoginSearch {
  return {
    returnUrl: str(search.returnUrl),
    error: str(search.error),
    reason: str(search.reason),
    provider: str(search.provider),
    link: search.link === 'gitlab' || search.link === 'bitbucket' ? search.link : undefined,
  }
}

/** /register search params. */
export function parseRegisterSearch(search: Record<string, unknown>): { returnUrl?: string } {
  return { returnUrl: str(search.returnUrl) }
}
