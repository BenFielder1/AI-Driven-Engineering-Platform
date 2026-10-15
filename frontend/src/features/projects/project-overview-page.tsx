import { Link } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'

import { EmptyState as SharedEmptyState } from '../../components/shared/empty-state'
import { MetricCard } from '../../components/shared/metric-card'
import { panelClass } from '../../components/shared/panel-class'
import { SkeletonCards, SkeletonPage } from '../../components/shared/skeletons'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/radix-tabs'
import { ErrorState } from '../../components/ui/states'
import { canInProject } from '../../lib/permissions'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { ActivityFeed } from '../activity/activity-feed'
import { GateHistory } from '../gates/gate-history'
import { PendingGatesPanel } from '../gates/pending-gates-panel'
import { LifecycleCard } from '../lifecycle/lifecycle-card'
import { nextAction } from '../lifecycle/next-action'
import { ReleasePanel } from '../release/release-panel'
import { TokenBudget } from '../runs/token-budget'
import {
  artifactStatusLabels,
  artifactTypeLabels,
  formatNumber,
  formatPercent,
} from './labels'
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

function Card({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  const id = `card-${title.toLowerCase().replaceAll(' ', '-')}`
  return (
    <section aria-labelledby={id} className={cn(panelClass, 'p-4')}>
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 id={id} className="text-base">
          {title}
        </h2>
        {action}
      </div>
      {children}
    </section>
  )
}

function Stat({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="flex justify-between gap-3 py-1 text-sm">
      <dt className="text-muted-foreground">{term}</dt>
      <dd className="text-right font-medium">{children}</dd>
    </div>
  )
}

type HistoryTab = 'activity' | 'gates'

/**
 * /projects/:id overview (SPEC §7.2): the next-action card (FR-54; the stage stepper is in the project header), KPI
 * tiles, pending gates with Owner decisions (FR-13–FR-15), the release (FR-42, FR-43), artifacts and review summaries,
 * the token budget (FR-26), and the activity preview and gate history (FR-50).
 */
export function ProjectOverviewPage() {
  const { project, user, role } = useProjectAccess()
  useDocumentTitle(project.name)
  const state = useProjectState(project.id)
  const [historyTab, setHistoryTab] = useState<HistoryTab>('activity')

  if (state.isPending) {
    return (
      <div className="flex flex-col gap-5">
        <SkeletonCards />
        <SkeletonPage />
      </div>
    )
  }
  if (state.isError) {
    return <ErrorState error={state.error} onRetry={() => void state.refetch()} title="The project state couldn't load" />
  }

  const s = state.data
  const can = (c: Parameters<typeof canInProject>[2]) => canInProject(user, role, c)
  const { review, execution } = s
  return (
    <div className="flex flex-col gap-5">
      <LifecycleCard projectId={project.id} state={s} canAdvance={can('advancePhase')} action={nextAction(s, can)} />

      <section aria-label="Key figures" className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <MetricCard
          label="Project version"
          value={String(s.projectVersion)}
          footer="Goes up on every phase change and release."
        />
        <MetricCard
          label="Latest compliance"
          value={review.latestScore === null ? '—' : formatPercent(review.latestScore)}
          footer={
            review.latestScore === null
              ? `Not reviewed yet · threshold ${formatPercent(project.complianceThreshold)}`
              : `Threshold ${formatPercent(project.complianceThreshold)} · cycle ${String(review.cycle)} of ${String(review.maxCycles)}`
          }
        />
        <MetricCard
          label="Steps done"
          value={`${String(execution.completed)} / ${String(execution.total)}`}
          footer={`${String(execution.inProgress)} in progress · ${String(execution.pending)} pending · ${String(execution.blocked)} blocked · ${String(execution.rejected)} rejected`}
        />
        <MetricCard
          label="Tokens used"
          value={formatNumber(s.tokensUsed)}
          footer={s.tokenBudget === null ? 'No budget' : `of ${formatNumber(s.tokenBudget)} budget`}
        />
      </section>

      <PendingGatesPanel projectId={project.id} canDecide={can('decideGates')} currentVersion={s.projectVersion} />

      {(s.phase === 'release' || s.latestRelease !== null) && (
        <ReleasePanel projectId={project.id} state={s} canRelease={can('requestRelease')} />
      )}

      <div className="grid gap-5 lg:grid-cols-3">
        <section aria-labelledby="history-title" className={cn(panelClass, 'p-4 lg:col-span-2')}>
          <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
            <h2 id="history-title" className="text-base">
              History
            </h2>
            <Link
              to="/projects/$projectId/activity"
              params={{ projectId: project.id }}
              className="text-sm font-medium text-primary underline-offset-4 hover:underline"
            >
              All activity
            </Link>
          </div>
          <Tabs
            value={historyTab}
            onValueChange={(v) => {
              setHistoryTab(v === 'gates' ? 'gates' : 'activity')
            }}
          >
            <TabsList aria-label="History">
              <TabsTrigger value="activity">Activity</TabsTrigger>
              <TabsTrigger value="gates">Gate history</TabsTrigger>
            </TabsList>
            <TabsContent value="activity">
              <ActivityFeed projectId={project.id} />
            </TabsContent>
            <TabsContent value="gates">
              <GateHistory projectId={project.id} />
            </TabsContent>
          </Tabs>
        </section>

        <div className="flex flex-col gap-5">
          <Card
            title="Artifacts"
            action={
              <Link
                to="/projects/$projectId/artifacts"
                params={{ projectId: project.id }}
                className="text-sm font-medium text-primary underline-offset-4 hover:underline"
              >
                View all
              </Link>
            }
          >
            {s.artifacts.length === 0 ? (
              <p className="text-sm text-muted-foreground">No artifacts yet.</p>
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
          <Card title="Review">
            <dl>
              <Stat term="Cycle">
                {review.cycle} of {review.maxCycles}
              </Stat>
              <Stat term="Latest score">{review.latestScore === null ? 'Not reviewed yet' : review.latestScore.toFixed(2)}</Stat>
              <Stat term="Threshold">{project.complianceThreshold.toFixed(2)}</Stat>
              <Stat term="Decision">{review.latestDecision ? decisionLabels[review.latestDecision] : '—'}</Stat>
              <Stat term="Drift">{review.driftLevel ? driftLabels[review.driftLevel] : '—'}</Stat>
            </dl>
          </Card>
          <Card title="Token budget">
            <TokenBudget
              projectId={project.id}
              tokensUsed={s.tokensUsed}
              tokenBudget={s.tokenBudget}
              canRaise={can('changeSettings')}
            />
          </Card>
        </div>
      </div>
    </div>
  )
}

/** Sections that arrive in later phases; the routes exist so project links work. */
export function ProjectPlaceholderPage({ title }: { title: string }) {
  const { project } = useProjectAccess()
  useDocumentTitle(`${title} · ${project.name}`)
  return <SharedEmptyState variant="not_started" title={title} description="This part of the project isn’t built yet." />
}
