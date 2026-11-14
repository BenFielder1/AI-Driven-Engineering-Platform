import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { ErrorState } from '../../components/ui/states'
import { EvidenceBlock } from '../../components/shared/evidence-block'
import { panelClass } from '../../components/shared/panel-class'
import { cn } from '../../lib/utils'
import { buildCheckStatusLabels } from '../projects/labels'
import {
  useBuildChecks,
  type BuildCheck,
  type BuildCheckFailure,
  type BuildCheckSource,
  type BuildCheckStatus,
} from './build-checks-api'

const statusTones: Record<BuildCheckStatus, BadgeTone> = {
  waiting: 'info',
  passed: 'success',
  failed: 'danger',
  skipped: 'neutral',
  exhausted: 'warning',
  cancelled: 'neutral',
}

/** A source's conclusion (`null` while pending) as text; colour only repeats it. */
function conclusionText(conclusion: string | null): { label: string; tone: BadgeTone } {
  switch (conclusion) {
    case null:
      return { label: 'Pending', tone: 'info' }
    case 'success':
      return { label: 'Passed', tone: 'success' }
    case 'failure':
      return { label: 'Failed', tone: 'danger' }
    case 'timed_out':
      return { label: 'Timed out', tone: 'danger' }
    case 'action_required':
      return { label: 'Action required', tone: 'warning' }
    case 'cancelled':
      return { label: 'Cancelled', tone: 'neutral' }
    case 'skipped':
      return { label: 'Skipped', tone: 'neutral' }
    case 'neutral':
      return { label: 'Neutral', tone: 'neutral' }
    default: {
      const words = conclusion.replaceAll('_', ' ')
      return { label: words.charAt(0).toUpperCase() + words.slice(1), tone: 'neutral' }
    }
  }
}

/** "A", "A and B", "A, B and C". */
function listText(names: string[]): string {
  return names.length <= 1 ? (names[0] ?? '') : `${names.slice(0, -1).join(', ')} and ${names.at(-1) ?? ''}`
}

const plural = (n: number, one: string, many = `${one}s`) => `${String(n)} ${n === 1 ? one : many}`

/** "Attempt 2 of 5"; attempt 0 checks the plan's own commit, before any fix (§4.47). */
function attemptText(check: BuildCheck, max: number): string {
  return check.attempt === 0 ? 'Initial check' : `Attempt ${String(check.attempt)} of ${String(max)}`
}

/** What the check is doing or concluded, in words (SPEC §7 part 2). */
function statusLine(check: BuildCheck): string {
  switch (check.status) {
    case 'waiting': {
      const pending = check.sources.filter((s) => s.conclusion === null).map((s) => s.name)
      return pending.length > 0 ? `Waiting for ${listText(pending)}` : 'Waiting for the preview build and CI checks'
    }
    case 'passed':
      return 'The preview build and required checks passed'
    case 'failed':
      return check.fixStepNumbers.length > 0
        ? `Build failed: fix ${check.fixStepNumbers.length === 1 ? 'step' : 'steps'} ${listText(check.fixStepNumbers.map(String))} planned`
        : 'Build failed: planning fix steps'
    case 'skipped':
      return 'No preview target or required checks'
    case 'exhausted':
      return 'Review starts with the failures'
    case 'cancelled':
      return 'Cancelled: the version changed or execution was stopped'
  }
}

function SourceList({ sources, label }: { sources: BuildCheckSource[]; label: string }) {
  if (sources.length === 0) {
    return null
  }
  return (
    <ul aria-label={label} className="flex flex-col gap-1 text-sm">
      {sources.map((s) => {
        const result = conclusionText(s.conclusion)
        return (
          <li key={`${s.kind}:${s.name}`} className="flex flex-wrap items-center gap-2">
            <span className="text-muted-foreground">{s.kind === 'preview' ? 'Preview' : 'CI check'}</span>
            {s.url ? (
              <a href={s.url} target="_blank" rel="noreferrer" className="break-all text-primary underline">
                {s.name}
                <span className="sr-only"> (opens in a new tab)</span>
              </a>
            ) : (
              <span className="break-all">{s.name}</span>
            )}
            <Badge tone={result.tone}>{result.label}</Badge>
          </li>
        )
      })}
    </ul>
  )
}

function FailureList({ failures, attemptLabel }: { failures: BuildCheckFailure[]; attemptLabel: string }) {
  if (failures.length === 0) {
    return null
  }
  return (
    <ul aria-label={`Failures of the ${attemptLabel.toLowerCase()}`} className="flex flex-col gap-2">
      {failures.map((f) => (
        <li key={f.failureId} className="flex min-w-0 flex-col gap-1 rounded-lg border border-border/60 p-2 text-sm">
          <p className="break-words">
            <code className="text-xs text-muted-foreground">{f.failureId}</code> {f.message}
          </p>
          {f.excerpt && (
            <EvidenceBlock text={f.excerpt} label={`Log excerpt for ${f.failureId}`} testId="build-check-excerpt" />
          )}
        </li>
      ))}
    </ul>
  )
}

const shortSha = (sha: string) => (
  <code className="text-xs" title={sha}>
    {sha.slice(0, 7)}
  </code>
)

/** An earlier attempt of the cycle, collapsed: status, commit, planned fix steps, sources and failures. */
function EarlierAttempt({ check, max, open }: { check: BuildCheck; max: number; open: boolean }) {
  const label = attemptText(check, max)
  return (
    <li>
      <details open={open} className="rounded-lg border border-border/60 p-2">
        <summary className="cursor-pointer text-sm">
          <span className="font-medium">{label}</span> · {buildCheckStatusLabels[check.status]} · commit {shortSha(check.headSha)}
          {check.fixStepNumbers.length > 0 && <> · fix steps {check.fixStepNumbers.join(', ')}</>}
        </summary>
        <div className="mt-2 flex flex-col gap-2">
          <SourceList sources={check.sources} label={`Sources of the ${label.toLowerCase()}`} />
          <FailureList failures={check.failures} attemptLabel={label} />
        </div>
      </details>
    </li>
  )
}

/**
 * FR-259 (§7 part 2): the build check of the version's current review cycle, above the step list: status, "Attempt n of
 * max", each source (preview platform and CI checks) with its result and link, and the failures with their log
 * excerpts. Earlier attempts of the cycle collapse into a list (the previous one open while the next check waits, so
 * what is being fixed stays in view). Renders nothing while loading or when the cycle has no check (0.6.0 behaviour,
 * or `maxBuildFixAttempts` 0). A check's `reviewCycle` is the cycle it ran in, so it gates review `reviewCycle + 1`.
 * `cycle` null: the newest check's cycle (once the project has left Execution, or a past release version).
 */
export function BuildCheckPanel({
  projectId,
  versionId,
  cycle,
  maxAttempts,
}: {
  projectId: string
  versionId: string | null
  cycle: number | null
  maxAttempts: number
}) {
  const query = useBuildChecks(projectId, versionId)
  if (query.isPending) {
    return null
  }
  if (query.isError) {
    return (
      <ErrorState error={query.error} title="The build check couldn't load" onRetry={() => void query.refetch()} />
    )
  }
  const shownCycle = cycle ?? query.data[0]?.reviewCycle ?? null
  const checks = query.data.filter((c) => c.reviewCycle === shownCycle)
  const [current, ...earlier] = checks
  if (!current) {
    return null
  }
  const label = attemptText(current, maxAttempts)
  // The fixes actually planned in this cycle (the current check and the ones before it), not the attempt number: a
  // cancelled or superseded check leaves gaps.
  const fixAttempts = checks.filter((c) => c.fixStepNumbers.length > 0).length
  const waiting = current.status === 'waiting'

  return (
    <section
      aria-labelledby="build-check-title"
      data-testid="build-check-panel"
      className={cn(panelClass, 'flex min-w-0 flex-col gap-3 p-4')}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 id="build-check-title" className="text-lg">
          Build check
        </h3>
        <Badge tone={statusTones[current.status]}>{buildCheckStatusLabels[current.status]}</Badge>
      </div>
      <div aria-live="polite" className="text-sm">
        <p data-testid="build-check-summary">
          {waiting && (
            <span
              aria-hidden="true"
              className="mr-2 inline-block size-3 rounded-full bg-primary align-middle motion-safe:animate-pulse"
            />
          )}
          {label} · {statusLine(current)}
        </p>
      </div>
      {current.status === 'exhausted' && (
        <Alert tone="warning">
          <p>Build still failing after {plural(fixAttempts, 'fix attempt')}. Review will include the failures.</p>
        </Alert>
      )}
      <p className="text-xs text-muted-foreground">
        Before review cycle {current.reviewCycle + 1} · commit {shortSha(current.headSha)}
      </p>
      <SourceList sources={current.sources} label="Sources" />
      <FailureList failures={current.failures} attemptLabel={label} />
      {earlier.length > 0 && (
        <div className="flex flex-col gap-2">
          <h4 className="text-sm font-semibold">Earlier attempts ({earlier.length})</h4>
          <ol aria-label="Earlier attempts" className="flex flex-col gap-2">
            {earlier.map((c, i) => (
              <EarlierAttempt key={c.id} check={c} max={maxAttempts} open={waiting && i === 0} />
            ))}
          </ol>
        </div>
      )}
    </section>
  )
}
