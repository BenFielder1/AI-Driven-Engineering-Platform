import { panelClass } from '../../../components/shared/panel-class'
import { cn } from '../../../lib/utils'
import { IntegrationMark } from '../components/integration-mark'
import { PageIntro } from '../components/page-intro'
import { Section } from '../components/section'
import { SiteLink } from '../components/site-link'
import { integrationGroups } from '../content/integrations'
import { pageTitles } from '../content/headings'
import { marketingPages } from '../content/pages'

const page = marketingPages['/integrations']

/** /integrations (FR-215): grouped cards, one line each on what Tevrin does with the tool. */
export function IntegrationsPage() {
  return (
    <>
      <PageIntro eyebrow={page.name} title={pageTitles.integrations}>
        {page.description}
      </PageIntro>
      {integrationGroups.map((group) => (
        <Section key={group.id} id={group.id} title={group.title} lead={group.summary} className="py-10 sm:py-12">
          <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {group.items.map((item) => (
              <li key={item.name} className={cn(panelClass, 'flex flex-col gap-2 p-5')} data-testid="integration-card">
                <h3 className="flex items-center gap-2 text-base font-semibold">
                  <IntegrationMark icon={item.icon} />
                  {item.name}
                </h3>
                <p className="text-sm text-muted-foreground">{item.does}</p>
              </li>
            ))}
          </ul>
          <p className="mt-4 text-sm">
            <SiteLink
              link={{ label: `${group.title} guide`, href: group.docsHref, external: true }}
              className="inline-flex min-h-6 items-center font-medium text-primary underline-offset-4 hover:underline"
            />
          </p>
        </Section>
      ))}
    </>
  )
}
