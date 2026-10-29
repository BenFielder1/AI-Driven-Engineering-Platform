import { stringifySearch, type RawSearch } from '../../lib/navigation'
import { safeReturnUrl } from '../../lib/return-url'

/** FR-97: where a signed-out visitor to an app URL goes: `/login?returnUrl=<path+search>`. */
export function loginRedirectHref(returnUrl: string): string {
  return `/login${stringifySearch({ returnUrl })}`
}

/**
 * FR-97 (v2 `_public` guard): where a signed-in user on /login or /register goes, or `null` to stay. A GitLab sign-in
 * waiting to be linked (`?link=gitlab`, FR-131) stays: the signed-in owner of the matching account confirms it there.
 */
export function publicRedirectTarget(search: RawSearch): string | null {
  if (search.link === 'gitlab') {
    return null
  }
  // A GitHub *link* failure redirects a signed-in user to /login?error=…: show it on the account page instead.
  if (search.error === 'GITHUB_AUTH_FAILED') {
    return `/account${stringifySearch({ error: 'GITHUB_AUTH_FAILED', reason: search.reason })}`
  }
  // FR-131: a GitLab failure while signed in belongs with the GitLab connections.
  if (search.error === 'GIT_AUTH_FAILED' && search.provider === 'gitlab') {
    return `/connections${stringifySearch({ error: 'GIT_AUTH_FAILED', reason: search.reason, provider: 'gitlab' })}`
  }
  return safeReturnUrl(search.returnUrl)
}
