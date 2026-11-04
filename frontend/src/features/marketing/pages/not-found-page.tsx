'use client'

import { buttonClasses } from '../../../components/ui/button-classes'
import { Link } from '../../../lib/navigation'
import { docsLink } from '../content/site-nav'
import { SiteLink } from '../components/site-link'

/** The marketing 404 (SPEC §7): inside the marketing shell, linking Home and the docs. */
export function MarketingNotFoundPage() {
  return (
    <section className="mx-auto max-w-6xl px-4 py-24">
      <p className="eyebrow text-primary">404</p>
      <h1 className="mt-3 text-3xl font-semibold tracking-tight sm:text-5xl">Page not found</h1>
      <p className="mt-4 max-w-xl text-lg text-muted-foreground">The page you&apos;re looking for doesn&apos;t exist.</p>
      <div className="mt-8 flex flex-wrap gap-3">
        <Link to="/" className={buttonClasses('primary')}>
          Go to the home page
        </Link>
        <SiteLink link={docsLink} className={buttonClasses('secondary')}>
          Read the docs
        </SiteLink>
      </div>
    </section>
  )
}
