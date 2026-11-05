import { BrandLockup } from '../../../components/brand'
import { ThemeSelector } from '../../../components/ui/theme-selector'
import { TAGLINE } from '../content/pages'
import { footerColumns } from '../content/site-nav'
import { SiteLink } from './site-link'

const footerLinkClass = 'inline-flex min-h-6 items-center text-sm text-muted-foreground transition-colors hover:text-foreground'

/** FR-210: footer columns (Product, Developers, Self-host, Trust), then © year "Tevrin" and the theme selector (FR-204). */
export function MarketingFooter() {
  return (
    <footer data-testid="marketing-footer" className="border-t border-border">
      <div className="mx-auto max-w-6xl px-4 py-12">
        <div className="grid grid-cols-2 gap-8 md:grid-cols-5">
          <div className="col-span-2 md:col-span-1">
            <BrandLockup />
            <p className="mt-3 max-w-xs text-sm text-muted-foreground">{TAGLINE}</p>
          </div>
          <nav aria-label="Footer" className="col-span-2 grid grid-cols-2 gap-8 sm:grid-cols-4 md:col-span-4">
            {footerColumns.map((column) => (
              <div key={column.title}>
                <h2 className="text-sm font-semibold text-foreground">{column.title}</h2>
                <ul className="mt-3 flex flex-col gap-2">
                  {column.links.map((link) => (
                    <li key={link.label}>
                      <SiteLink link={link} className={footerLinkClass} />
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </nav>
        </div>
        <div className="mt-10 flex flex-wrap items-center justify-between gap-4 border-t border-border pt-6">
          <p className="text-sm text-muted-foreground">
            © <span suppressHydrationWarning>{new Date().getFullYear()}</span> Tevrin
          </p>
          <ThemeSelector compact />
        </div>
      </div>
    </footer>
  )
}
