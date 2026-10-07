/** Only same-origin app paths are allowed as post-sign-in destinations (no `//host`, schemes or auth pages). */
export function safeReturnUrl(value: unknown, fallback = '/projects'): string {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return fallback
  }
  if (/^\/(login|register|api)(\/|\?|#|$)/.test(value)) {
    return fallback
  }
  return value
}
