import { panelClass } from '../../../components/shared/panel-class'
import { buttonClasses } from '../../../components/ui/button-classes'
import { cn } from '../../../lib/utils'
import { FaqAccordion } from '../components/faq-accordion'
import { PageIntro } from '../components/page-intro'
import { SiteLink } from '../components/site-link'
import { faqHeadings, pageTitles } from '../content/headings'
import { faqHelpLinks, faqs } from '../content/faq'
import { marketingPages } from '../content/pages'

const page = marketingPages['/faq']

/** /faq (FR-218): the accordion, then "Still have questions?" → the docs and GitHub issues. */
export function FaqPage() {
  return (
    <>
      <PageIntro eyebrow={page.name} title={pageTitles.faq}>
        {page.description}
      </PageIntro>
      <div className="mx-auto max-w-3xl px-4 pb-20">
        <FaqAccordion items={faqs} anchored />
        <section aria-labelledby="faq-more" className={cn(panelClass, 'mt-10 p-6')} data-testid="faq-more">
          <h2 id="faq-more" className="text-lg font-semibold">
            {faqHeadings.more.title}
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">{faqHeadings.more.lead}</p>
          <div className="mt-4 flex flex-wrap gap-3">
            {faqHelpLinks.map((link, i) => (
              <SiteLink key={link.href} link={link} className={buttonClasses(i === 0 ? 'primary' : 'secondary')} />
            ))}
          </div>
        </section>
      </div>
    </>
  )
}
