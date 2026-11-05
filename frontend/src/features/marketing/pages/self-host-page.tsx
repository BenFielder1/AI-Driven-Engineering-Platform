import { Check } from 'lucide-react'

import { panelClass } from '../../../components/shared/panel-class'
import { StatusBadge } from '../../../components/shared/status-badge'
import { cn } from '../../../lib/utils'
import { CommandBlock } from '../components/command-block'
import { FaqAccordion } from '../components/faq-accordion'
import { PageIntro } from '../components/page-intro'
import { Section } from '../components/section'
import { SiteLink } from '../components/site-link'
import { pageTitles, selfHostHeadings } from '../content/headings'
import { marketingPages } from '../content/pages'
import { configureItems, pathSummaries, PREVIEW_NOTE, selfHostFaqs, selfHostRequirements } from '../content/self-host'
import { selfHostPaths } from '../content/self-host-commands'

const page = marketingPages['/self-host']

const docsLinkClass = 'inline-flex min-h-6 items-center text-sm font-medium text-primary underline-offset-4 hover:underline'

/**
 * /self-host (FR-216): "Free while in preview", requirements, the three install paths with their commands (from
 * self-host-commands.ts) and docs links, what you configure, and a FAQ on upgrades and data.
 */
export function SelfHostPage() {
  return (
    <>
      <PageIntro eyebrow={page.name} title={pageTitles.selfHost}>
        {page.description}
      </PageIntro>
      <div className="mx-auto max-w-6xl px-4">
        <div className={cn(panelClass, 'flex flex-col gap-4 p-6 sm:flex-row sm:items-center sm:justify-between')} data-testid="preview-note">
          <p className="flex flex-wrap items-center gap-3 text-lg font-semibold">
            <StatusBadge tone="info">Preview</StatusBadge>
            {PREVIEW_NOTE}
          </p>
          <div>
            <h2 className="text-sm font-semibold">{selfHostHeadings.requirements}</h2>
            <ul className="mt-2 flex flex-col gap-1 text-sm text-muted-foreground" data-testid="requirements">
              {selfHostRequirements.map((r) => (
                <li key={r} className="flex items-center gap-2">
                  <Check aria-hidden="true" className="size-4 text-success" />
                  {r}
                </li>
              ))}
            </ul>
          </div>
        </div>
      </div>
      <Section id="install" {...selfHostHeadings.install}>
        {/* §8: one column that can shrink below the commands' width (they scroll inside their block) at 375 px. */}
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
          {selfHostPaths.map((path) => (
            <article key={path.id} id={path.id} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-6')} aria-labelledby={`path-${path.id}`}>
              <h3 id={`path-${path.id}`} className="text-lg font-semibold">
                {path.title}
              </h3>
              <p className="text-sm text-muted-foreground">{pathSummaries[path.id]}</p>
              <CommandBlock title={path.title} commands={path.commands} />
              <SiteLink link={{ label: `${path.title} docs`, href: path.docsHref, external: true }} className={docsLinkClass} />
            </article>
          ))}
        </div>
      </Section>
      <Section id="configure" {...selfHostHeadings.configure}>
        <ul className="grid gap-4 sm:grid-cols-2">
          {configureItems.map((item) => (
            <li key={item.title} className={cn(panelClass, 'p-6')}>
              <h3 className="text-base font-semibold">{item.title}</h3>
              <p className="mt-2 text-sm text-muted-foreground">{item.body}</p>
              <p className="mt-3">
                <SiteLink link={item.link} className={docsLinkClass} />
              </p>
            </li>
          ))}
        </ul>
      </Section>
      <Section id="upgrades-and-data" {...selfHostHeadings.faq} className="max-w-3xl">
        <FaqAccordion items={selfHostFaqs} headingLevel="h3" anchored />
      </Section>
    </>
  )
}
