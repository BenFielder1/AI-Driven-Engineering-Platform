import { Link } from '../../lib/navigation'
import { useState, type ReactNode } from 'react'

import { Badge } from '../../components/ui/badge'
import { stepTones } from './step-tones'
import { guardianCheckLabels, stepStatusLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import type { Gate } from '../gates/gates-api'
import { MAX_STEP_ATTEMPTS, type ExecutionStep, type StepViolation } from './execution-api'


const plural = (n: number, one: string, many = `${one}s`) => `${String(n)} ${n === 1 ? one : many}`

function CopyButton({ value, label }: { value: string; label: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
  return (
    <>
      <button
        type="button"
        className="text-xs text-primary underline"
        aria-label={label}
        onClick={() => {
          navigator.clipboard.writeText(value).then(
            () => {
              setState('copied')
            },
            () => {
              setState('failed')
            },
          )
        }}
      >
        Copy
      </button>
      <span role="status" aria-live="polite" className="text-xs text-muted-foreground">
        {state === 'copied' && 'Copied.'}
        {state === 'failed' && "Couldn't copy: select the SHA and copy it manually."}
      </span>
    </>
  )
}

/** FR-34: the short SHA, linked to the commit when the API gives its URL (GitHub), else plain text with a copy button. */
export function Commit({ sha, url }: { sha: string; url: string | null }) {
  const short = sha.slice(0, 7)
  return url ? (
    <a href={url} target="_blank" rel="noreferrer" className="font-mono text-primary underline" title={sha}>
      {short}
      <span className="sr-only"> (full SHA {sha}, opens GitHub in a new tab)</span>
    </a>
  ) : (
    <span className="inline-flex flex-wrap items-center gap-2">
      <code className="text-xs" title={sha}>
        {short}
      </code>
      <CopyButton value={sha} label={`Copy commit SHA ${sha}`} />
    </span>
  )
}

export function ViolationList({ title, violations }: { title: string; violations: StepViolation[] }) {
  return (
    <div className="flex flex-col gap-1">
      <h5 className="text-sm font-semibold">{title}</h5>
      <ul className="flex flex-col gap-1">
        {violations.map((v) => (
          <li key={`${v.check}:${v.code}:${v.path ?? ''}:${v.message}`} className="rounded border border-border p-2 text-sm">
            <span className="font-medium">{guardianCheckLabels[v.check]}</span>{' '}
            <code className="text-xs text-muted-foreground">{v.code}</code>
            <p className="break-words">{v.message}</p>
            {v.path && (
              <p className="text-xs text-muted-foreground">
                File: <code className="break-all">{v.path}</code>
              </p>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}

/** FR-32: the last attempt's Guardian violations, errors first, grouped by severity (text, not colour). */
function Violations({ violations, open }: { violations: StepViolation[]; open: boolean }) {
  const errors = violations.filter((v) => v.severity === 'error')
  const warnings = violations.filter((v) => v.severity === 'warning')
  const parts = [
    ...(errors.length > 0 ? [plural(errors.length, 'error')] : []),
    ...(warnings.length > 0 ? [plural(warnings.length, 'warning')] : []),
  ]
  return (
    <details open={open} className="rounded-lg border border-border/60 p-2">
      <summary className="cursor-pointer text-sm font-medium">Guardian violations: {parts.join(', ')}</summary>
      <div className="mt-2 flex flex-col gap-3">
        {errors.length > 0 && <ViolationList title={`Errors (${String(errors.length)}): these rejected the attempt`} violations={errors} />}
        {warnings.length > 0 && <ViolationList title={`Warnings (${String(warnings.length)}): recorded only`} violations={warnings} />}
      </div>
    </details>
  )
}

/** FR-33: a blocked step waits for an Owner's escalation decision on its own gate, listed on the overview. */
function BlockedNotice({ projectId, gate }: { projectId: string; gate: Gate | null }) {
  return (
    <div className="rounded-md border-l-4 border-warning bg-warning/10 p-3 text-sm">
      <p className="font-semibold">Blocked after {MAX_STEP_ATTEMPTS} rejected attempts</p>
      {gate ? (
        <p className="mt-1">
          An Owner decides its escalation: continue retries the step with fresh attempts, roll back returns the project
          to an earlier version.{' '}
          <Link
            to="/projects/$projectId"
            params={{ projectId }}
            hash={`pending-gate-${gate.id}`}
            className="text-primary underline"
          >
            Go to this step&apos;s escalation gate
          </Link>
        </p>
      ) : (
        <p className="mt-1">The escalation gate for this step isn&apos;t listed yet. It appears under Pending gates on the overview.</p>
      )}
    </div>
  )
}

function attemptText(step: ExecutionStep) {
  if (step.attempt === 0) {
    return 'Not attempted yet'
  }
  return `Attempt ${String(step.attempt)} of ${String(MAX_STEP_ATTEMPTS)}`
}

/** One step: status, attempt, dependencies, declared files, violations, commit and file manifest (FR-30–FR-34). */
export function ExecutionStepItem({
  projectId,
  step,
  current,
  escalationGate,
  extra,
}: {
  projectId: string
  step: ExecutionStep
  current: boolean
  /** The pending escalation gate created for this step when it was blocked (FR-33), if listed. */
  escalationGate: Gate | null
  /** FR-57: the run's view of the step (specialist, produced files, attempt history). */
  extra?: ReactNode
}) {
  const titleId = `step-${String(step.number)}-title`
  const violations = step.violations ?? []
  return (
    <li
      aria-labelledby={titleId}
      aria-current={current ? 'step' : undefined}
      className={`flex flex-col gap-2 rounded-lg border bg-card p-4 ${current ? 'border-primary' : 'border-border'}`}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h4 id={titleId} className="text-base font-semibold break-words">
          Step {step.number}: {step.title}
        </h4>
        <Badge tone={stepTones[step.status]}>{stepStatusLabels[step.status]}</Badge>
        {current && <span className="text-xs font-medium">(running now)</span>}
      </div>
      <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
        <div className="flex gap-1">
          <dt className="text-muted-foreground">Attempt</dt>
          <dd>{attemptText(step)}</dd>
        </div>
        <div className="flex gap-1">
          <dt className="text-muted-foreground">Depends on</dt>
          <dd>{step.dependencies.length === 0 ? 'None' : step.dependencies.map((d) => `step ${String(d)}`).join(', ')}</dd>
        </div>
        {step.startedAt && (
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Started</dt>
            <dd>
              <RelativeTime iso={step.startedAt} />
            </dd>
          </div>
        )}
        {step.completedAt && (
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Finished</dt>
            <dd>
              <RelativeTime iso={step.completedAt} />
            </dd>
          </div>
        )}
        {step.commitSha && (
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Commit</dt>
            <dd>
              <Commit sha={step.commitSha} url={step.commitUrl} />
            </dd>
          </div>
        )}
        {step.branchName && (
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Branch</dt>
            <dd>
              <code className="text-xs break-all">{step.branchName}</code>
            </dd>
          </div>
        )}
      </dl>

      {step.status === 'blocked' && <BlockedNotice projectId={projectId} gate={escalationGate} />}
      {violations.length > 0 && <Violations violations={violations} open={step.status === 'blocked'} />}

      <details className="text-sm">
        <summary className="cursor-pointer">Task and declared files ({plural(step.declaredFiles.length, 'file')})</summary>
        <p className="mt-2 whitespace-pre-wrap break-words">{step.task}</p>
        {step.userInstructions && (
          <p className="mt-2 whitespace-pre-wrap break-words">
            <span className="font-medium">Instructions: </span>
            {step.userInstructions}
          </p>
        )}
        <ul className="mt-2 flex flex-col gap-0.5">
          {step.declaredFiles.map((f) => (
            <li key={`${f.action}:${f.path}`} className="break-all">
              <code className="text-xs">{f.path}</code> <span className="text-xs text-muted-foreground">({f.action})</span>
              {f.protected && (
                <>
                  {' '}
                  <Badge tone="warning">Protected</Badge>
                </>
              )}
            </li>
          ))}
        </ul>
      </details>

      {step.fileManifest && step.fileManifest.length > 0 && (
        <details className="text-sm">
          <summary className="cursor-pointer">Files written ({plural(step.fileManifest.length, 'file')})</summary>
          <table className="mt-2 w-full border-collapse text-sm">
            <caption className="sr-only">Files written by step {step.number}</caption>
            <thead>
              <tr className="border-b border-border text-left text-xs text-muted-foreground">
                <th scope="col" className="py-1 pr-3">
                  File
                </th>
                <th scope="col" className="py-1 pr-3">
                  Action
                </th>
                <th scope="col" className="py-1">
                  Content hash
                </th>
              </tr>
            </thead>
            <tbody>
              {step.fileManifest.map((f) => (
                <tr key={f.path} className="border-b border-border last:border-b-0">
                  <td className="py-1 pr-3 break-all">
                    <code className="text-xs">{f.path}</code>
                  </td>
                  <td className="py-1 pr-3">{f.action === 'create' ? 'Created' : 'Modified'}</td>
                  <td className="py-1">
                    <code className="text-xs" title={f.contentHash}>
                      {f.contentHash.slice(0, 12)}
                    </code>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
      {extra}
    </li>
  )
}
