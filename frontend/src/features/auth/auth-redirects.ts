import { stringifySearch, type RawSearch } from '../../lib/navigation'
import { safeReturnUrl } from '../../lib/return-url'

/** FR-97: where a signed-out visitor to an app URL goes: `/login?returnUrl=<path+search>`. */
export function loginRedirectHref(returnUrl: string): string {
  return `/login${stringifySearch({ returnUrl })}`
}

/** FR-97 (v2 `_public` guard): where a signed-in user on /login or /register goes. */
export function publicRedirectTarget(search: RawSearch): string {
  // A GitHub *link* failure redirects a signed-in user to /login?error=…: show it on the account page instead.
  if (search.error === 'GITHUB_AUTH_FAILED') {
    return `/account${stringifySearch({ error: 'GITHUB_AUTH_FAILED', reason: search.reason })}`
  }
  return safeReturnUrl(search.returnUrl)
}
