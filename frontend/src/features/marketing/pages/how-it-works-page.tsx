import { Check, Minus } from 'lucide-react'

import { panelClass } from '../../../components/shared/panel-class'
import { StatusBadge } from '../../../components/shared/status-badge'
import { cn } from '../../../lib/utils'
import { AnimatedLifecycleDiagram } from '../components/animated-lifecycle-diagram'
import { howItWorksHeadings, pageTitles } from '../content/headings'
import { PageIntro } from '../components/page-intro'
import { Section } from '../components/section'
import {
  approvalModes,
  approvalModesNote,
  phaseDetails,
  reviewRules,
  rollbackCopy,
  versionsCopy,
  type PhaseDetail,
} from '../content/how-it-works'
import { marketingPages } from '../content/pages'

const page = marketingPages['/how-it-works']

function PhaseCard({ phase, index }: { phase: PhaseDetail; index: number }) {
  const rows: [string, string][] = [
    ['Input', phase.input],
    ['Agent', phase.agent],
    ['Artifact', phase.artifact],
    ['Gate', phase.gate],
  ]
  return (
    <li id={phase.id} className={cn(panelClass, 'scroll-mt-20 p-6')} data-testid="phase-card">
      <h3 className="flex flex-wrap items-center gap-3 text-lg font-semibold">
        <span className="font-mono text-sm text-primary">{phase.optional ? '0' : String(index)}</span>
        {phase.name}
        {phase.optional && <StatusBadge tone="neutral">Optional</StatusBadge>}
      </h3>
      <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-[7rem_1fr]">
        {rows.map(([term, value]) => (
          <div key={term} className="contents">
            <dt className="eyebrow pt-0.5 text-muted-foreground">{term}</dt>
            <dd className="text-foreground">{value}</dd>
          </div>
        ))}
      </dl>
    </li>
  )
}

function ModesTable() {
  const yes = (
    <>
      <Check aria-hidden="true" className="mx-auto size-4 text-success" />
      <span className="sr-only">Gate</span>
    </>
  )
  const no = (
    <>
      <Minus aria-hidden="true" className="mx-auto size-4 text-muted-foreground" />
      <span className="sr-only">No gate</span>
    </>
  )
  return (
    // §8: at 375 px the table scrolls inside this focusable, labelled region. `relative` keeps the sr-only cell text
    // (absolutely positioned) inside the scroller; without it, it widened the page.
    <div
      className={cn(panelClass, 'relative overflow-x-auto')}
      tabIndex={0}
      role="region"
      aria-label={howItWorksHeadings.modesCaption}
      data-testid="modes-table-scroller"
    >
      <table className="w-full min-w-[32rem] text-left text-sm">
        <caption className="sr-only">{howItWorksHeadings.modesCaption}</caption>
        <thead className="border-b border-border text-muted-foreground">
          <tr>
            <th scope="col" className="px-4 py-3 font-medium">Mode</th>
            <th scope="col" className="px-4 py-3 text-center font-medium">Spec approval</th>
            <th scope="col" className="px-4 py-3 text-center font-medium">Plan approval</th>
            <th scope="col" className="px-4 py-3 text-center font-medium">Pre-release</th>
          </tr>
        </thead>
        <tbody>
          {approvalModes.map((mode) => (
            <tr key={mode.id} className="border-b border-border last:border-b-0">
              <th scope="row" className="px-4 py-3 align-top font-medium">
                {mode.name}
                <p className="mt-1 font-normal text-muted-foreground">{mode.summary}</p>
              </th>
              <td className="px-4 py-3 text-center">{mode.spec ? yes : no}</td>
              <td className="px-4 py-3 text-center">{mode.plan ? yes : no}</td>
              <td className="px-4 py-3 text-center">{yes}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Paragraphs({ items }: { items: readonly string[] }) {
  return (
    <div className="flex max-w-3xl flex-col gap-3 text-muted-foreground">
      {items.map((p) => (
        <p key={p}>{p}</p>
      ))}
    </div>
  )
}

/** /how-it-works (FR-214): phases (input, agent, artifact, gate), approval modes, review cycles, versions, rollback. */
export function HowItWorksPage() {
  return (
    <>
      <div className="mx-auto grid max-w-6xl items-end gap-8 lg:grid-cols-[1fr_auto]">
        <PageIntro eyebrow={page.name} title={pageTitles.howItWorks}>
          {page.description}
        </PageIntro>
        <AnimatedLifecycleDiagram className="px-4 pb-12" />
      </div>
      <Section id="phases" {...howItWorksHeadings.phases}>
        <ol className="flex flex-col gap-4">
          {phaseDetails.map((phase, i) => (
            <PhaseCard key={phase.id} phase={phase} index={phase.optional ? 0 : i} />
          ))}
        </ol>
      </Section>
      <Section id="approval-modes" {...howItWorksHeadings.approvalModes} lead={approvalModesNote}>
        <ModesTable />
      </Section>
      <Section id="review-cycles" {...howItWorksHeadings.reviewCycles}>
        <ol className="flex max-w-3xl list-decimal flex-col gap-2 pl-5 text-muted-foreground marker:text-primary">
          {reviewRules.map((r) => (
            <li key={r}>{r}</li>
          ))}
        </ol>
      </Section>
      <Section id="versions" {...howItWorksHeadings.versions}>
        <Paragraphs items={versionsCopy} />
      </Section>
      <Section id="rollback" {...howItWorksHeadings.rollback}>
        <Paragraphs items={rollbackCopy} />
      </Section>
    </>
  )
}
