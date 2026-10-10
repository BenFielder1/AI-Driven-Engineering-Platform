import { useState, type ReactNode } from 'react'

import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { Tabs, type TabItem } from '../../components/ui/tabs'
import { canInProject } from '../../lib/permissions'
import { useDocumentTitle } from '../../lib/use-document-title'
import { ActivityFeed } from '../activity/activity-feed'
import { GateHistory } from '../gates/gate-history'
import { PendingGatesPanel } from '../gates/pending-gates-panel'
import { LifecycleCard } from '../lifecycle/lifecycle-card'
import { ReleasePanel } from '../release/release-panel'
import { TokenBudget } from '../runs/token-budget'
import { artifactStatusLabels, artifactTypeLabels } from './labels'
import { useProjectAccess } from './project-context'
import { useProjectState, type ProjectState } from './projects-api'

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

const historyTabs = [
  { id: 'activity', label: 'Activity' },
  { id: 'gates', label: 'Gate history' },
] as const satisfies readonly TabItem<string>[]

type HistoryTab = (typeof historyTabs)[number]['id']

/**
 * /projects/:id overview (SPEC §7): lifecycle timeline with the advance action (FR-11, FR-12), pending gates with
 * Owner decisions (FR-13–FR-15), state summaries from /state, tokens used against the budget (FR-26), the release (FR-42, FR-43), and the activity feed and gate history (FR-50).
 */
export function ProjectOverviewPage() {
  const { project, user, role } = useProjectAccess()
  useDocumentTitle(project.name)
  const state = useProjectState(project.id)
  const [historyTab, setHistoryTab] = useState<HistoryTab>('activity')

  if (state.isPending) {
    return <LoadingState label="Loading project state…" />
  }
  if (state.isError) {
    return <ErrorState error={state.error} onRetry={() => void state.refetch()} title="The project state couldn't load" />
  }

  const s = state.data
  return (
    <div className="flex flex-col gap-5">
      <LifecycleCard projectId={project.id} state={s} canAdvance={canInProject(user, role, 'advancePhase')} />

      <PendingGatesPanel
        projectId={project.id}
        canDecide={canInProject(user, role, 'decideGates')}
        currentVersion={s.projectVersion}
      />

      {(s.phase === 'release' || s.latestRelease !== null) && (
        <ReleasePanel projectId={project.id} state={s} canRelease={canInProject(user, role, 'requestRelease')} />
      )}

      <div className="grid gap-5 md:grid-cols-2 xl:grid-cols-4">
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
        <Card title="Token budget">
          <TokenBudget
            projectId={project.id}
            tokensUsed={s.tokensUsed}
            tokenBudget={s.tokenBudget}
            canRaise={canInProject(user, role, 'changeSettings')}
          />
        </Card>
      </div>

      <section aria-labelledby="history-title" className="rounded-lg border border-border bg-surface-raised p-4">
        <h2 id="history-title" className="mb-3 text-lg font-semibold">
          History
        </h2>
        <Tabs label="History" tabs={historyTabs} selected={historyTab} onSelect={setHistoryTab}>
          {historyTab === 'activity' ? <ActivityFeed projectId={project.id} /> : <GateHistory projectId={project.id} />}
        </Tabs>
      </section>
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
