import type { ReactNode } from 'react'

import { Link } from '../../../lib/navigation'
import type { NavLink } from '../content/site-nav'

/**
 * A marketing nav link (FR-210): internal paths navigate client-side; external ones (docs, GitHub) open in a new tab,
 * show ↗ and say so to assistive tech. `children` replaces the label (the ↗ and the new-tab note are still added).
 */
export function SiteLink({
  link,
  className,
  onNavigate,
  children,
}: {
  link: NavLink
  className?: string
  onNavigate?: () => void
  children?: ReactNode
}) {
  const label = children ?? link.label
  if (link.external) {
    return (
      <a href={link.href} target="_blank" rel="noopener noreferrer" className={className} onClick={onNavigate}>
        {label}
        <span aria-hidden="true"> ↗</span>
        <span className="sr-only"> (opens in a new tab)</span>
      </a>
    )
  }
  return (
    <Link to={link.href} className={className} onClick={onNavigate}>
      {label}
    </Link>
  )
}
