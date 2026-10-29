import type { Schemas } from '../../api/client'
import { compareSemVer, normalizeVersion, parseSemVer } from '../versions/semver'
import { NEVER_RELEASED } from './schemas'

/** FR-73, FR-141: a repository's Releases and tags, as GitHub (`GitHubRepoRefsDto`) and GitLab (`GitLabRepoRefsDto`) list them. */
export type ReleaseRefs = Pick<Schemas['GitHubRepoRefsDto'] | Schemas['GitLabRepoRefsDto'], 'releases' | 'tags'>
type Refs = ReleaseRefs

/** FR-73, FR-141: one choice of "Currently released version": a GitHub or GitLab Release, or a semver tag. */
export interface ReleasedOption {
  tag: string
  /** The tag's semantic version (normalised, no leading `v`). */
  version: string
  kind: 'release' | 'tag'
  /** The host's latest release. */
  isLatest: boolean
  name: string | null
  publishedAt: string | null
}

/** The semver of a tag: the API's `semver` for a known tag (tag prefixes stripped), else the tag parsed as a version. */
export function semverOfTag(tag: string, refs: Refs | undefined): string | null {
  const t = tag.trim()
  if (t === '' || t === NEVER_RELEASED) {
    return null
  }
  const known = refs?.tags.find((x) => x.name === t)
  return normalizeVersion(known ? known.semver : t)
}

const byVersionDesc = (a: ReleasedOption, b: ReleasedOption) => {
  const pa = parseSemVer(a.version)
  const pb = parseSemVer(b.version)
  return pa && pb ? compareSemVer(pb, pa) : 0
}

/**
 * FR-73: the repository's GitHub Releases, then the semver tags no release uses, each highest first. Releases and tags
 * that aren't semantic versions are left out: they can't be the released version (the API answers VERSION_INVALID).
 */
export function releasedOptions(refs: Refs): ReleasedOption[] {
  const releases = refs.releases.flatMap((r): ReleasedOption[] => {
    const version = semverOfTag(r.tagName, refs)
    return version
      ? [{ tag: r.tagName, version, kind: 'release', isLatest: r.isLatest, name: r.name, publishedAt: r.publishedAt }]
      : []
  })
  const releaseTags = new Set(releases.map((r) => r.tag))
  const tags = refs.tags.flatMap((t): ReleasedOption[] => {
    const version = t.semver ? normalizeVersion(t.semver) : null
    return version && !releaseTags.has(t.name)
      ? [{ tag: t.name, version, kind: 'tag', isLatest: false, name: null, publishedAt: null }]
      : []
  })
  return [...releases.sort(byVersionDesc), ...tags.sort(byVersionDesc)]
}

/** FR-73: the preselected choice: the host's latest release, else the highest release, else the highest tag, else never. */
export function defaultReleasedTag(options: readonly ReleasedOption[]): string {
  return (
    options.find((o) => o.isLatest)?.tag ??
    options.find((o) => o.kind === 'release')?.tag ??
    options[0]?.tag ??
    NEVER_RELEASED
  )
}
