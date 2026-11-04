'use client'

import type { ReactNode } from 'react'

import { SkipLink } from '../../components/shared/skip-link'
import { MarketingFooter } from './components/marketing-footer'
import { MarketingHeader } from './components/marketing-header'

/**
 * FR-210: the marketing shell (route group `(marketing)` and the site-wide 404): skip link, sticky header, `<main>`,
 * footer. It never waits for the session: content renders at once and the header swaps its buttons when `me` resolves.
 */
export function MarketingShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col">
      <SkipLink />
      <MarketingHeader />
      <main id="main" tabIndex={-1} className="flex-1 outline-none">
        {children}
      </main>
      <MarketingFooter />
    </div>
  )
}
