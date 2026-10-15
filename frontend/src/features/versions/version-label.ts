import { useQuery } from '@tanstack/react-query'

import { releaseVersionsQueryOptions } from './versions-api'

/** FR-63: "1.2.0 · checkpoint 4", or "Checkpoint 4" when the release version isn't known. */
export function checkpointLabel(version: string | null | undefined, checkpoint: number): string {
  return version ? `${version} · checkpoint ${String(checkpoint)}` : `Checkpoint ${String(checkpoint)}`
}

/**
 * The semver of a release version id, from the cached GET /versions (SPEC §7.2: every list and detail shows which
 * release version it belongs to). Returns `undefined` until the versions load or for an unknown id.
 */
export function useReleaseVersionNames(projectId: string): (releaseVersionId: string | null | undefined) => string | undefined {
  const versions = useQuery(releaseVersionsQueryOptions(projectId))
  const data = versions.data
  return (releaseVersionId) => (releaseVersionId ? data?.find((v) => v.id === releaseVersionId)?.version : undefined)
}
