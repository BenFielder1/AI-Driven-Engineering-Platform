import { useQuery } from '@tanstack/react-query'

import { releaseVersionsQueryOptions, type ReleaseVersion } from '../versions/versions-api'

export interface VersionAnalysisFlags {
  /** FR-182: the version starts from existing code (a base commit), so the Analysis page is in the project nav. */
  hasCode: boolean
  /** FR-178, §4.27: the version infers its stack, so the lifecycle tracker shows Analysis. */
  infers: boolean
}

/** What a release version means for the Analysis page and the lifecycle tracker. */
export function versionAnalysisFlags(version: Pick<ReleaseVersion, 'baseCommitSha' | 'stackSource'> | undefined): VersionAnalysisFlags {
  const infers = version?.stackSource === 'infer'
  return { hasCode: infers || Boolean(version?.baseCommitSha), infers }
}

/** The flags of the project's current release version (GET /versions, shared cache with the version switcher). */
export function useCurrentVersionFlags(projectId: string | undefined, releaseVersionId: string | null | undefined): VersionAnalysisFlags {
  const versions = useQuery({ ...releaseVersionsQueryOptions(projectId ?? ''), enabled: projectId !== undefined && Boolean(releaseVersionId) })
  return versionAnalysisFlags(versions.data?.find((v) => v.id === releaseVersionId))
}
