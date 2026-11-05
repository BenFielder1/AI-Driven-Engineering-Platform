/**
 * Only same-origin app paths are allowed as post-sign-in destinations (no `//host`, schemes or auth pages). Anything else
 * lands on Home (FR-207, amends the v1–v5 `/projects` landing).
 */
export function safeReturnUrl(value: unknown, fallback = '/home'): string {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return fallback
  }
  if (/^\/(login|register|api)(\/|\?|#|$)/.test(value)) {
    return fallback
  }
  return value
}

/** A notification `link` (a relative app path from the API) when it's a safe same-origin path, else `null`. */
export function safeAppPath(value: unknown): string | null {
  const path = safeReturnUrl(value, '')
  return path === '' ? null : path
}
