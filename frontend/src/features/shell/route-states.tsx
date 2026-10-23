'use client'

import type { ReactNode } from 'react'

import { buttonClasses } from '../../components/ui/button-classes'
import { ErrorState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'

/** Inside the app shell `<main>` already exists, so the page frame is a plain div there. */
function Frame({ inShell, children }: { inShell: boolean; children: ReactNode }) {
  return inShell ? (
    <div className="mx-auto max-w-xl px-4 py-12">{children}</div>
  ) : (
    <main id="main" className="mx-auto max-w-xl px-4 py-12">
      {children}
    </main>
  )
}

/** The v2 not-found page (unknown URLs; `not-found.tsx` files render it). */
export function NotFoundView({ inShell = false }: { inShell?: boolean }) {
  return (
    <Frame inShell={inShell}>
      <h1 className="text-2xl font-semibold tracking-tight">Page not found</h1>
      <p className="mt-2 text-muted-foreground">The page you&apos;re looking for doesn&apos;t exist.</p>
      <Link to="/projects" className={buttonClasses('secondary', 'mt-4')}>
        Go to projects
      </Link>
    </Frame>
  )
}

/** The v2 root error state (`error.tsx` files and the route guards render it). */
export function RouteErrorView({ error, onRetry, inShell = false }: { error: unknown; onRetry?: () => void; inShell?: boolean }) {
  return (
    <Frame inShell={inShell}>
      <h1 className="mb-4 text-2xl font-semibold tracking-tight">Something went wrong</h1>
      <ErrorState error={error} onRetry={onRetry} title="The page couldn't load" />
    </Frame>
  )
}
