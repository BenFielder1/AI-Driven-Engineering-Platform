import { stringifySearch, type RawSearch } from '../../lib/navigation'
import { safeReturnUrl } from '../../lib/return-url'

/** FR-97: where a signed-out visitor to an app URL goes: `/login?returnUrl=<path+search>`. */
export function loginRedirectHref(returnUrl: string): string {
  return `/login${stringifySearch({ returnUrl })}`
}

/**
 * FR-97 (v2 `_public` guard): where a signed-in user on /login or /register goes, or `null` to stay. A GitLab or
 * Bitbucket sign-in waiting to be linked (`?link=gitlab|bitbucket`, FR-131, FR-134) stays: the signed-in owner of the
 * matching account confirms it there.
 */
export function publicRedirectTarget(search: RawSearch): string | null {
  if (search.link === 'gitlab' || search.link === 'bitbucket') {
    return null
  }
  // A GitHub *link* failure redirects a signed-in user to /login?error=…: show it on the account page instead.
  if (search.error === 'GITHUB_AUTH_FAILED') {
    return `/account${stringifySearch({ error: 'GITHUB_AUTH_FAILED', reason: search.reason })}`
  }
  // FR-131, FR-134: a GitLab or Bitbucket failure while signed in belongs with that host's connections.
  if (search.error === 'GIT_AUTH_FAILED' && (search.provider === 'gitlab' || search.provider === 'bitbucket')) {
    return `/connections${stringifySearch({ error: 'GIT_AUTH_FAILED', reason: search.reason, provider: search.provider })}`
  }
  return safeReturnUrl(search.returnUrl)
}
