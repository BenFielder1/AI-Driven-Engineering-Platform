import { keepPreviousData, queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import type { paths } from '../../api/generated/schema'
import { toApiError } from '../../lib/errors'
import type { ProjectRole } from '../../lib/permissions'

export type Project = Schemas['ProjectDto']
export type ProjectSummary = Schemas['ProjectSummaryDto']
export type ProjectState = Schemas['ProjectStateDto']
export type Member = Schemas['MemberDto']
export type ProjectSettings = Schemas['ProjectSettingsDto']
export type GitSettings = Schemas['GitSettingsDto']
export type LlmSettings = Schemas['LlmSettingsDto']
export type NotificationSettings = Schemas['NotificationSettingsDto']
export type GitTestResult = Schemas['GitTestResultDto']
export type Phase = Schemas['Phase']
export type ProjectStatus = Schemas['ProjectStatus']
export type ApprovalMode = Schemas['ApprovalMode']

export type ProjectListParams = NonNullable<paths['/api/projects']['get']['parameters']['query']>
export type ProjectSortBy = NonNullable<ProjectListParams['sortBy']>
export type SortOrder = NonNullable<ProjectListParams['sortOrder']>

/** Rows per page on /projects. */
export const PROJECTS_PAGE_SIZE = 20

export const projectKeys = {
  all: ['projects'] as const,
  lists: () => [...projectKeys.all, 'list'] as const,
  list: (params: ProjectListParams) => [...projectKeys.lists(), params] as const,
  detail: (id: string) => [...projectKeys.all, 'detail', id] as const,
  state: (id: string) => [...projectKeys.detail(id), 'state'] as const,
  members: (id: string) => [...projectKeys.detail(id), 'members'] as const,
  settings: (id: string) => [...projectKeys.detail(id), 'settings'] as const,
  /** GET /gates (all statuses; the overview splits pending from decided). */
  gates: (id: string) => [...projectKeys.detail(id), 'gates'] as const,
  /** GET /activity, per category filter (`all` when unfiltered). */
  activity: (id: string, category: string | null) => [...projectKeys.detail(id), 'activity', category ?? 'all'] as const,
  /** Every artifact query of the project (list, per-type latest/history/versions/diffs; see artifacts-api.ts). */
  artifacts: (id: string) => [...projectKeys.detail(id), 'artifacts'] as const,
  /** Every agent-run query of the project (pages per filter, single runs; see runs-api.ts). */
  runs: (id: string) => [...projectKeys.detail(id), 'runs'] as const,
  /** GET /refinement: latest draft, latest round and its clarifying questions (FR-27; see refinement-api.ts). */
  refinement: (id: string) => [...projectKeys.detail(id), 'refinement'] as const,
  /** Every execution-steps query of the project, per plan type and version (FR-30; see execution-api.ts). */
  execution: (id: string) => [...projectKeys.detail(id), 'execution'] as const,
  /** GET /git/pull-request: the recorded PR and its branch comparison (FR-36; see features/git/pull-request-api.ts). */
  pullRequest: (id: string) => [...projectKeys.detail(id), 'git', 'pull-request'] as const,
  /** Every compliance query of the project: latest report, trend and drift (FR-38–FR-41; see features/review/review-api.ts). */
  review: (id: string) => [...projectKeys.detail(id), 'review'] as const,
  /** GET /releases: every release attempt, newest first (FR-42, FR-43; see features/release/release-api.ts). */
  releases: (id: string) => [...projectKeys.detail(id), 'releases'] as const,
  /** GET /rollbacks: recorded rollbacks (FR-44). */
  rollbacks: (id: string) => [...projectKeys.detail(id), 'rollbacks'] as const,
  /** GET /versions: the project's release versions, highest first (FR-62; see features/versions/versions-api.ts). */
  releaseVersions: (id: string) => [...projectKeys.detail(id), 'release-versions'] as const,
  /** GET /checkpoints: a release version's checkpoints with their rollback eligibility (FR-44, FR-63). */
  checkpoints: (id: string) => [...projectKeys.detail(id), 'checkpoints'] as const,
}

/** Unwraps an openapi-fetch result: the body on success, an ApiError otherwise. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.data !== undefined) {
    return result.data
  }
  throw toApiError(result.error, result.response)
}

/** For 204 responses: throws an ApiError unless the response is OK. */
function ensureOk(result: { error?: unknown; response: Response }): void {
  if (!result.response.ok) {
    throw toApiError(result.error, result.response)
  }
}

// ---- Queries ---------------------------------------------------------------------------------------------------

export const projectsQueryOptions = (params: ProjectListParams) =>
  queryOptions({
    queryKey: projectKeys.list(params),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/projects', { params: { query: params }, signal })),
    placeholderData: keepPreviousData,
  })

/** FR-6: the caller's projects (all projects for Admins), paged, searched, filtered and sorted. */
export function useProjects(params: ProjectListParams) {
  return useQuery(projectsQueryOptions(params))
}

export const projectQueryOptions = (id: string) =>
  queryOptions({
    queryKey: projectKeys.detail(id),
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/projects/{id}', { params: { path: { id } }, signal })),
  })

export function useProject(id: string) {
  return useQuery(projectQueryOptions(id))
}

export const projectStateQueryOptions = (id: string) =>
  queryOptions({
    queryKey: projectKeys.state(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/state', { params: { path: { id } }, signal })),
  })

export function useProjectState(id: string) {
  return useQuery(projectStateQueryOptions(id))
}

/** FR-8: members are visible to every member. */
export function useMembers(id: string) {
  return useQuery({
    queryKey: projectKeys.members(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/members', { params: { path: { id } }, signal })),
  })
}

/** FR-9: settings (Owner only), with secrets as set / not-set flags. */
export function useProjectSettings(id: string, enabled: boolean) {
  return useQuery({
    queryKey: projectKeys.settings(id),
    queryFn: async ({ signal }) =>
      unwrap(await api.GET('/api/projects/{id}/settings', { params: { path: { id } }, signal })),
    enabled,
  })
}

// ---- Project mutations -----------------------------------------------------------------------------------------

/** FR-5: creates a project; the caller becomes its Owner. */
export function useCreateProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['CreateProjectRequest']) => unwrap(await api.POST('/api/projects', { body })),
    onSuccess: (project) => {
      queryClient.setQueryData(projectKeys.detail(project.id), project)
      return queryClient.invalidateQueries({ queryKey: projectKeys.lists() })
    },
  })
}

/** FR-7: partial update (Owner). */
export function useUpdateProject(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (body: Schemas['UpdateProjectRequest']) =>
      unwrap(await api.PATCH('/api/projects/{id}', { params: { path: { id } }, body })),
    onSuccess: (project) => {
      queryClient.setQueryData(projectKeys.detail(id), project)
      return queryClient.invalidateQueries({ queryKey: projectKeys.lists() })
    },
  })
}

/** FR-7: deletes the project and everything it owns (Owner). */
export function useDeleteProject(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      ensureOk(await api.DELETE('/api/projects/{id}', { params: { path: { id } } }))
    },
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: projectKeys.detail(id) })
      return queryClient.invalidateQueries({ queryKey: projectKeys.lists() })
    },
  })
}

// ---- Members ---------------------------------------------------------------------------------------------------

function useInvalidateMembership(id: string) {
  const queryClient = useQueryClient()
  // The caller's own role (ProjectDto.myRole) can change too, so the project is refreshed as well.
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: projectKeys.members(id) }),
      queryClient.invalidateQueries({ queryKey: projectKeys.detail(id), exact: true }),
      queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
    ])
}

/** FR-8: adds an existing user by email (Owner). */
export function useAddMember(id: string) {
  const invalidate = useInvalidateMembership(id)
  return useMutation({
    mutationFn: async (body: Schemas['AddMemberRequest']) =>
      unwrap(await api.POST('/api/projects/{id}/members', { params: { path: { id } }, body })),
    onSuccess: invalidate,
  })
}

/** FR-8: changes a member's role (Owner); the last Owner can't be demoted (LAST_OWNER). */
export function useUpdateMemberRole(id: string) {
  const invalidate = useInvalidateMembership(id)
  return useMutation({
    mutationFn: async ({ userId, role }: { userId: string; role: ProjectRole }) =>
      unwrap(
        await api.PATCH('/api/projects/{id}/members/{userId}', { params: { path: { id, userId } }, body: { role } }),
      ),
    onSettled: invalidate,
  })
}

/** FR-8: removes a member (Owner), or leaves the project (any member removing themselves). */
export function useRemoveMember(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (userId: string) => {
      ensureOk(await api.DELETE('/api/projects/{id}/members/{userId}', { params: { path: { id, userId } } }))
    },
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.members(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.lists() }),
      ]),
  })
}

// ---- Settings (FR-9, FR-10) ------------------------------------------------------------------------------------

function useSetSettingsSection<K extends keyof ProjectSettings>(id: string, key: K) {
  const queryClient = useQueryClient()
  return (section: ProjectSettings[K]) => {
    queryClient.setQueryData<ProjectSettings>(projectKeys.settings(id), (old) =>
      old ? { ...old, [key]: section } : old,
    )
  }
}

export function useUpdateGitSettings(id: string) {
  const setSection = useSetSettingsSection(id, 'git')
  return useMutation({
    mutationFn: async (body: Schemas['UpdateGitSettingsRequest']) =>
      unwrap(await api.PATCH('/api/projects/{id}/settings/git', { params: { path: { id } }, body })),
    onSuccess: setSection,
  })
}

export function useUpdateLlmSettings(id: string) {
  const setSection = useSetSettingsSection(id, 'llm')
  return useMutation({
    mutationFn: async (body: Schemas['UpdateLlmSettingsRequest']) =>
      unwrap(await api.PATCH('/api/projects/{id}/settings/llm', { params: { path: { id } }, body })),
    onSuccess: setSection,
  })
}

export function useUpdateNotificationSettings(id: string) {
  const setSection = useSetSettingsSection(id, 'notifications')
  return useMutation({
    mutationFn: async (body: Schemas['UpdateNotificationSettingsRequest']) =>
      unwrap(await api.PATCH('/api/projects/{id}/settings/notifications', { params: { path: { id } }, body })),
    onSuccess: setSection,
  })
}

/** FR-10: tests the saved Git settings; nothing is saved. */
export function useTestGitConnection(id: string) {
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/projects/{id}/settings/git/test', { params: { path: { id } } })),
  })
}
