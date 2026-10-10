import { Link } from '@tanstack/react-router'

import { useDocumentTitle } from '../../lib/use-document-title'
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
        {adminSections.map((s) => (
          <li key={s.to} className="rounded-lg border border-border bg-surface-raised p-4">
            <h3 className="text-lg font-semibold">
              <Link to={s.to} className="text-link underline underline-offset-4">
                {s.label}
              </Link>
            </h3>
            <p className="mt-1 text-sm text-muted">{s.description}</p>
          </li>
        ))}
      </ul>
    </section>
  )
}
