import { ArrowRight, Bot, Check, Cloud, GitCompare, GitPullRequest, ListTodo, ShieldCheck, type LucideIcon } from 'lucide-react'

import { panelClass } from '../../../../components/shared/panel-class'
import { Link } from '../../../../lib/navigation'
import { cn } from '../../../../lib/utils'
import { changelog } from '../../content/changelog'
import { homeFaqs } from '../../content/faq'
import { homeHeadings } from '../../content/headings'
import { bentoTiles, closingCta, type BentoTile } from '../../content/home'
import { selfHostRequirements } from '../../content/self-host'
import { selfHostPaths } from '../../content/self-host-commands'
import { ChangelogEntryCard } from '../changelog-entry'
import { CommandBlock } from '../command-block'
import { FaqAccordion } from '../faq-accordion'
import { Section } from '../section'
import { HeroButtons } from './home-hero'

const bentoIcons: Record<BentoTile['icon'], LucideIcon> = {
  gate: ShieldCheck,
  diff: GitCompare,
  bot: Bot,
  git: GitPullRequest,
  tracker: ListTodo,
  cloud: Cloud,
}

const textLink = 'inline-flex min-h-6 items-center gap-1 text-sm font-medium text-primary underline-offset-4 hover:underline'

/** FR-212 §5: six tiles, each linking to its Product section. */
export function FeatureBento() {
  return (
    <Section {...homeHeadings.bento} testId="home-bento">
      <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {bentoTiles.map((tile, i) => {
          const Icon = bentoIcons[tile.icon]
          return (
            <li key={tile.anchor} className={cn(i === 0 || i === 5 ? 'lg:col-span-2' : '')}>
              <Link
                to="/product"
                hash={tile.anchor}
                className={cn(panelClass, 'group flex h-full flex-col p-6 transition-colors hover:border-primary/50')}
                data-testid="bento-tile"
              >
                <Icon aria-hidden="true" className="size-6 text-primary" />
                <h3 className="mt-4 text-lg font-semibold">{tile.title}</h3>
                <p className="mt-2 flex-1 text-sm text-muted-foreground">{tile.body}</p>
                <span className="mt-4 inline-flex items-center gap-1 text-sm font-medium text-primary">
                  Learn more <ArrowRight aria-hidden="true" className="size-4" />
                </span>
              </Link>
            </li>
          )
        })}
      </ul>
    </Section>
  )
}

/** FR-212 §6: requirements and a copyable command block (the first path's commands), linking to Self-host. */
export function SelfHostTeaser() {
  const railway = selfHostPaths[0]
  return (
    <Section {...homeHeadings.selfHost} testId="home-self-host">
      <div className="grid items-start gap-8 lg:grid-cols-2">
        <div>
          <h3 className="text-sm font-semibold">{homeHeadings.selfHostNeed}</h3>
          <ul className="mt-3 flex flex-col gap-2 text-sm">
            {selfHostRequirements.map((r) => (
              <li key={r} className="flex items-center gap-2">
                <Check aria-hidden="true" className="size-4 text-success" />
                {r}
              </li>
            ))}
          </ul>
          <Link to="/self-host" className={cn(textLink, 'mt-6')}>
            {homeHeadings.selfHostLink} <ArrowRight aria-hidden="true" className="size-4" />
          </Link>
        </div>
        {railway && <CommandBlock title={railway.title} commands={railway.commands} />}
      </div>
    </Section>
  )
}

/** FR-212 §7: the two newest Changelog entries. */
export function LatestChanges() {
  return (
    <Section {...homeHeadings.changelog} testId="home-changelog">
      <div className="grid gap-4 md:grid-cols-2">
        {changelog.slice(0, 2).map((entry) => (
          <ChangelogEntryCard key={entry.version} entry={entry} headingLevel="h3" compact />
        ))}
      </div>
      <Link to="/changelog" className={cn(textLink, 'mt-6')}>
        {homeHeadings.changelogLink} <ArrowRight aria-hidden="true" className="size-4" />
      </Link>
    </Section>
  )
}

/** FR-212 §8: the first five FAQ questions, linking to /faq. */
export function HomeFaq() {
  return (
    <Section {...homeHeadings.faq} className="max-w-3xl" testId="home-faq">
      <FaqAccordion items={homeFaqs} headingLevel="h3" />
      <Link to="/faq" className={cn(textLink, 'mt-6')}>
        {homeHeadings.faqLink} <ArrowRight aria-hidden="true" className="size-4" />
      </Link>
    </Section>
  )
}

/** FR-212 §9: the closing call to action, repeating the hero buttons. */
export function ClosingCta() {
  return (
    <section aria-labelledby="closing-cta" className="mx-auto max-w-6xl px-4 pb-20" data-testid="home-cta">
      <div className={cn(panelClass, 'relative isolate overflow-hidden px-6 py-12 text-center sm:px-12')}>
        <div aria-hidden="true" className="bg-brand-gradient pointer-events-none absolute inset-x-0 -bottom-32 -z-10 mx-auto h-48 max-w-xl rounded-full opacity-15 blur-3xl" />
        <h2 id="closing-cta" className="text-2xl font-semibold tracking-tight text-balance sm:text-3xl">
          {closingCta.title}
        </h2>
        <p className="mx-auto mt-3 max-w-xl text-muted-foreground">{closingCta.lead}</p>
        <div className="mt-8 flex justify-center">
          <HeroButtons />
        </div>
      </div>
    </section>
  )
}

