import { useQuery } from '@tanstack/react-query'

import { useNavigate, useSearch } from '../../lib/navigation'
import { releaseVersionsQueryOptions, type ReleaseVersion } from './versions-api'
import { normalizeVersion } from './semver'

/** The search param that picks a past release version on project pages (FR-62), e.g. `?release=1.1.0`. */
export const RELEASE_SEARCH_PARAM = 'release'

/** Parses the `?release=` value: a normalised semver, or undefined. */
export function parseReleaseSearch(value: unknown): string | undefined {
  return typeof value === 'string' ? (normalizeVersion(value) ?? undefined) : undefined
}

export interface ReleaseView {
  /** The release version on screen: the one picked with `?release=`, else the project's current one (once loaded). */
  version: ReleaseVersion | undefined
  /** A version other than the project's current one is on screen: every project page renders read-only. */
  readOnly: boolean
  /**
   * The `releaseVersionId` for history queries: the picked past version's id, or `undefined` for the current one (the
   * API's default: the version in development, else the latest released).
   */
  releaseVersionId: string | undefined
  /** `?release=` is set but the versions haven't loaded yet: pages wait rather than flash the current version. */
  resolving: boolean
  /** All release versions, highest first (empty until loaded). */
  versions: ReleaseVersion[]
}

/**
 * FR-62: the release version the project pages show. One place decides it: the `?release=<semver>` search param (kept
 * across project navigation by the project route), matched against GET /versions. A value the project doesn't have,
 * or the current version itself, means the current version (editable).
 */
export function useSelectedReleaseVersion(projectId: string, currentReleaseVersionId: string | null): ReleaseView {
  const search: Record<string, unknown> = useSearch()
  const requested = parseReleaseSearch(search[RELEASE_SEARCH_PARAM])
  const versions = useQuery(releaseVersionsQueryOptions(projectId))
  const list = versions.data ?? []
  const current = list.find((v) => v.id === currentReleaseVersionId)
  const picked = requested ? list.find((v) => normalizeVersion(v.version) === requested) : undefined
  const readOnly = picked !== undefined && picked.id !== currentReleaseVersionId
  return {
    version: picked ?? current,
    readOnly,
    releaseVersionId: readOnly ? picked.id : undefined,
    resolving: requested !== undefined && versions.isPending,
    versions: list,
  }
}

/** Shows a release version on the current project page: `?release=<semver>`, or the current version for `undefined`. */
export function useShowReleaseVersion(): (version: string | undefined) => void {
  const navigate = useNavigate()
  return (version) => {
    void navigate({ to: '.', search: (prev) => ({ ...prev, [RELEASE_SEARCH_PARAM]: version }) })
  }
}
