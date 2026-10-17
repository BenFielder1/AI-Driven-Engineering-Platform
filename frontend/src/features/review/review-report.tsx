import { useState, type ReactNode } from 'react'

import { Alert } from '../../components/ui/alert'
import { Badge, type BadgeTone } from '../../components/ui/badge'
import { isFailingCheck } from '../git/check-run-rules'
import { CheckRunList } from '../git/check-runs'
import {
  driftLevelLabels,
  findingSeverities,
  findingSeverityLabels,
  findingSourceLabels,
  formatPercent,
  reviewCategoryLabels,
} from '../projects/labels'
import { BuildLogTail, DeploymentLink, DeploymentStatusPill } from '../deployments/deployment-parts'
import type { PreviewCheck } from '../deployments/deployments-api'
import { CategoryBarsChart } from './category-bars-chart'
import type { CategoryScore, ComplianceReportDetail, FindingSeverity } from './review-api'

/** The report fields the sections read (the v2 detail DTO; the v1 latest-report DTO has the same shapes). */
type Report = Pick<ComplianceReportDetail, 'categoryScores' | 'driftLevel' | 'driftAreas' | 'violations' | 'missingFeatures'>

const severityTones: Record<FindingSeverity, BadgeTone> = {
  critical: 'danger',
  major: 'warning',
  minor: 'neutral',
}

const driftTones: Record<NonNullable<Report['driftLevel']>, BadgeTone> = {
  none: 'success',
  low: 'neutral',
  moderate: 'warning',
  severe: 'danger',
}

const plural = (n: number, one: string, many = `${one}s`) => `${String(n)} ${n === 1 ? one : many}`

export function ReportSection({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-3 rounded-xl border border-border/60 bg-card/70 shadow-sm backdrop-blur-md p-4">
      <h3 id={id} className="text-lg">
        {title}
      </h3>
      {children}
    </section>
  )
}

function CategoryRow({ score }: { score: CategoryScore }) {
  const [open, setOpen] = useState(false)
  const label = reviewCategoryLabels[score.category]
  const evidenceId = `evidence-${score.category}`
  const count = score.evidence.length
  return (
    <>
      <tr className="border-b border-border/60 text-muted-foreground">
        <th scope="row" className="py-2 pr-3 font-medium">
          {label}
        </th>
        <td className="py-2 pr-3">{formatPercent(score.weight)}</td>
        <td className="py-2 pr-3">{formatPercent(score.agentScore)}</td>
        <td className="py-2 pr-3">{score.penalty > 0 ? `−${formatPercent(score.penalty)}` : 'None'}</td>
        <td className="py-2 pr-3 font-semibold">{formatPercent(score.score)}</td>
        <td className="py-2">
          {count === 0 ? (
            <span className="text-muted-foreground">No evidence</span>
          ) : (
            <button
              type="button"
              className="text-primary underline"
              aria-expanded={open}
              aria-controls={evidenceId}
              onClick={() => {
                setOpen((o) => !o)
              }}
            >
              {open ? 'Hide' : 'Show'} evidence ({count}) <span className="sr-only">for {label}</span>
            </button>
          )}
        </td>
      </tr>
      {count > 0 && (
        <tr id={evidenceId} hidden={!open} className="border-b border-border">
          <td colSpan={6} className="pb-3 pl-4">
            <ul aria-label={`Evidence for ${label}`} className="flex list-disc flex-col gap-1 pl-5">
              {score.evidence.map((e, i) => (
                <li key={`${e.file ?? ''}-${String(i)}`}>
                  {e.file && <code className="mr-1 text-xs">{e.file}</code>}
                  {e.file ? '— ' : ''}
                  {e.reason}
                </li>
              ))}
            </ul>
          </td>
        </tr>
      )}
    </>
  )
}

/** FR-41, §4.7: weight, agent score, deterministic penalty and final score per category, with expandable evidence. */
export function CategoryBreakdown({ report }: { report: Report }) {
  return (
    <ReportSection id="review-categories" title="Category breakdown">
      <CategoryBarsChart scores={report.categoryScores} />
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <caption className="sr-only">
            Compliance score per category: weight, the review agent&apos;s score, the deterministic penalty and the final
            score
          </caption>
          <thead>
            <tr className="border-b border-border/60 text-muted-foreground">
              <th scope="col" className="py-1 pr-3 font-medium">Category</th>
              <th scope="col" className="py-1 pr-3 font-medium">Weight</th>
              <th scope="col" className="py-1 pr-3 font-medium">Agent score</th>
              <th scope="col" className="py-1 pr-3 font-medium">Penalty</th>
              <th scope="col" className="py-1 pr-3 font-medium">Final score</th>
              <th scope="col" className="py-1 font-medium">Evidence</th>
            </tr>
          </thead>
          <tbody>
            {report.categoryScores.map((s) => (
              <CategoryRow key={s.category} score={s} />
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-xs text-muted-foreground">
        Penalties come from deterministic checks: critical −20%, major −10%, minor −3% each, never below 0.
      </p>
    </ReportSection>
  )
}

/** FR-41, §4.7: drift level and the areas the review agent reported. */
export function DriftSection({ report }: { report: Report }) {
  const level = report.driftLevel
  return (
    <ReportSection id="review-drift" title="Drift from the specification">
      <p className="text-sm">
        Drift level:{' '}
        {level ? <Badge tone={driftTones[level]}>{driftLevelLabels[level]}</Badge> : <span className="text-muted-foreground">Not reported</span>}
      </p>
      {report.driftAreas.length === 0 ? (
        <p className="text-sm text-muted-foreground">No drift areas were reported.</p>
      ) : (
        <ul aria-label="Drift areas" className="flex flex-col gap-2">
          {report.driftAreas.map((a, i) => (
            <li key={`${a.area}-${String(i)}`} className="flex flex-col gap-0.5 text-sm">
              <span className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{a.area}</span>
                <Badge tone={severityTones[a.severity]}>{findingSeverityLabels[a.severity]}</Badge>
              </span>
              <span className="text-muted-foreground">{a.description}</span>
            </li>
          ))}
        </ul>
      )}
    </ReportSection>
  )
}

/** FR-41: violations grouped by severity, each with its category, source, file and evidence. */
export function ViolationsSection({ report }: { report: Report }) {
  const groups = findingSeverities
    .map((severity) => ({ severity, items: report.violations.filter((v) => v.severity === severity) }))
    .filter((g) => g.items.length > 0)
  return (
    <ReportSection id="review-violations" title={`Violations (${String(report.violations.length)})`}>
      {groups.length === 0 ? (
        <p className="text-sm text-muted-foreground">No violations were found.</p>
      ) : (
        groups.map(({ severity, items }) => (
          <section key={severity} aria-labelledby={`violations-${severity}`} className="flex flex-col gap-2">
            <h4 id={`violations-${severity}`} className="font-semibold">
              {findingSeverityLabels[severity]} ({plural(items.length, 'violation')})
            </h4>
            <ul className="flex flex-col gap-2">
              {items.map((v, i) => (
                <li key={`${v.message}-${String(i)}`} className="flex flex-col gap-0.5 border-l-2 border-border pl-3 text-sm">
                  <span>{v.message}</span>
                  <span className="flex flex-wrap gap-x-3 text-xs text-muted-foreground">
                    <span>Category: {reviewCategoryLabels[v.category]}</span>
                    <span>Source: {findingSourceLabels[v.source]}</span>
                    {v.file && (
                      <span>
                        File: <code>{v.file}</code>
                      </span>
                    )}
                  </span>
                  {v.evidence && <span className="text-xs">Evidence: {v.evidence}</span>}
                </li>
              ))}
            </ul>
          </section>
        ))
      )}
    </ReportSection>
  )
}

/** FR-38: features from the specification the review agent could not find in the code. */
export function MissingFeaturesSection({ report }: { report: Report }) {
  return (
    <ReportSection id="review-missing" title={`Missing features (${String(report.missingFeatures.length)})`}>
      {report.missingFeatures.length === 0 ? (
        <p className="text-sm text-muted-foreground">Every specified feature was found.</p>
      ) : (
        <ul aria-label="Missing features" className="list-disc pl-5 text-sm">
          {report.missingFeatures.map((f) => (
            <li key={f}>{f}</li>
          ))}
        </ul>
      )}
    </ReportSection>
  )
}

/** "45 s", "4 min", "1 min 30 s". */
function waitedCopy(seconds: number): string {
  if (seconds < 60) {
    return `${String(seconds)} s`
  }
  const minutes = Math.floor(seconds / 60)
  const rest = seconds % 60
  return rest === 0 ? `${String(minutes)} min` : `${String(minutes)} min ${String(rest)} s`
}

/**
 * FR-85: the PR head's check runs and commit statuses as this review cycle saw them (a failed required check is a
 * major testing finding), how long review waited for pending checks, and a warning when it gave up waiting. The
 * preview deployment (FR-89) arrives in a later phase.
 */
/**
 * FR-90: the preview deployment the cycle waited for: its final state, URL, how long review waited and whether it gave
 * up, and the build-log tail. An `error` preview is a critical architecture finding in the report ("Vercel preview
 * build failed"), listed under Violations.
 */
function PreviewCheckBlock({ preview }: { preview: PreviewCheck | null }) {
  return (
    <section aria-labelledby="report-preview-title" data-testid="report-preview" className="flex flex-col gap-2 border-t border-border/60 pt-3">
      <h4 id="report-preview-title" className="flex flex-wrap items-center gap-2 font-semibold">
        Preview deployment
        {preview && <DeploymentStatusPill status={preview.status} />}
      </h4>
      {preview === null ? (
        <p className="text-sm text-muted-foreground">
          No preview deployment was checked for this cycle (deployment isn't set up, or there was no preview of the pull request
          head).
        </p>
      ) : (
        <>
          <p className="text-sm">
            <DeploymentLink url={preview.url} />
          </p>
          {preview.timedOut ? (
            <Alert tone="warning" title="Review stopped waiting for the preview">
              <p>
                The preview was still building after{' '}
                {preview.waitedSeconds !== null ? waitedCopy(preview.waitedSeconds) : '20 minutes'}, so the review scored
                without it.
              </p>
            </Alert>
          ) : (
            preview.waitedSeconds !== null &&
            preview.waitedSeconds > 0 && (
              <p className="text-sm text-muted-foreground">{`Waited ${waitedCopy(preview.waitedSeconds)} for the preview`}</p>
            )
          )}
          {preview.status === 'error' && (
            <Alert tone="error" title="Vercel preview build failed">
              <p>This is a critical architecture finding in this report: the optimisation plan has to fix the build.</p>
            </Alert>
          )}
          <BuildLogTail log={preview.buildLogTail} defaultOpen={preview.status === 'error'} />
        </>
      )}
    </section>
  )
}

export function ExternalChecksSection({ report }: { report: Pick<ComplianceReportDetail, 'externalChecks'> }) {
  const external = report.externalChecks
  const failingRequired = external?.checks.filter((c) => c.required && isFailingCheck(c)) ?? []
  return (
    <ReportSection id="report-external-checks" title="External checks">
      {external === null ? (
        <p className="text-sm text-muted-foreground">
          No external checks were collected for this cycle (no GitHub checks on the pull request, or a Local project).
        </p>
      ) : (
        <>
          {failingRequired.length > 0 && (
            <Alert tone="error" title={`${plural(failingRequired.length, 'required check')} failed`}>
              <p>Each failed required check is a major testing finding in this report.</p>
            </Alert>
          )}
          {external.checksTimedOut && (
            <Alert tone="warning" title="Review stopped waiting for checks">
              <p>
                Some checks were still pending after{' '}
                {external.checksWaitedSeconds !== null ? waitedCopy(external.checksWaitedSeconds) : '15 minutes'}, so the
                review scored without them.
              </p>
            </Alert>
          )}
          {!external.checksTimedOut && external.checksWaitedSeconds !== null && external.checksWaitedSeconds > 0 && (
            <p className="text-sm text-muted-foreground">{`Waited ${waitedCopy(external.checksWaitedSeconds)} for checks`}</p>
          )}
          <CheckRunList checks={external.checks} label="External checks" empty="No checks are reported on the pull request." />
        </>
      )}
      <PreviewCheckBlock preview={external?.preview ?? null} />
    </ReportSection>
  )
}
