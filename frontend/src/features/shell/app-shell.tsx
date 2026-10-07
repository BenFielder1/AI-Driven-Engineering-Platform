import { Link } from '@tanstack/react-router'
import type { ReactNode } from 'react'

import { canViewAdmin } from '../../lib/permissions'
import type { User } from '../auth/auth-api'
import { HealthIndicator } from './health-indicator'
import { ThemeToggle } from './theme-toggle'
import { UserMenu } from './user-menu'

const navLinkClass = 'rounded-md px-3 py-2 text-sm font-medium text-muted hover:bg-surface hover:text-fg'
const activeNavLinkClass = 'bg-surface text-fg underline decoration-2 underline-offset-4'

export function SkipLink() {
  return (
    <a
      href="#main"
      className="sr-only z-50 rounded-md bg-primary px-4 py-2 text-on-primary focus:not-sr-only focus:fixed focus:left-4 focus:top-4"
    >
      Skip to content
    </a>
  )
}

export function Logo() {
  return (
    <Link to="/" className="text-lg font-bold tracking-tight text-fg" aria-label="AIDE home">
      AIDE
    </Link>
  )
}

function MainNav({ user }: { user: User }) {
  const links = [
    { to: '/projects', label: 'Projects' },
    { to: '/notifications', label: 'Notifications' },
    ...(canViewAdmin(user) ? [{ to: '/admin', label: 'Admin' }] : []),
  ] as const

  return (
    <nav aria-label="Main">
      <ul className="flex flex-wrap items-center gap-1">
        {links.map((link) => (
          <li key={link.to}>
            <Link to={link.to} className={navLinkClass} activeProps={{ className: activeNavLinkClass }}>
              {link.label}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  )
}

/** The signed-in app shell: skip link, top bar (logo, nav, theme, user menu), main landmark, footer. */
export function AppShell({ user, children }: { user: User; children: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col bg-canvas text-fg">
      <SkipLink />
      <header className="border-b border-border bg-surface-raised">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2">
          <Logo />
          <div className="order-last w-full sm:order-none sm:w-auto sm:flex-1">
            <MainNav user={user} />
          </div>
          <div className="ml-auto flex items-center gap-2 sm:ml-0">
            <ThemeToggle />
            <UserMenu user={user} />
          </div>
        </div>
      </header>
      <main id="main" tabIndex={-1} className="mx-auto w-full max-w-6xl flex-1 px-4 py-8 focus:outline-none">
        {children}
      </main>
      <footer className="border-t border-border">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-2 px-4 py-3 text-xs text-muted">
          <span>AIDE Platform</span>
          <HealthIndicator />
        </div>
      </footer>
    </div>
  )
}

/** The signed-out layout for /login and /register. */
export function PublicShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col bg-canvas text-fg">
      <SkipLink />
      <header className="border-b border-border">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-4 px-4 py-2">
          <Logo />
          <ThemeToggle />
        </div>
      </header>
      <main id="main" tabIndex={-1} className="flex-1 focus:outline-none">
        {children}
      </main>
    </div>
  )
}
