import { Link } from '@tanstack/react-router'
import { useState } from 'react'

import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { canInProject } from '../../lib/permissions'
import { useDocumentTitle } from '../../lib/use-document-title'
import {
  escalationReasonLabels,
  formatDateTime,
  formatPercent,
  phaseLabels,
  reviewDecisionDescriptions,
  reviewDecisionLabels,
  reviewEscalationReasonDescriptions,
  reviewRunStatusLabels,
} from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState, type ProjectState } from '../projects/projects-api'
import { ComplianceTrendChart } from './compliance-trend-chart'
import {
  useComplianceTrend,
  useLatestCompliance,
  useStartReview,
  type ComplianceReport,
  type ReviewDecision,
  type ReviewRunStatus,
} from './review-api'
import { CategoryBreakdown, DriftSection, MissingFeaturesSection, ReportSection, ViolationsSection } from './review-report'

const statusTones: Record<ReviewRunStatus, BadgeTone> = {
  idle: 'neutral',
  running: 'info',
  completed: 'success',
  failed: 'danger',
}

const decisionTones: Record<ReviewDecision, BadgeTone> = {
  release_eligible: 'success',
  review_required: 'warning',
  escalation_required: 'danger',
}

/** FR-38: why "Run review" can't be used right now, or `null` when it can. */
function runDisabledReason(state: ProjectState): string | null {
  if (state.phase !== 'review') {
    return `Reviews run only in the Review phase; the project is in ${phaseLabels[state.phase]}.`
  }
  if (state.review.status === 'running') {
    return 'A review is already running. Its result appears here when it finishes.'
  }
  return null
}

/**
 * FR-39, FR-40: the optimisation plan a review drafted, and what has to happen before execution re-enters. After
 * review_required, approving the plan is enough; after escalation_required, the escalation gate comes first (FR-14).
 */
function OptimisationPlanNote({
  projectId,
  state,
  decision,
}: {
  projectId: string
  state: ProjectState
  decision: ReviewDecision | null
}) {
  const summary = state.artifacts.find((a) => a.type === 'optimization_plan')
  const approved = summary?.latestStatus === 'locked'
  const escalated = decision === 'escalation_required'
  let next: string
  if (escalated) {
    next = approved
      ? 'The plan is approved, but an Owner must decide the escalation gate first; Continue then lets it re-enter execution, and review runs again.'
      : 'An Owner must decide the escalation gate first; Continue then lets an approved plan re-enter execution, so the plan also needs approving.'
  } else {
    next = approved
      ? 'It is approved: execution runs its steps on the same branch, then review runs again.'
      : 'An Owner must approve it before execution re-enters to run its steps; review then runs again.'
  }
  return (
    <Alert tone={approved && !escalated ? 'info' : 'warning'} title="Optimisation plan">
      <p>
        This review drafted an optimisation plan. {next}{' '}
        <Link
          to="/projects/$projectId/artifacts/$type"
          params={{ projectId, type: 'optimization_plan' }}
          className="font-medium text-link underline"
        >
          Open the optimisation plan
        </Link>
      </p>
    </Alert>
  )
}

/** FR-38, FR-39: score vs threshold, cycle, decision and its reason in plain words. */
function ReportSummary({ report, state, projectId }: { report: ComplianceReport; state: ProjectState; projectId: string }) {
  const meets = report.overallScore >= report.threshold
  const reason = report.decisionReason && report.decisionReason !== 'step_blocked' ? report.decisionReason : null
  return (
    <ReportSection id="review-latest" title={`Latest report: cycle ${String(report.cycle)}, version ${String(report.projectVersion)}`}>
      <dl className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <div className="flex flex-col">
          <dt className="text-xs text-muted">Compliance score</dt>
          <dd>
            <span className="text-2xl font-semibold">{formatPercent(report.overallScore)}</span>{' '}
            <span className="text-sm">
              {meets ? 'meets' : 'below'} the {formatPercent(report.threshold)} threshold
            </span>
          </dd>
        </div>
        <div className="flex flex-col">
          <dt className="text-xs text-muted">Decision</dt>
          <dd className="flex flex-col gap-1">
            {report.decision ? (
              <>
                <span>
                  <Badge tone={decisionTones[report.decision]}>{reviewDecisionLabels[report.decision]}</Badge>
                </span>
                <span className="text-sm">{reviewDecisionDescriptions[report.decision]}</span>
              </>
            ) : (
              <span className="text-muted">Not decided</span>
            )}
          </dd>
        </div>
        <div className="flex flex-col">
          <dt className="text-xs text-muted">Reviewed</dt>
          <dd className="text-sm">{formatDateTime(report.createdAt)}</dd>
        </div>
      </dl>
      {reason && (
        <p className="text-sm">
          <span className="font-medium">Why it escalated: {escalationReasonLabels[reason]}.</span>{' '}
          {reviewEscalationReasonDescriptions[reason]}
        </p>
      )}
      {report.optimizationPlanArtifactId && <OptimisationPlanNote projectId={projectId} state={state} decision={report.decision} />}
    </ReportSection>
  )
}

/**
 * /projects/:id/review (SPEC §7; FR-38–FR-41): review status and cycle, "Run review" for writers, the latest report
 * (score vs threshold, decision and reason, category breakdown with evidence, drift, violations, missing features), the
 * score trend across cycles, and the optimisation plan link. Live `review` events refresh it (FR-46).
 */
export function ReviewPage() {
  const { project, user, role } = useProjectAccess()
  useDocumentTitle(`Review · ${project.name}`)
  const state = useProjectState(project.id)
  const running = state.data?.review.status === 'running'
  const latest = useLatestCompliance(project.id, running)
  const trend = useComplianceTrend(project.id)
  const writer = canInProject(user, role, 'triggerReview')
  const start = useStartReview(project.id)
  const [startError, setStartError] = useState<unknown>(null)
  const [queued, setQueued] = useState<number | null>(null)

  const header = (
    <h2 id="review-title" className="mb-4 text-xl font-semibold">
      Review
    </h2>
  )

  if (state.isPending || latest.isPending || trend.isPending) {
    return (
      <>
        {header}
        <LoadingState label="Loading review…" />
      </>
    )
  }
  if (state.isError || latest.isError || trend.isError) {
    return (
      <>
        {header}
        <ErrorState
          error={state.error ?? latest.error ?? trend.error}
          title="Review couldn't load"
          onRetry={() => {
            void state.refetch()
            void latest.refetch()
            void trend.refetch()
          }}
        />
      </>
    )
  }

  const s = state.data
  const review = s.review
  const report = latest.data
  const disabledReason = runDisabledReason(s)

  const run = () => {
    setStartError(null)
    setQueued(null)
    start.mutate(undefined, {
      onSuccess: (started) => {
        setQueued(started.cycle)
      },
      onError: (error) => {
        setStartError(error)
      },
    })
  }

  return (
    <section aria-labelledby="review-title">
      {header}
      <div className="flex flex-col gap-5">
        {writer ? (
          <div className="flex flex-col gap-2">
            <div>
              <Button
                disabled={disabledReason !== null}
                aria-describedby={disabledReason ? 'review-run-reason' : undefined}
                pending={start.isPending}
                pendingLabel="Starting review…"
                onClick={run}
              >
                Run review
              </Button>
            </div>
            {disabledReason && (
              <p id="review-run-reason" className="text-sm text-muted">
                {disabledReason}
              </p>
            )}
          </div>
        ) : (
          <Alert tone="info">You can follow reviews here. Only Contributors and Owners can run one.</Alert>
        )}
        <div role="alert" aria-live="assertive">
          {startError !== null && <Alert tone="error" title={errorMessage(startError, 'startReview')} />}
        </div>

        <section
          aria-labelledby="review-status"
          className="flex flex-col gap-2 rounded-lg border border-border bg-surface-raised p-4"
        >
          <div className="flex flex-wrap items-center gap-2">
            <h3 id="review-status" className="text-lg font-semibold">
              Review status
            </h3>
            <Badge tone={statusTones[review.status]}>{reviewRunStatusLabels[review.status]}</Badge>
          </div>
          <p className="text-sm">
            {review.cycle === 0
              ? `No review cycle yet (up to ${String(review.maxCycles)} allowed).`
              : `Cycle ${String(review.cycle)} of ${String(review.maxCycles)}`}
          </p>
          <div aria-live="polite" className="text-sm">
            {review.status === 'running' && (
              <p role="status" aria-busy="true">
                <span aria-hidden="true" className="mr-2 inline-block size-3 animate-pulse rounded-full bg-primary align-middle" />
                {queued === null ? 'Review running' : `Review cycle ${String(queued)} running`}: deterministic checks and the
                Code Review agent are scoring the code against the specification…
              </p>
            )}
          </div>
          {review.status === 'failed' && (
            <Alert tone="error" title="The last review failed">
              <p>
                No report was recorded for it.{' '}
                <Link to="/projects/$projectId/runs" params={{ projectId: project.id }} className="font-medium text-link underline">
                  See the agent runs
                </Link>{' '}
                for the reason{writer ? ', then run the review again.' : '.'}
              </p>
            </Alert>
          )}
        </section>

        {report === null ? (
          <EmptyState title="No review yet">
            <p>
              {s.phase === 'review'
                ? 'A review runs when the project enters Review, or when a Contributor or Owner runs one.'
                : 'Reviews start once execution completes and the project enters Review.'}
            </p>
          </EmptyState>
        ) : (
          <>
            <ReportSummary report={report} state={s} projectId={project.id} />
            <CategoryBreakdown report={report} />
            <ReportSection id="review-trend" title="Compliance trend">
              {trend.data.series.length === 0 ? (
                <p className="text-sm text-muted">No review cycles to compare yet.</p>
              ) : (
                <ComplianceTrendChart trend={trend.data} />
              )}
            </ReportSection>
            <DriftSection report={report} />
            <ViolationsSection report={report} />
            <MissingFeaturesSection report={report} />
          </>
        )}
      </div>
    </section>
  )
}
