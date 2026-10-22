import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { CheckRunList } from '../git/check-runs'
import { mergeReasonLabels, mergeStrategyLabels } from '../projects/labels'
import type { ProjectState } from '../projects/projects-api'
import { RelativeTime } from '../projects/relative-time'
import { StageSection } from '../stages/stage-section'
import { mergeReadinessApplies } from './merge-readiness-rules'
import { useMergeReadiness, useRefreshMergeReadiness, type MergeReadiness, type MergeReadinessReason } from './merge-readiness-api'

/** FR-84, FR-65: the reasons AIDE can't merge (also recorded on an `awaiting_merge` release attempt). */
export function MergeReasons({ reasons, label = 'Why a merge on GitHub is needed' }: { reasons: readonly MergeReadinessReason[]; label?: string }) {
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

/** FR-84: "AIDE will merge automatically", or "A merge on GitHub will be needed" with the reasons. */
export function MergeVerdict({ readiness }: { readiness: MergeReadiness }) {
  const auto = readiness.willAutoMerge
  return (
    <div className="flex flex-col gap-2">
      <p className="flex flex-wrap items-center gap-2">
        <StatusBadge tone={auto ? 'success' : 'warning'}>
          {auto ? 'AIDE will merge automatically' : 'A merge on GitHub will be needed'}
        </StatusBadge>
      </p>
      <MergeReasons reasons={readiness.reasons} />
    </div>
  )
}

/**
 * The pre-release gate card's summary (FR-84): the verdict and its reasons from the cached readiness; the full panel
 * (checks, Check again) is on the Release page.
 */
export function MergeReadinessSummary({ projectId }: { projectId: string }) {
  const readiness = useMergeReadiness(projectId, true)
  if (readiness.isPending) {
    return <span className="text-muted-foreground">Checking merge readiness…</span>
  }
  if (readiness.isError) {
    return null
  }
  return (
    <div data-testid="gate-merge-readiness">
      <MergeVerdict readiness={readiness.data} />
    </div>
  )
}

/**
 * Release page → Merge readiness (FR-84): whether AIDE will merge the version's PR itself, why not, the PR head's check
 * runs and when it was checked. "Check again" asks GitHub again. The merge itself stays authoritative (FR-65).
 */
export function MergeReadinessPanel({ projectId, state }: { projectId: string; state: ProjectState }) {
  const applies = mergeReadinessApplies(state)
  const readiness = useMergeReadiness(projectId, applies)
  const refresh = useRefreshMergeReadiness(projectId)
  const canCheck = applies

  let body
  if (!applies) {
    body = (
      <p className="text-sm text-muted-foreground">
        {state.status === 'released' || state.developmentVersion === null
          ? 'This version is released, so there is no pull request left to merge.'
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
        <MergeVerdict readiness={r} />
        <p className="text-sm text-muted-foreground">
          Merge strategy: {mergeStrategyLabels[r.mergeStrategy]}. Checked <RelativeTime iso={r.checkedAt} />.
        </p>
        <section aria-labelledby="merge-checks-title" className="flex flex-col gap-1">
          <h4 id="merge-checks-title" className="font-semibold">
            Checks
          </h4>
          <CheckRunList checks={r.checks} label="Pull request checks" empty="No checks are reported on the pull request." />
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
