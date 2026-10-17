import type { ProjectState } from '../projects-api'

/**
 * §4.10, FR-64: the API's tag prefix rule (mirrors ReleaseVersionRules.IsValidTagPrefix): `^[A-Za-z0-9._/-]{0,20}$`,
 * not starting with `.`, `/` or `-`, and no `..`, `//`, `/.` or `.lock/` (the tag must stay a safe Git ref name).
 */
export const TAG_PREFIX_MAX = 20
const tagPrefixPattern = /^[A-Za-z0-9._/-]*$/

/** The tag prefix's problem, or `null` when it's valid (empty is valid: bare `1.2.0` tags). */
export function checkTagPrefix(value: string): string | null {
  if (value.length > TAG_PREFIX_MAX) {
    return `Use ${String(TAG_PREFIX_MAX)} characters or fewer.`
  }
  if (!tagPrefixPattern.test(value)) {
    return 'Use only letters, digits, ".", "_", "/" or "-".'
  }
  if (/^[./-]/.test(value)) {
    return 'The prefix can’t start with ".", "/" or "-".'
  }
  const sequence = ['..', '//', '/.', '.lock/'].find((bad) => value.includes(bad))
  if (sequence) {
    return `The prefix can’t contain "${sequence}" (it must make a valid Git tag name).`
  }
  return null
}

/** Why the tag prefix can't change now (409 CONFLICT server-side), or `null`. */
export function tagPrefixLockReason(state: Pick<ProjectState, 'status' | 'latestRelease'> | undefined): string | null {
  if (state?.status === 'awaiting_merge' || state?.latestRelease?.status === 'awaiting_merge') {
    return 'The tag prefix can’t change while the release is awaiting its merge on GitHub.'
  }
  if (state?.latestRelease?.status === 'in_progress') {
    return 'The tag prefix can’t change while a release is in progress.'
  }
  return null
}
