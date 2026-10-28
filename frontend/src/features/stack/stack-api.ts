import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { projectKeys, unwrap } from '../projects/projects-api'

export type StackCatalog = Schemas['StackCatalogDto']
export type StackCatalogChoice = Schemas['StackChoiceDto']
export type StackCatalogOption = Schemas['StackOptionDto']
export type StackOptions = Schemas['StackOptionsDto']
export type StackChoiceState = Schemas['StackChoiceStateDto']
export type StackOptionState = Schemas['StackOptionStateDto']
export type StackComposition = Schemas['StackCompositionDto']
export type StackCompositionRequest = Schemas['StackCompositionRequest']
export type StackDetection = Schemas['StackDetectionDto']
export type DetectStackRequest = Schemas['DetectStackRequest']
export type ProjectStack = Schemas['ProjectStackDto']
export type AdminStackCatalog = Schemas['AdminStackCatalogDto']
export type AdminStackPack = Schemas['AdminStackPackDto']

export const stackKeys = {
  all: ['stack'] as const,
  catalog: () => [...stackKeys.all, 'catalog'] as const,
  options: (request: StackCompositionRequest) => [...stackKeys.all, 'options', request] as const,
  validate: (request: StackCompositionRequest) => [...stackKeys.all, 'validate', request] as const,
  detect: (request: DetectStackRequest) => [...stackKeys.all, 'detect', request] as const,
  admin: () => [...stackKeys.all, 'admin'] as const,
  /** Under the project's detail key, so live project events and project-wide invalidations refresh it. */
  project: (id: string) => [...projectKeys.detail(id), 'stack'] as const,
}

/** The catalog ships with the server (FR-113) and only changes on a deploy, so it stays fresh for the session. */
const CATALOG_STALE_MS = 30 * 60_000

/** FR-113: the stack catalog (layers, choices with their option cards, rules, recommendations, version and hash). */
export function useStackCatalog(enabled = true) {
  return useQuery({
    queryKey: stackKeys.catalog(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/stack/catalog', { signal })),
    staleTime: CATALOG_STALE_MS,
    enabled,
  })
}

/**
 * FR-114, §4.17: the state of every option for the choices made so far (`POST /api/stack/options`, a read). The
 * previous answer stays shown while the next one loads (`isPlaceholderData`), so the cards don't flash.
 */
export function useStackOptions(request: StackCompositionRequest, enabled = true) {
  return useQuery({
    queryKey: stackKeys.options(request),
    queryFn: async ({ signal }) => unwrap(await api.POST('/api/stack/options', { body: request, signal })),
    placeholderData: keepPreviousData,
    staleTime: CATALOG_STALE_MS,
    enabled,
  })
}

/**
 * FR-116: the normalised composition with its derived facts (Next.js mode, specialists, origin models) and caveats,
 * or STACK_INCOMPATIBLE (violations) / VALIDATION (paths) as the query error. Off for `null`.
 */
export function useValidateStack(request: StackCompositionRequest | null) {
  return useQuery({
    queryKey: stackKeys.validate(request ?? {}),
    queryFn: async ({ signal }) => unwrap(await api.POST('/api/stack/validate', { body: request ?? {}, signal })),
    placeholderData: keepPreviousData,
    staleTime: CATALOG_STALE_MS,
    enabled: request !== null,
  })
}

/** FR-118: import detection over the repository's files at a ref. Nothing is stored; off for `null`. */
export function useStackDetection(request: DetectStackRequest | null) {
  return useQuery({
    queryKey: stackKeys.detect(request ?? { git: { provider: 'local', repo: '' } }),
    queryFn: async ({ signal }) => unwrap(await api.POST('/api/stack/detect', { body: request ?? { git: { provider: 'local', repo: '' } }, signal })),
    staleTime: 5 * 60_000,
    enabled: request !== null,
  })
}

/** FR-117 Settings → Stack: the composition pinned on the current release version, its lock and the stack change. */
export function useProjectStack(id: string, enabled = true) {
  return useQuery({
    queryKey: stackKeys.project(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/stack', { params: { path: { id } }, signal })),
    enabled,
  })
}

/**
 * FR-117: changes the stack of the version in development (Owner). 400 STACK_INCOMPATIBLE / VCS_MISMATCH /
 * VALIDATION; 409 STACK_LOCKED or CONFLICT. The stack chips show on the list and the header, so those refresh too.
 */
export function useUpdateProjectStack(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: StackCompositionRequest) =>
      unwrap(await api.PUT('/api/projects/{id}/stack', { params: { path: { id } }, body })),
    onSuccess: (stack) => {
      queryClient.setQueryData<ProjectStack>(stackKeys.project(id), stack)
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.detail(id), exact: true }),
        queryClient.invalidateQueries({ queryKey: [...projectKeys.detail(id), 'activity'] }),
        queryClient.invalidateQueries({ queryKey: projectKeys.releaseVersions(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
      ])
    },
  })
}

/** Admin → Integrations → Stack catalog (§6 `GET /api/admin/stack`): load status and every pack. */
export function useAdminStackCatalog() {
  return useQuery({
    queryKey: stackKeys.admin(),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/admin/stack', { signal })),
  })
}
