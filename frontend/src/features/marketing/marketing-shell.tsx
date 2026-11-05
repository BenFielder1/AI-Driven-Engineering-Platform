'use client'

import { useEffect, type ReactNode } from 'react'

import { SkipLink } from '../../components/shared/skip-link'
import { NavigationEvents } from '../../lib/navigation'
import { initTheme } from '../../lib/theme'
import { MarketingFooter } from './components/marketing-footer'
import { MarketingHeader } from './components/marketing-header'
import { MarketingSessionProvider } from './marketing-session'

/**
 * FR-210: the marketing shell (route group `(marketing)` and the site-wide 404): skip link, sticky header, `<main>`,
 * footer. It never waits for the session: content renders at once and the header swaps its buttons when `me` resolves.
 * The app's client providers (TanStack Query, toasts) live in the `(app)` and `(public)` layouts, not here (SPEC §8
 * first-load JS budget for `/`); navigation settling and the theme following the OS (FR-204) are set up here as there.
 */
export function MarketingShell({ children }: { children: ReactNode }) {
  useEffect(() => initTheme(), [])
  return (
    <MarketingSessionProvider>
      <NavigationEvents />
      <div className="flex min-h-dvh flex-col">
        <SkipLink />
        <MarketingHeader />
        <main id="main" tabIndex={-1} className="flex-1 outline-none">
          {children}
        </main>
        <MarketingFooter />
      </div>
    </MarketingSessionProvider>
  )
}
