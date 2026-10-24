import { Link } from '../../lib/navigation'
import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import {
  driftLevelLabels,
  escalationReasonLabels,
  findingSourceLabels,
  formatDateTime,
  formatPercent,
  phaseLabels,
  reviewCategoryLabels,
  reviewDecisionDescriptions,
  reviewDecisionLabels,
  reviewEscalationReasonDescriptions,
  reviewRunStatusLabels,
} from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { useProjectState, type ProjectState } from '../projects/projects-api'
import { PreviewDeploymentSection } from '../deployments/stage-deployment-sections'
import { StageArtifact } from '../stages/stage-artifact'
import { StageGates } from '../stages/stage-gates'
import { StageHeader } from '../stages/stage-header'
import { stageGates } from '../stages/stage-model'
import { StageRuns } from '../stages/stage-runs'
import { checkpointLabel, useReleaseVersionNames } from '../versions/version-label'
import { ComplianceTrendChart } from './compliance-trend-chart'
import { reportKey } from './report-key'
import {
  useComplianceReport,
  useComplianceReports,
  useComplianceTrend,
  useStartReview,
  type ComplianceReportDetail,
  type ComplianceReportSummary,
  type ReviewDecision,
  type ReviewRunStatus,
} from './review-api'
import {
  CategoryBreakdown,
  DriftSection,
  ExternalChecksSection,
  MissingFeaturesSection,
  ReportSection,
  ViolationsSection,
} from './review-report'

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
 * FR-56: once the plan ran, the execution run it produced is linked.
 */
function OptimisationPlanNote({
  projectId,
  report,
}: {
  projectId: string
  report: Pick<ComplianceReportDetail, 'decision' | 'optimizationPlan' | 'executionRunId'>
}) {
  const plan = report.optimizationPlan
  if (!plan) {
    return null
  }
  const approved = plan.status === 'locked'
  const escalated = report.decision === 'escalation_required'
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
    <Alert tone={approved && !escalated ? 'info' : 'warning'} title={`Optimisation plan v${String(plan.version)}`}>
      <p>
        This review drafted an optimisation plan. {next}{' '}
        <Link
          to="/projects/$projectId/artifacts/$type"
          params={{ projectId, type: 'optimization_plan' }}
          search={{ version: plan.version }}
          className="font-medium text-primary underline"
        >
          Open the optimisation plan
        </Link>
        {report.executionRunId && (
          <>
            {' · '}
            <Link
              to="/projects/$projectId/execution"
              params={{ projectId }}
              hash={`run-${report.executionRunId}`}
              className="font-medium text-primary underline"
            >
              See the execution run it produced
            </Link>
          </>
        )}
      </p>
    </Alert>
  )
}

/** FR-38, FR-39, FR-56: score vs threshold, cycle, decision and its reason in plain words. */
function ReportSummary({
  report,
  latest,
  projectId,
  versionName,
}: {
  report: ComplianceReportDetail
  latest: boolean
  projectId: string
  versionName: string | undefined
}) {
  const meets = report.overallScore >= report.threshold
  const reason = report.escalationReason && report.escalationReason !== 'step_blocked' ? report.escalationReason : null
  const where = checkpointLabel(versionName, report.checkpoint)
  return (
    <ReportSection
      id="review-latest"
      title={latest ? `Latest report: cycle ${String(report.cycle)}, ${where}` : `Cycle ${String(report.cycle)} report, ${where}`}
    >
      <dl className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <div className="flex flex-col">
          <dt className="text-xs text-muted-foreground">Compliance score</dt>
          <dd>
            <span className="text-2xl font-semibold">{formatPercent(report.overallScore)}</span>{' '}
            <span className="text-sm">
              {meets ? 'meets' : 'below'} the {formatPercent(report.threshold)} threshold
            </span>
          </dd>
        </div>
        <div className="flex flex-col">
          <dt className="text-xs text-muted-foreground">Decision</dt>
          <dd className="flex flex-col gap-1">
            {report.decision ? (
              <>
                <span>
                  <Badge tone={decisionTones[report.decision]}>{reviewDecisionLabels[report.decision]}</Badge>
                </span>
                <span className="text-sm">{reviewDecisionDescriptions[report.decision]}</span>
              </>
            ) : (
              <span className="text-muted-foreground">Not decided</span>
            )}
          </dd>
        </div>
        <div className="flex flex-col">
          <dt className="text-xs text-muted-foreground">Reviewed</dt>
          <dd className="text-sm">{formatDateTime(report.createdAt)}</dd>
        </div>
      </dl>
      {reason && (
        <p className="text-sm">
          <span className="font-medium">Why it escalated: {escalationReasonLabels[reason]}.</span>{' '}
          {reviewEscalationReasonDescriptions[reason]}
        </p>
      )}
      <OptimisationPlanNote projectId={projectId} report={report} />
    </ReportSection>
  )
}

/** FR-56: every cycle, newest first; picking one shows its report (kept in the URL as `?cycle=N`). */
function CyclesTable({
  reports,
  selected,
  onSelect,
}: {
  reports: ComplianceReportSummary[]
  /** The selected report's id. */
  selected: string | null
  onSelect: (report: ComplianceReportSummary) => void
}) {
  const th = 'px-3 py-2 font-medium'
  return (
    <ReportSection id="review-cycles" title={`Review cycles (${String(reports.length)})`}>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[36rem] border-collapse text-sm">
          <caption className="sr-only">Review cycles, newest first. Pick a cycle to see its report.</caption>
          <thead>
            <tr className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <th scope="col" className={th}>Cycle</th>
              <th scope="col" className={`${th} text-right`}>Score</th>
              <th scope="col" className={`${th} text-right`}>Threshold</th>
              <th scope="col" className={th}>Drift</th>
              <th scope="col" className={th}>Decision</th>
              <th scope="col" className={th}>Date</th>
            </tr>
          </thead>
          <tbody>
            {reports.map((r) => {
              const isSelected = r.id === selected
              return (
                <tr
                  key={r.id}
                  aria-current={isSelected ? 'true' : undefined}
                  className={`border-b border-border/40 last:border-b-0 ${isSelected ? 'bg-primary/10' : ''}`}
                >
                  <th scope="row" className="px-3 py-1 text-left font-medium">
                    <button
                      type="button"
                      aria-pressed={isSelected}
                      onClick={() => {
                        onSelect(r)
                      }}
                      className="min-h-9 text-primary underline underline-offset-4"
                    >
                      Cycle {r.cycle}
                    </button>
                  </th>
                  <td className="px-3 py-1 text-right font-mono tabular-nums">{formatPercent(r.score)}</td>
                  <td className="px-3 py-1 text-right font-mono tabular-nums">{formatPercent(r.threshold)}</td>
                  <td className="px-3 py-1">{r.drift ? driftLevelLabels[r.drift] : '—'}</td>
                  <td className="px-3 py-1">
                    {r.decision ? <Badge tone={decisionTones[r.decision]}>{reviewDecisionLabels[r.decision]}</Badge> : '—'}
                  </td>
                  <td className="px-3 py-1">{formatDateTime(r.createdAt)}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </ReportSection>
  )
}

/** FR-56: the deterministic findings alone (the violations whose source is a deterministic check). */
function DeterministicFindings({ report }: { report: ComplianceReportDetail }) {
  return (
    <ReportSection id="review-deterministic" title={`Deterministic findings (${String(report.deterministicFindings.length)})`}>
      {report.deterministicFindings.length === 0 ? (
        <p className="text-sm text-muted-foreground">The deterministic checks found nothing.</p>
      ) : (
        <ul className="flex flex-col gap-2 text-sm">
          {report.deterministicFindings.map((v, i) => (
            <li key={`${v.message}-${String(i)}`} className="border-l-2 border-border pl-3">
              <span>{v.message}</span>
              <span className="block text-xs text-muted-foreground">
                {reviewCategoryLabels[v.category]} · {findingSourceLabels[v.source]}
                {v.file ? ` · ${v.file}` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
    </ReportSection>
  )
}

/** §4.13: [unchanged] requirements the review found broken in this iteration. */
function Regressions({ report }: { report: ComplianceReportDetail }) {
  if (report.regressions.length === 0) {
    return null
  }
  return (
    <ReportSection id="review-regressions" title={`Regressions (${String(report.regressions.length)})`}>
      <ul className="flex flex-col gap-2 text-sm">
        {report.regressions.map((r) => (
          <li key={`${r.requirementId}-${r.description}`} className="border-l-2 border-warning pl-3">
            <span className="font-medium">{r.requirementId}</span>: {r.description}
            <span className="block text-xs text-muted-foreground">Category: {reviewCategoryLabels[r.category]}</span>
            {r.evidence && <span className="block text-xs">Evidence: {r.evidence}</span>}
          </li>
        ))}
      </ul>
    </ReportSection>
  )
}

function CycleDetail({
  projectId,
  cycle,
  reportId,
  latest,
  releaseVersionId,
}: {
  projectId: string
  cycle: number
  reportId: string
  latest: boolean
  releaseVersionId: string | undefined
}) {
  const versionName = useReleaseVersionNames(projectId)
  const report = useComplianceReport(projectId, cycle, reportId, releaseVersionId)
  if (report.isPending) {
    return <LoadingState label={`Loading the cycle ${String(cycle)} report…`} />
  }
  if (report.isError) {
    return <ErrorState error={report.error} title={`The cycle ${String(cycle)} report couldn't load`} onRetry={() => void report.refetch()} />
  }
  const r = report.data
  return (
    <div aria-live="polite" className="flex flex-col gap-5">
      <ReportSummary report={r} latest={latest} projectId={projectId} versionName={versionName(r.releaseVersionId)} />
      <CategoryBreakdown report={r} />
      <DriftSection report={r} />
      <ViolationsSection report={r} />
      <DeterministicFindings report={r} />
      <MissingFeaturesSection report={r} />
      <Regressions report={r} />
      <ExternalChecksSection report={r} />
    </div>
  )
}

/**
 * /projects/:id/review (FR-38–FR-41, FR-55, FR-56): review status and "Run review" for writers, every review cycle
 * (newest first) with the selected cycle's full report (`?cycle=N`, default the latest), the compliance trend (click a
 * point to select its cycle), the optimisation plan approved in place, the stage's escalation gates and the Code Review
 * runs. Live `review` events refresh it (FR-46).
 */
export function ReviewPage({
  cycle,
  report: reportParam,
  onSelectionChange,
}: {
  cycle?: number
  report?: string
  onSelectionChange?: (next: { cycle?: number; report?: string }) => void
}) {
  const { project, can, readOnly, releaseVersionId } = useProjectAccess()
  useDocumentTitle(`Review · ${project.name}`)
  const state = useProjectState(project.id)
  const running = !readOnly && state.data?.review.status === 'running'
  const reports = useComplianceReports(project.id, running, releaseVersionId)
  const trend = useComplianceTrend(project.id, releaseVersionId)
  const writer = can('triggerReview')
  const start = useStartReview(project.id)
  const [startError, setStartError] = useState<unknown>(null)
  const [queued, setQueued] = useState<number | null>(null)
  const [localPick, setLocalPick] = useState<{ cycle?: number; report?: string }>({})
  const picked = onSelectionChange ? { cycle, report: reportParam } : localPick
  /** FR-56: `?cycle=N` names the pick; `&report=<id>` is added only when that cycle number repeats. */
  const select = (next: ComplianceReportSummary) => {
    const all = reports.data ?? []
    const isLatest = next.id === all[0]?.id
    const ambiguous = all.filter((r) => r.cycle === next.cycle).length > 1
    const search = isLatest ? {} : { cycle: next.cycle, report: ambiguous ? next.id : undefined }
    if (onSelectionChange) {
      onSelectionChange(search)
    } else {
      setLocalPick(search)
    }
  }

  const header = (
    <StageHeader
      stage="review"
      awaitingApproval={
        state.data?.phase === 'review' &&
        (state.data.pendingGates.some((g) => g.type === 'escalation') ||
          state.data.artifacts.some((a) => a.type === 'optimization_plan' && a.latestStatus === 'draft'))
      }
    />
  )

  if (state.isPending || reports.isPending || trend.isPending) {
    return (
      <section aria-labelledby="review-title">
        {header}
        <LoadingState label="Loading review…" />
      </section>
    )
  }
  if (state.isError || reports.isError || trend.isError) {
    return (
      <section aria-labelledby="review-title">
        {header}
        <ErrorState
          error={state.error ?? reports.error ?? trend.error}
          title="Review couldn't load"
          onRetry={() => {
            void state.refetch()
            void reports.refetch()
            void trend.refetch()
          }}
        />
      </section>
    )
  }

  const s = state.data
  const review = s.review
  const list = reports.data
  const latestReport = list[0] ?? null
  // The exact report when named, else the newest report of the picked cycle, else the newest report.
  const selectedReport =
    (picked.report ? list.find((r) => r.id === picked.report) : undefined) ??
    (picked.cycle !== undefined ? list.find((r) => r.cycle === picked.cycle) : undefined) ??
    latestReport
  const selected = selectedReport?.id ?? null
  const disabledReason = runDisabledReason(s)

  const run = () => {
    setStartError(null)
    setQueued(null)
    start.mutate(undefined, {
      onSuccess: (started) => {
        setQueued(started.cycle)
        toast.success('Review queued.')
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
        {readOnly ? null : writer ? (
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
              <p id="review-run-reason" className="text-sm text-muted-foreground">
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

        {!readOnly && (
          <section
            aria-labelledby="review-status"
            className="flex flex-col gap-2 rounded-xl border border-border/60 bg-card/70 p-4 shadow-sm backdrop-blur-md"
          >
            <div className="flex flex-wrap items-center gap-2">
              <h3 id="review-status" className="text-lg">
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
                  <Link to="/projects/$projectId/runs" params={{ projectId: project.id }} className="font-medium text-primary underline">
                    See the agent runs
                  </Link>{' '}
                  for the reason{writer ? ', then run the review again.' : '.'}
                </p>
              </Alert>
            )}
          </section>
        )}

        {list.length === 0 || selectedReport === null ? (
          <EmptyState title="No review yet">
            <p>
              {s.phase === 'review'
                ? 'A review runs when the project enters Review, or when a Contributor or Owner runs one.'
                : 'Reviews start once execution completes and the project enters Review.'}
            </p>
          </EmptyState>
        ) : (
          <>
            <CyclesTable reports={list} selected={selected} onSelect={select} />
            {trend.data.series.length === 0 ? (
              <ReportSection id="review-trend" title="Compliance trend">
                <p className="text-sm text-muted-foreground">No review cycles to compare yet.</p>
              </ReportSection>
            ) : (
              <ComplianceTrendChart
                trend={trend.data}
                selectedKey={reportKey(selectedReport)}
                onSelectPoint={(point) => {
                  const match = list.find((r) => reportKey(r) === reportKey(point))
                  if (match) {
                    select(match)
                  }
                }}
              />
            )}
            <CycleDetail
              key={selectedReport.id}
              projectId={project.id}
              cycle={selectedReport.cycle}
              reportId={selectedReport.id}
              latest={selectedReport.id === latestReport?.id}
              releaseVersionId={releaseVersionId}
            />
          </>
        )}

        <StageArtifact
          stage="review"
          type="optimization_plan"
          // FR-40: a plan is only coming while a review runs or after a cycle decided review_required (the history polls
          // only while it is still empty, i.e. none exists yet).
          awaitingDraft={review.status === 'running' || latestReport?.decision === 'review_required'}
          notStarted="A review cycle that finds more work drafts an optimisation plan. Execution has to complete and a review has to run first."
        />
        {!readOnly && <PreviewDeploymentSection />}
        <StageGates types={stageGates.review} filter={(g) => g.stepNumber === null} />
        <StageRuns agent="code_review" />
      </div>
    </section>
  )
}
