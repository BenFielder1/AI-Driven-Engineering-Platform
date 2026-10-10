import { Link } from '@tanstack/react-router'
import type { ReactNode } from 'react'

import { ForbiddenState } from '../../components/ui/states'
import { canViewAdmin } from '../../lib/permissions'
import { useCurrentUser } from '../auth/auth-api'
import { adminSections } from './admin-sections'

const linkClass = '-mb-px inline-flex min-h-10 items-center border-b-2 px-3 py-2 text-sm font-medium'
const activeLinkClass = 'border-primary text-fg'
const inactiveLinkClass = 'border-transparent text-muted hover:text-fg'

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
      <h1 className="mb-4 text-2xl font-semibold tracking-tight">Admin</h1>
      <nav aria-label="Admin sections" className="mb-6">
        <ul className="flex flex-wrap gap-1 border-b border-border">
          {adminSections.map((s) => (
            <li key={s.to}>
              <Link
                to={s.to}
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
