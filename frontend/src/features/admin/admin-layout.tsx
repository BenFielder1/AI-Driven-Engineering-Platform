import type { ReactNode } from 'react'

import { ForbiddenState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { canViewAdmin } from '../../lib/permissions'
import { useCurrentUser } from '../auth/auth-api'
import { adminSections } from './admin-sections'

const linkClass =
  'inline-flex min-h-11 items-center rounded-full border px-4 text-sm font-medium whitespace-nowrap transition-colors outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50'
const activeLinkClass = 'border-primary/50 bg-primary/10 text-foreground'
const inactiveLinkClass = 'border-border/60 text-muted-foreground hover:bg-accent hover:text-foreground'

/**
 * /admin/* (FR-51, FR-3): a route guard that shows the forbidden view to non-admins (no admin request is made), then the
 * admin heading, the section nav and the section's page.
 */
export function AdminLayout({ children }: { children: ReactNode }) {
  const user = useCurrentUser()
  if (!canViewAdmin(user)) {
    return <ForbiddenState>The admin area is only available to administrators.</ForbiddenState>
  }
  return (
    <>
      {/* Not PageHeader: it would set the document title after each section page sets its own. */}
      <header className="mb-4">
        <h1 className="text-2xl sm:text-3xl">Admin</h1>
        <p className="mt-1 text-sm text-muted-foreground">System-wide events, agent runs, jobs, users and integrations.</p>
      </header>
      <nav aria-label="Admin sections" className="mb-6 min-w-0">
        <ul className="flex gap-2 overflow-x-auto pb-1">
          {adminSections.map((s) => (
            <li key={s.key}>
              <Link
                to={s.to}
                activeOptions={{ exact: s.exact }}
                className={linkClass}
                activeProps={{ className: activeLinkClass }}
                inactiveProps={{ className: inactiveLinkClass }}
              >
                {s.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
      {children}
    </>
  )
}
