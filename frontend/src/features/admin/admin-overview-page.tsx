import { Link } from '@tanstack/react-router'

import { panelClass } from '../../components/shared/panel-class'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { adminSections } from './admin-sections'

/** /admin (FR-51, FR-3): what each admin section is for, with a link to it. */
export function AdminOverviewPage() {
  useDocumentTitle('Admin')
  return (
    <section aria-labelledby="admin-overview-title">
      <h2 id="admin-overview-title" className="sr-only">
        Admin sections
      </h2>
      <ul className="grid gap-4 sm:grid-cols-2">
        {adminSections
          .filter((s) => s.key !== 'admin-overview')
          .map((s) => (
          <li key={s.key} className={cn(panelClass, 'relative p-4 transition-colors focus-within:ring-[3px] focus-within:ring-ring/50 hover:bg-accent/40')}>
            <h3 className="text-base">
              <Link to={s.to} className="text-primary underline-offset-4 outline-none after:absolute after:inset-0 hover:underline">
                {s.label}
              </Link>
            </h3>
            <p className="mt-1 text-sm text-muted-foreground">{s.description}</p>
          </li>
        ))}
      </ul>
    </section>
  )
}
