import type { Schemas } from '../api/client'

/**
 * How the review and release UI names a project's Git host and its change request (FR-138, FR-140). GitHub and
 * Bitbucket have pull requests; GitLab has merge requests and its own Releases. `release` is the host's release object
 * ("GitHub Release", "GitLab Release"), or `null` when the host has none (Bitbucket: an annotated tag, D-V4-7).
 * Unknown (no pull request recorded yet) and Local read as GitHub, the v1–v3 copy.
 */
export interface GitHostTerms {
  /** "GitHub", "GitLab" or "Bitbucket". */
  host: string
  /** "pull request" or "merge request" (lower case, mid-sentence). */
  pr: string
  /** "Pull request" or "Merge request" (sentence start, labels). */
  prTitle: string
  /** "PR" or "MR". */
  prShort: string
  /** "GitHub Release", "GitLab Release", or null. */
  release: string | null
}

const github: GitHostTerms = { host: 'GitHub', pr: 'pull request', prTitle: 'Pull request', prShort: 'PR', release: 'GitHub Release' }

const termsByProvider: Record<Schemas['GitProvider'], GitHostTerms> = {
  github_app: github,
  github_pat: github,
  local: github,
  gitlab: { host: 'GitLab', pr: 'merge request', prTitle: 'Merge request', prShort: 'MR', release: 'GitLab Release' },
  bitbucket: { host: 'Bitbucket', pr: 'pull request', prTitle: 'Pull request', prShort: 'PR', release: null },
}

/** FR-140: host-neutral terms, for copy shown before the project's Git host is known (or when it can't be loaded). */
export const unknownGitHost: GitHostTerms = {
  host: 'the Git host',
  pr: 'pull or merge request',
  prTitle: 'Pull or merge request',
  prShort: 'PR/MR',
  release: null,
}

export function gitHostTerms(provider: Schemas['GitProvider'] | null | undefined): GitHostTerms {
  return provider ? termsByProvider[provider] : github
}

/** The terms for a free-form `provider` value (e.g. an activity payload); unknown or missing reads as GitHub. */
export function gitHostTermsOf(value: unknown): GitHostTerms {
  return typeof value === 'string' && Object.hasOwn(termsByProvider, value)
    ? termsByProvider[value as Schemas['GitProvider']]
    : github
}

/**
 * Fills `{host}`, `{pr}`, `{Pr}` (sentence start), `{PR}` (the short form) and `{release}` (the host release, or
 * "release tag" without one) in host-aware copy.
 */
export function withGitHost(copy: string, terms: GitHostTerms = github): string {
  return copy
    .replaceAll('{host}', terms.host)
    .replaceAll('{pr}', terms.pr)
    .replaceAll('{Pr}', terms.prTitle)
    .replaceAll('{PR}', terms.prShort)
    .replaceAll('{release}', terms.release ?? 'release tag')
}
