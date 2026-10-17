import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap, type Project } from '../projects/projects-api'

export type PresetSummary = Schemas['PresetSummaryDto']
export type PresetDetail = Schemas['PresetDetailDto']
export type PresetAgent = Schemas['PresetAgentDto']

/** The plan step's agent when there is no preset (FR-76). */
export const GENERALIST_AGENT = 'generalist'

export const presetKeys = {
  all: ['presets'] as const,
  list: () => [...presetKeys.all, 'list'] as const,
  detail: (id: string) => [...presetKeys.all, 'detail', id] as const,
}

/** Presets ship with the server (D-V2-12) and only change on a deploy, so they stay fresh for the session. */
const PRESET_STALE_MS = 30 * 60_000

/** FR-74: every preset (wizard Stack step, filters, admin list), ordered by id. */
export function usePresets(enabled = true) {
  return useQuery({
    queryKey: presetKeys.list(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/presets', { signal })),
    staleTime: PRESET_STALE_MS,
    enabled,
  })
}

export const presetQueryOptions = (presetId: string) =>
  queryOptions({
    queryKey: presetKeys.detail(presetId),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/presets/{presetId}', { params: { path: { presetId } }, signal })),
    staleTime: PRESET_STALE_MS,
  })

/** FR-74, FR-76: a preset in full (specialist agents, spec sections, checklist, protected paths); off for `null`. */
export function usePreset(presetId: string | null | undefined) {
  return useQuery({ ...presetQueryOptions(presetId ?? ''), enabled: Boolean(presetId) })
}

/**
 * FR-74: changes the preset of the version in development (Owner). 400 PRESET_NOT_FOUND; 409 PRESET_LOCKED (its
 * implementation plan is locked, or nothing is in development) or CONFLICT. The chip shows on the list, the header and
 * the versions, so those refresh too.
 */
export function useUpdateProjectPreset(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (presetId: string | null) =>
      unwrap(await api.PATCH('/api/projects/{id}/preset', { params: { path: { id } }, body: { presetId } })),
    onSuccess: (project) => {
      queryClient.setQueryData<Project>(projectKeys.detail(id), project)
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.releaseVersions(id) }),
        queryClient.invalidateQueries({ queryKey: [...projectKeys.detail(id), 'activity'] }),
        queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
      ])
    },
  })
}

/** "nextjs-frontend@1.0.0", or null without a preset. */
export function presetRef(presetId: string | null | undefined, presetVersion: string | null | undefined): string | null {
  if (!presetId) {
    return null
  }
  return presetVersion ? `${presetId}@${presetVersion}` : presetId
}

/** "Database engineer (db-engineer)" for a catalogue agent; the key alone when the preset doesn't list it. */
export function agentLabel(key: string, agents: readonly PresetAgent[] | undefined): { name: string | null; key: string } {
  const agent = agents?.find((a) => a.key === key)
  return { name: agent?.name ?? (key === GENERALIST_AGENT ? 'Generalist' : null), key }
}

/** Display names for deployment targets (§4.11 `deployTargets`). */
export const deployTargetLabels: Readonly<Record<string, string>> = { vercel: 'Vercel' }

export const deployTargetLabel = (target: string) => deployTargetLabels[target] ?? target
