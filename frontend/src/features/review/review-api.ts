import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { api, type Schemas } from '../../api/client'
import { ApiError } from '../../lib/errors'
import { projectKeys, unwrap, type ProjectState } from '../projects/projects-api'

export type ComplianceReport = Schemas['ComplianceReportDto']
export type CategoryScore = Schemas['CategoryScoreDto']
export type Violation = Schemas['ViolationDto']
export type DriftArea = Schemas['DriftAreaDto']
export type ComplianceTrend = Schemas['ComplianceTrendDto']
export type ComplianceTrendPoint = Schemas['ComplianceTrendPointDto']
export type ReviewState = Schemas['ReviewStateDto']
export type ReviewRunStatus = Schemas['ReviewRunStatus']
export type ReviewDecision = NonNullable<Schemas['ReviewDecision']>
export type FindingSeverity = Schemas['Severity']
export type ComplianceReportSummary = Schemas['ComplianceReportSummaryDto']
export type ComplianceReportDetail = Schemas['ComplianceReportDetailDto']
export type Regression = Schemas['RegressionDto']

/** Fallback refresh while a review runs, for when the live connection is down (FR-46 normally refreshes first). */
export const REVIEW_RUNNING_REFETCH_MS = 15_000

/** Query keys, all under `projectKeys.review(id)` so a live `review` event refreshes every compliance view. */
export const reviewKeys = {
  all: (id: string) => projectKeys.review(id),
  latest: (id: string, releaseVersionId?: string) => [...projectKeys.review(id), 'latest', releaseVersionId ?? 'current'] as const,
  trend: (id: string, releaseVersionId?: string) => [...projectKeys.review(id), 'trend', releaseVersionId ?? 'current'] as const,
  reports: (id: string, releaseVersionId?: string) => [...projectKeys.review(id), 'reports', releaseVersionId ?? 'current'] as const,
  report: (id: string, cycle: number, reportId: string | null, releaseVersionId?: string) =>
    [...projectKeys.review(id), 'report', cycle, reportId ?? 'newest', releaseVersionId ?? 'current'] as const,
}

const isNotFound = (error: unknown) => error instanceof ApiError && error.status === 404

/**
 * FR-38, FR-41: the latest compliance report (members), or `null` before the first review (the API answers 404). A
 * rollback hides reports from the versions it undid, so this can go back to `null` (§4.8).
 */
export function useLatestCompliance(id: string, refetchWhileRunning: boolean, releaseVersionId?: string) {
  return useQuery({
    queryKey: reviewKeys.latest(id, releaseVersionId),
    queryFn: async ({ signal }): Promise<ComplianceReport | null> => {
      try {
        return unwrap(
          await api.GET('/api/projects/{id}/compliance/latest', {
            params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
            signal,
          }),
        )
      } catch (error) {
        if (isNotFound(error)) {
          return null
        }
        throw error
      }
    },
    refetchInterval: refetchWhileRunning ? REVIEW_RUNNING_REFETCH_MS : false,
  })
}

/** FR-41: the overall score per review cycle, oldest first, with the threshold; the series is empty before the first review. */
export function useComplianceTrend(id: string, releaseVersionId?: string) {
  return useQuery({
    queryKey: reviewKeys.trend(id, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/compliance/trend', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      ),
  })
}

/** FR-56: every review cycle of a release version, newest first (an empty list before the first review). */
export function useComplianceReports(id: string, refetchWhileRunning: boolean, releaseVersionId?: string) {
  return useQuery({
    queryKey: reviewKeys.reports(id, releaseVersionId),
    queryFn: async ({ signal }) => {
      const reports = unwrap(
        await api.GET('/api/projects/{id}/compliance/reports', {
          params: { path: { id }, query: releaseVersionId ? { releaseVersionId } : {} },
          signal,
        }),
      )
      // Newest first by time: cycle numbers restart after a rollback, so the cycle can't order them.
      return [...reports].sort((a, b) => b.createdAt.localeCompare(a.createdAt))
    },
    refetchInterval: refetchWhileRunning ? REVIEW_RUNNING_REFETCH_MS : false,
  })
}

/**
 * FR-56: the full report of one review cycle (`null` = nothing selected yet). `reportId` picks the exact report when
 * the cycle number repeats (after a rollback); without it the server returns the cycle's newest report.
 */
export function useComplianceReport(id: string, cycle: number | null, reportId: string | null, releaseVersionId?: string) {
  return useQuery({
    queryKey: reviewKeys.report(id, cycle ?? 0, reportId, releaseVersionId),
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/projects/{id}/compliance/reports/{cycle}', {
          params: {
            path: { id, cycle: cycle ?? 0 },
            query: { ...(releaseVersionId ? { releaseVersionId } : {}), ...(reportId ? { reportId } : {}) },
          },
          signal,
        }),
      ),
    enabled: cycle !== null,
  })
}

/**
 * FR-38: run a review cycle now (writers, review phase only). The 202 says the job is queued, so /state's review status
 * becomes `running` straight away; the result arrives with REVIEW_COMPLETED on the live channel. Errors
 * (WRONG_PHASE, REVIEW_IN_PROGRESS, CONFLICT) mean our view may be stale, so the project is refetched either way.
 */
export function useStartReview(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/projects/{id}/review', { params: { path: { id } } })),
    onSuccess: () => {
      queryClient.setQueryData<ProjectState>(projectKeys.state(id), (current) =>
        current ? { ...current, review: { ...current.review, status: 'running' } } : current,
      )
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: projectKeys.state(id) }),
        queryClient.invalidateQueries({ queryKey: projectKeys.detail(id), exact: true }),
        queryClient.invalidateQueries({ queryKey: projectKeys.review(id) }),
      ]),
  })
}
