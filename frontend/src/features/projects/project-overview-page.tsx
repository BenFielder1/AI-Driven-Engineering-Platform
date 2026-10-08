import type { ReactNode } from 'react'

import { Badge } from '../../components/ui/badge'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { artifactTypeLabels, formatDateTime, gateTypeLabels, phaseLabels } from './labels'
import { useProjectAccess } from './project-context'
import { useProjectState, type ProjectState } from './projects-api'

const artifactStatusLabels: Record<NonNullable<ProjectState['artifacts'][number]['latestStatus']>, string> = {
  draft: 'Draft',
  locked: 'Approved',
  rejected: 'Rejected',
  superseded: 'Superseded',
  invalidated: 'Invalidated',
  corrupted: 'Corrupted',
}

const decisionLabels: Record<NonNullable<ProjectState['review']['latestDecision']>, string> = {
  release_eligible: 'Release eligible',
  review_required: 'Another review cycle needed',
  escalation_required: 'Escalation needed',
}

const driftLabels: Record<NonNullable<ProjectState['review']['driftLevel']>, string> = {
  none: 'None',
  low: 'Low',
  moderate: 'Moderate',
  severe: 'Severe',
}

function Card({ title, children }: { title: string; children: ReactNode }) {
  const id = `card-${title.toLowerCase().replaceAll(' ', '-')}`
  return (
    <section aria-labelledby={id} className="rounded-lg border border-border bg-surface-raised p-4">
      <h2 id={id} className="mb-3 text-lg font-semibold">
        {title}
      </h2>
      {children}
    </section>
  )
}

function Stat({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="flex justify-between gap-3 py-1 text-sm">
      <dt className="text-muted">{term}</dt>
      <dd className="text-right font-medium">{children}</dd>
    </div>
  )
}

/** /projects/:id overview (first version): pending gates, next-action area and state summaries from /state. */
export function ProjectOverviewPage() {
  const { project } = useProjectAccess()
  useDocumentTitle(project.name)
  const state = useProjectState(project.id)

  if (state.isPending) {
    return <LoadingState label="Loading project state…" />
  }
  if (state.isError) {
    return <ErrorState error={state.error} onRetry={() => void state.refetch()} title="The project state couldn't load" />
  }

  const s = state.data
  return (
    <div className="flex flex-col gap-5">
      <section aria-labelledby="next-action-title" className="rounded-lg border border-primary bg-surface p-4">
        <h2 id="next-action-title" className="text-lg font-semibold">
          Next action
        </h2>
        <p className="mt-1 text-sm text-muted">
          The project is in {phaseLabels[s.phase]}
          {s.executionPaused ? ' (execution paused)' : ''}. Lifecycle actions arrive in a later phase.
        </p>
      </section>

      <Card title="Pending gates">
        {s.pendingGates.length === 0 ? (
          <EmptyState title="No gates are waiting for a decision" />
        ) : (
          <ul className="divide-y divide-border" aria-label="Pending gates">
            {s.pendingGates.map((gate) => (
              <li key={gate.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                <span className="font-medium">{gateTypeLabels[gate.type]}</span>
                <span className="flex items-center gap-2 text-sm text-muted">
                  Waiting since <time dateTime={gate.createdAt}>{formatDateTime(gate.createdAt)}</time>
                  {gate.overdue && <Badge tone="warning">Overdue</Badge>}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <div className="grid gap-5 md:grid-cols-3">
        <Card title="Artifacts">
          {s.artifacts.length === 0 ? (
            <p className="text-sm text-muted">No artifacts yet.</p>
          ) : (
            <dl>
              {s.artifacts.map((a) => (
                <Stat key={a.type} term={artifactTypeLabels[a.type]}>
                  {a.latestVersion === null
                    ? 'None yet'
                    : `v${String(a.latestVersion)} · ${a.latestStatus ? artifactStatusLabels[a.latestStatus] : 'Unknown'}`}
                </Stat>
              ))}
            </dl>
          )}
        </Card>
        <Card title="Execution">
          <dl>
            <Stat term="Steps">{s.execution.total}</Stat>
            <Stat term="Completed">{s.execution.completed}</Stat>
            <Stat term="In progress">{s.execution.inProgress}</Stat>
            <Stat term="Pending">{s.execution.pending}</Stat>
            <Stat term="Blocked">{s.execution.blocked}</Stat>
            <Stat term="Rejected">{s.execution.rejected}</Stat>
          </dl>
        </Card>
        <Card title="Review">
          <dl>
            <Stat term="Cycle">
              {s.review.cycle} of {s.review.maxCycles}
            </Stat>
            <Stat term="Latest score">
              {s.review.latestScore === null ? 'Not reviewed yet' : s.review.latestScore.toFixed(2)}
            </Stat>
            <Stat term="Threshold">{project.complianceThreshold.toFixed(2)}</Stat>
            <Stat term="Decision">{s.review.latestDecision ? decisionLabels[s.review.latestDecision] : '—'}</Stat>
            <Stat term="Drift">{s.review.driftLevel ? driftLabels[s.review.driftLevel] : '—'}</Stat>
          </dl>
        </Card>
      </div>
    </div>
  )
}

/** Sections that arrive in later phases; the routes exist so project links work. */
export function ProjectPlaceholderPage({ title }: { title: string }) {
  const { project } = useProjectAccess()
  useDocumentTitle(`${title} · ${project.name}`)
  return (
    <section aria-labelledby="placeholder-title">
      <h2 id="placeholder-title" className="mb-3 text-xl font-semibold">
        {title}
      </h2>
      <EmptyState title="Coming in a later phase">This part of the project isn&apos;t built yet.</EmptyState>
    </section>
  )
}
