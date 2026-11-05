import { GitCompare, ShieldCheck, Undo2, type LucideIcon } from 'lucide-react'
import { Fragment } from 'react'

import { panelClass } from '../../../../components/shared/panel-class'
import { cn } from '../../../../lib/utils'
import { homeHeadings } from '../../content/headings'
import { whyCards, worksWithNote, type WhyCard } from '../../content/home'
import { phaseGates } from '../../content/how-it-works'
import { worksWith } from '../../content/integrations'
import { lifecyclePhases } from '../../content/lifecycle'
import { IntegrationMark } from '../integration-mark'
import { Section } from '../section'
import { SiteLink } from '../site-link'

/** FR-212 §2: "Works with" (not customers): names with monochrome marks. */
export function WorksWithStrip() {
  return (
    <section aria-labelledby="works-with" className="border-y border-border bg-muted/40" data-testid="works-with">
      <div className="mx-auto max-w-6xl px-4 py-8">
        <h2 id="works-with" className="eyebrow text-center text-muted-foreground">
          {homeHeadings.worksWith.title}
        </h2>
        <ul className="mt-5 flex flex-wrap justify-center gap-x-6 gap-y-3">
          {worksWith.map((item) => (
            <li key={item.name} className="flex items-center gap-2 text-sm text-muted-foreground">
              <IntegrationMark icon={item.icon} className="size-4" />
              {item.name}
            </li>
          ))}
        </ul>
        <p className="mt-4 text-center text-xs text-muted-foreground">
          {worksWithNote}{' '}
          <SiteLink link={{ label: 'See all integrations', href: '/integrations' }} className="font-medium text-primary underline-offset-4 hover:underline" />
        </p>
      </div>
    </section>
  )
}

/** FR-212 §3: the six phases as numbered steps (plus optional Analysis), with the gate between each. */
export function LifecycleSteps() {
  return (
    <Section {...homeHeadings.lifecycle} testId="home-lifecycle">
      <p className="mb-4 text-sm text-muted-foreground">
        {homeHeadings.analysisNote}
      </p>
      <ol className="grid gap-3 lg:grid-cols-[repeat(5,auto_minmax(0,1fr))_auto] lg:items-center">
        {lifecyclePhases.map((phase, i) => (
          <Fragment key={phase}>
            <li className={cn(panelClass, 'flex items-center gap-3 px-4 py-3')}>
              <span className="flex size-7 shrink-0 items-center justify-center rounded-full bg-primary font-mono text-xs font-semibold text-primary-foreground">
                {i + 1}
              </span>
              <span className="text-sm font-medium">{phase}</span>
            </li>
            {i < phaseGates.length && (
              <li className="flex items-center gap-2 pl-5 text-xs text-muted-foreground lg:flex-col lg:pl-0 lg:text-center" data-testid="lifecycle-gate">
                <span aria-hidden="true" className="size-2.5 rotate-45 bg-ring" />
                <span>
                  <span className="sr-only">Gate: </span>
                  {phaseGates[i]}
                </span>
              </li>
            )}
          </Fragment>
        ))}
      </ol>
      <p className="mt-6 text-sm">
        <SiteLink link={{ label: homeHeadings.lifecycleLink, href: '/how-it-works' }} className="font-medium text-primary underline-offset-4 hover:underline" />
      </p>
    </Section>
  )
}

const whyIcons: Record<WhyCard['icon'], LucideIcon> = { gate: ShieldCheck, versions: GitCompare, undo: Undo2 }

/** FR-212 §4: Governed, Versioned, Reversible. */
export function WhyTevrin() {
  return (
    <Section {...homeHeadings.why} testId="home-why">
      <ul className="grid gap-4 md:grid-cols-3">
        {whyCards.map((card) => {
          const Icon = whyIcons[card.icon]
          return (
            <li key={card.title} className={cn(panelClass, 'p-6')}>
              <Icon aria-hidden="true" className="size-6 text-primary" />
              <h3 className="mt-4 text-lg font-semibold">{card.title}</h3>
              <p className="mt-2 text-sm text-muted-foreground">{card.body}</p>
            </li>
          )
        })}
      </ul>
    </Section>
  )
}
