import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { invalidateProject } from '../lifecycle/lifecycle-api'
import { projectKeys, unwrap } from '../projects/projects-api'

export type Artifact = Schemas['ArtifactDto']
export type ArtifactSummary = Schemas['ArtifactSummaryDto']
export type ArtifactDiff = Schemas['ArtifactDiffDto']
export type DiffLine = Schemas['DiffLineDto']
export type ArtifactType = Schemas['ArtifactType']
export type ArtifactStatus = NonNullable<Schemas['ArtifactStatus']>
export type ArtifactAuthor = Schemas['ArtifactAuthorDto']

/** Query keys, all under `projectKeys.artifacts(id)` so one invalidation refreshes every artifact view. */
export const artifactKeys = {
  all: (id: string) => projectKeys.artifacts(id),
  list: (id: string, releaseVersionId?: string) => [...projectKeys.artifacts(id), 'list', releaseVersionId ?? 'current'] as const,
  type: (id: string, type: ArtifactType) => [...projectKeys.artifacts(id), 'type', type] as const,
  latest: (id: string, type: ArtifactType, releaseVersionId?: string) =>
    [...artifactKeys.type(id, type), 'latest', releaseVersionId ?? 'current'] as const,
  history: (id: string, type: ArtifactType, releaseVersionId?: string) =>
    [...artifactKeys.type(id, type), 'history', releaseVersionId ?? 'current'] as const,
  allHistory: (id: string, type: ArtifactType) => [...artifactKeys.type(id, type), 'all-history'] as const,
  version: (id: string, type: ArtifactType, version: number) =>
    [...artifactKeys.type(id, type), 'version', version] as const,
  diff: (id: string, type: ArtifactType, from: number, to: number) =>
    [...artifactKeys.type(id, type), 'diff', from, to] as const,
}

const versionQuery = (releaseVersionId: string | undefined) => (releaseVersionId ? { releaseVersionId } : {})

/**
 * FR-21, FR-59: the latest version of each type that has one (types with no versions are absent), in a release
 * version (default: the current one; FR-62 passes a past one).
 */
export function useArtifacts(id: string, releaseVersionId?: string) {
  return useQuery({
    queryKey: artifactKeys.list(id, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/artifacts', {
          params: { path: { id }, query: versionQuery(releaseVersionId) },
          signal,
        }),
      ),
  })
}

/** FR-21: every version of a type (summaries, no content), newest first. */
export function useArtifactHistory(id: string, type: ArtifactType, releaseVersionId?: string) {
  return useQuery({
    queryKey: artifactKeys.history(id, type, releaseVersionId),
    queryFn: async ({ signal }) => {
      const versions = unwrap(
        await api.GET('/api/projects/{id}/artifacts/{type}/history', {
          params: { path: { id, type }, query: versionQuery(releaseVersionId) },
          signal,
        }),
      )
      return [...versions].sort((a, b) => b.version - a.version)
    },
  })
}

/**
 * FR-59: every version of a type across all release versions, newest first, each with its `releaseVersion` (for the
 * cross-version diff picker).
 */
export function useAllArtifactHistory(id: string, type: ArtifactType) {
  return useQuery({
    queryKey: artifactKeys.allHistory(id, type),
    queryFn: async ({ signal }) => {
      const versions = unwrap(
        await api.GET('/api/projects/{id}/artifacts/{type}/history', {
          params: { path: { id, type }, query: { allReleaseVersions: true } },
          signal,
        }),
      )
      return [...versions].sort((a, b) => b.version - a.version)
    },
  })
}

/** FR-16/FR-20: the latest version with its content (integrity re-checked by the server on read). */
export function useLatestArtifact(id: string, type: ArtifactType, enabled = true, releaseVersionId?: string) {
  return useQuery({
    queryKey: artifactKeys.latest(id, type, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/artifacts/{type}/latest', {
          params: { path: { id, type }, query: versionQuery(releaseVersionId) },
          signal,
        }),
      ),
    enabled,
  })
}

/** FR-16/FR-20: one version with its content. */
export function useArtifactVersion(id: string, type: ArtifactType, version: number | null) {
  return useQuery({
    queryKey: artifactKeys.version(id, type, version ?? 0),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/artifacts/{type}/versions/{version}', {
          params: { path: { id, type, version: version ?? 0 } },
          signal,
        }),
      ),
    enabled: version !== null,
  })
}

/** FR-21: the line diff between two versions (`null` until the user picks a pair). */
export function useArtifactDiff(id: string, type: ArtifactType, pair: { from: number; to: number } | null) {
  return useQuery({
    queryKey: artifactKeys.diff(id, type, pair?.from ?? 0, pair?.to ?? 0),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/artifacts/{type}/diff', {
          params: { path: { id, type }, query: { from: pair?.from ?? 0, to: pair?.to ?? 0 } },
          signal,
        }),
      ),
    enabled: pair !== null,
    placeholderData: keepPreviousData,
  })
}

/**
 * FR-18: save edited Markdown as a new draft (writers). `baseVersion` is the version the edit started from: the
 * server answers 409 CONFLICT (details `latestVersion: N`) when a newer version was saved meanwhile. Refreshes the whole project: the previous draft becomes
 * `superseded` and the overview's artifact summary and activity change too.
 */
export function useCreateArtifactDraft(id: string, type: ArtifactType) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ content, baseVersion }: { content: string; baseVersion: number | null }) =>
      unwrap(
        await api.POST('/api/projects/{id}/artifacts/{type}', {
          params: { path: { id, type } },
          body: { content, baseVersion },
        }),
      ),
    onSettled: () => invalidateProject(queryClient, id),
  })
}

/**
 * FR-58: restore an older version as a new draft (writers): same content and hash, parent = the latest version,
 * `restoredFromVersion` = the chosen one. Refreshes the whole project either way (a 409 means our view is stale).
 */
export function useRestoreArtifact(id: string, type: ArtifactType) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (version: number) =>
      unwrap(
        await api.POST('/api/projects/{id}/artifacts/{type}/versions/{version}/restore', {
          params: { path: { id, type, version } },
        }),
      ),
    onSettled: () => invalidateProject(queryClient, id),
  })
}

interface DecideVariables {
  artifactId: string
  comment: string | null
}

/**
 * FR-17/FR-19: approve a draft (Owners). Approval can invalidate downstream artifacts and move the phase back
 * (§4.4), so success and failure (ARTIFACT_NOT_DRAFT: our view is stale) both refresh the whole project.
 */
export function useApproveArtifact(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ artifactId, comment }: DecideVariables) =>
      unwrap(
        await api.POST('/api/projects/{id}/artifacts/{artifactId}/approve', {
          params: { path: { id, artifactId } },
          body: { comment },
        }),
      ),
    onSettled: () => invalidateProject(queryClient, id),
  })
}

/** FR-17: reject a draft (Owners); for an agent-produced draft the agent re-runs with the comment. */
export function useRejectArtifact(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ artifactId, comment }: DecideVariables) =>
      unwrap(
        await api.POST('/api/projects/{id}/artifacts/{artifactId}/reject', {
          params: { path: { id, artifactId } },
          body: { comment },
        }),
      ),
    onSettled: () => invalidateProject(queryClient, id),
  })
}
