import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { gitHostTerms, type GitHostTerms } from '../../lib/git-host'
import { CheckRunList } from '../git/check-runs'
import { mergeReasonLabels, mergeStrategyLabel } from '../projects/labels'
import type { ProjectState } from '../projects/projects-api'
import { RelativeTime } from '../projects/relative-time'
import { StageSection } from '../stages/stage-section'
import { mergeReadinessApplies } from './merge-readiness-rules'
import { useMergeReadiness, useRefreshMergeReadiness, type MergeReadiness, type MergeReadinessReason } from './merge-readiness-api'

/** FR-84, FR-65, FR-138: the reasons AIDE can't merge (also recorded on an `awaiting_merge` release attempt). */
export function MergeReasons({ reasons, label }: { reasons: readonly MergeReadinessReason[]; label: string }) {
  if (reasons.length === 0) {
    return null
  }
  return (
    <ul aria-label={label} className="list-disc pl-5 text-sm">
      {reasons.map((r) => (
        <li key={r.code}>{r.message || mergeReasonLabels[r.code]}</li>
      ))}
    </ul>
  )
}

/**
 * FR-84, FR-138: "AIDE will merge automatically", or "A merge on GitHub will be needed" (naming the project's Git host)
 * with the reasons. The host's raw merge state (`mergeStateStatus`, e.g. GitLab's `detailed_merge_status`) isn't shown.
 */
export function MergeVerdict({ readiness, host }: { readiness: MergeReadiness; host: GitHostTerms }) {
  const auto = readiness.willAutoMerge
  return (
    <div className="flex flex-col gap-2">
      <p className="flex flex-wrap items-center gap-2">
        <StatusBadge tone={auto ? 'success' : 'warning'}>
          {auto ? 'AIDE will merge automatically' : `A merge on ${host.host} will be needed`}
        </StatusBadge>
      </p>
      <MergeReasons reasons={readiness.reasons} label={`Why a merge on ${host.host} is needed`} />
    </div>
  )
}

/**
 * The pre-release gate card's summary (FR-84): the verdict and its reasons from the cached readiness; the full panel
 * (checks, Check again) is on the Release page.
 */
export function MergeReadinessSummary({ projectId, state }: { projectId: string; state: ProjectState }) {
  const readiness = useMergeReadiness(projectId, true)
  const host = gitHostTerms(state.pullRequest?.provider)
  if (readiness.isPending) {
    return <span className="text-muted-foreground">Checking merge readiness…</span>
  }
  if (readiness.isError) {
    return null
  }
  return (
    <div data-testid="gate-merge-readiness">
      <MergeVerdict readiness={readiness.data} host={host} />
    </div>
  )
}

/**
 * Release page → Merge readiness (FR-84, FR-138): whether AIDE will merge the version's PR (GitLab: MR) itself, why
 * not, the head's check runs and when it was checked. "Check again" asks the Git host again. The merge itself stays
 * authoritative (FR-65).
 */
export function MergeReadinessPanel({ projectId, state }: { projectId: string; state: ProjectState }) {
  const applies = mergeReadinessApplies(state)
  const readiness = useMergeReadiness(projectId, applies)
  const refresh = useRefreshMergeReadiness(projectId)
  const canCheck = applies
  const host = gitHostTerms(state.pullRequest?.provider)

  let body
  if (!applies) {
    body = (
      <p className="text-sm text-muted-foreground">
        {state.status === 'released' || state.developmentVersion === null
          ? `This version is released, so there is no ${host.pr} left to merge.`
          : 'Merge readiness is checked once the project reaches Review.'}
      </p>
    )
  } else if (readiness.isPending) {
    body = <LoadingState label="Checking merge readiness…" />
  } else if (readiness.isError) {
    body = <ErrorState error={readiness.error} title="Merge readiness couldn't load" context="mergeReadiness" onRetry={() => void readiness.refetch()} />
  } else {
    const r = readiness.data
    body = (
      <div className="flex flex-col gap-3">
        <MergeVerdict readiness={r} host={host} />
        <p className="text-sm text-muted-foreground">
          Merge strategy: {mergeStrategyLabel(r.mergeStrategy, host)}. Checked <RelativeTime iso={r.checkedAt} />.
        </p>
        <section aria-labelledby="merge-checks-title" className="flex flex-col gap-1">
          <h4 id="merge-checks-title" className="font-semibold">
            Checks
          </h4>
          <CheckRunList checks={r.checks} label={`${host.prTitle} checks`} empty={`No checks are reported on the ${host.pr}.`} />
        </section>
      </div>
    )
  }

  return (
    <StageSection
      id="release-merge-readiness"
      title="Merge readiness"
      actions={
        canCheck ? (
          <Button
            variant="secondary"
            pending={refresh.isPending}
            pendingLabel="Checking…"
            onClick={() => {
              refresh.mutate()
            }}
          >
            Check again
          </Button>
        ) : undefined
      }
    >
      {body}
      <div role="alert" aria-live="assertive">
        {refresh.isError && <Alert tone="error" title={errorMessage(refresh.error, 'mergeReadiness')} />}
      </div>
    </StageSection>
  )
}
