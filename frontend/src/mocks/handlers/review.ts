import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { antiforgeryFailure, nextId } from './auth'
import { problem } from './problem'
import { access, record, type Access, type StoredProject } from './projects'

type Report = Schemas['ComplianceReportDto']
type CategoryScore = Schemas['CategoryScoreDto']

/** §4.7: categories and weights. */
const weights: Record<Schemas['ReviewCategory'], number> = {
  feature_coverage: 0.3,
  api_contracts: 0.2,
  data_model: 0.15,
  architecture: 0.15,
  security: 0.1,
  testing: 0.1,
}

/** Six category scores, each `score` (agent score, no penalty) with one evidence entry. */
export function categoryScores(score = 0.9): CategoryScore[] {
  return (Object.keys(weights) as Schemas['ReviewCategory'][]).map((category) => ({
    category,
    weight: weights[category],
    agentScore: score,
    penalty: 0,
    score,
    evidence: [{ file: `src/${category}.ts`, reason: `${category} evidence` }],
  }))
}

/** §4.7: Σ category score × weight, rounded to 3 dp. */
export function overallScore(scores: readonly CategoryScore[]): number {
  return Math.round(scores.reduce((sum, c) => sum + c.score * c.weight, 0) * 1000) / 1000
}

/** Reports the API would show: at or below the current project version (§4.8), oldest first. */
function visibleReports(p: StoredProject): Report[] {
  return (p.complianceReports ?? []).filter((r) => r.projectVersion <= p.projectVersion)
}

/** Puts the project in the review phase (status `reviewing`), as after execution completes (FR-36). */
export function seedReviewPhase(p: StoredProject) {
  p.currentPhase = 'review'
  p.status = 'reviewing'
}

/**
 * Seeds a compliance report for the next cycle (FR-38) and updates /state's review summary to match, as the review job
 * does. Defaults: every category at 0.93 (release eligible at the default 0.90 threshold), no violations, no drift.
 */
export function seedComplianceReport(p: StoredProject, overrides: Partial<Report> = {}): Report {
  const reports = (p.complianceReports ??= [])
  const scores = overrides.categoryScores ?? categoryScores(0.93)
  const report: Report = {
    id: nextId(),
    projectVersion: p.projectVersion,
    cycle: reports.length + 1,
    overallScore: overallScore(scores),
    threshold: p.complianceThreshold,
    categoryScores: scores,
    violations: [],
    missingFeatures: [],
    driftLevel: 'none',
    driftAreas: [],
    decision: 'release_eligible',
    decisionReason: null,
    optimizationPlanArtifactId: null,
    createdAt: new Date(Date.parse('2026-09-03T10:00:00Z') + reports.length * 3_600_000).toISOString(),
    ...overrides,
  }
  reports.push(report)
  p.state.review = {
    ...p.state.review,
    cycle: report.cycle,
    latestScore: report.overallScore,
    latestDecision: report.decision,
    driftLevel: report.driftLevel,
    status: 'completed',
  }
  return report
}

/** Writer (Contributor/Owner/Admin) access, including antiforgery. */
function writer(request: Request, id: unknown): Access {
  const a = access(id)
  if (a instanceof Response) {
    return a
  }
  const rejected = antiforgeryFailure(request)
  if (rejected) {
    return rejected
  }
  return a.level === 'viewer' ? problem(403, 'FORBIDDEN', 'Viewers can’t run a review.') : a
}

const noReport = () => problem(404, 'NOT_FOUND', 'No compliance report exists for this project yet.')

export const reviewHandlers = [
  // FR-38: queue a review cycle (writers, review phase only); 202 with the job id and the cycle it will record.
  http.post('*/api/projects/:id/review', async ({ request, params }) => {
    const a = writer(request, params.id)
    if (a instanceof Response) {
      return a
    }
    await record(request)
    const p = a.project
    if (p.currentPhase !== 'review') {
      return problem(409, 'WRONG_PHASE', `The project is in ${p.currentPhase}, not review.`)
    }
    if (p.state.review.status === 'running') {
      return problem(409, 'REVIEW_IN_PROGRESS', 'A review job is already queued or running.')
    }
    p.state.review = { ...p.state.review, status: 'running' }
    return HttpResponse.json<Schemas['ReviewStartedDto']>(
      { jobId: nextId(), cycle: p.state.review.cycle + 1 },
      { status: 202 },
    )
  }),
  // FR-41: the latest report; 404 before the first review.
  http.get('*/api/projects/:id/compliance/latest', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const latest = visibleReports(a.project).at(-1)
    return latest ? HttpResponse.json<Report>(latest) : noReport()
  }),
  // FR-41: the score per cycle, oldest first, each with its report's threshold, plus the project's current one.
  http.get('*/api/projects/:id/compliance/trend', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const series = visibleReports(a.project).map(({ cycle, projectVersion, overallScore, driftLevel, decision, createdAt, threshold }) => ({
      cycle,
      projectVersion,
      overallScore,
      driftLevel,
      decision,
      createdAt,
      threshold,
    }))
    return HttpResponse.json<Schemas['ComplianceTrendDto']>({ threshold: a.project.complianceThreshold, series })
  }),
  // FR-41: the latest report's drift; 404 before the first review.
  http.get('*/api/projects/:id/compliance/drift', ({ params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const latest = visibleReports(a.project).at(-1)
    return latest
      ? HttpResponse.json<Schemas['ComplianceDriftDto']>({
          driftLevel: latest.driftLevel,
          areas: latest.driftAreas,
          projectVersion: latest.projectVersion,
          cycle: latest.cycle,
        })
      : noReport()
  }),
]
