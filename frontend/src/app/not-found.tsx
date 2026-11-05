import type { Metadata } from 'next'

import { MarketingShell } from '../features/marketing/marketing-shell'
import { MarketingNotFoundPage } from '../features/marketing/pages/not-found-page'

/** Next adds `noindex` to 404 responses itself. */
export const metadata: Metadata = { title: { absolute: 'Page not found · Tevrin' } }

/**
 * Unknown URLs (FR-95, SPEC §7 "marketing 404"), HTTP 404. Next serves the root not-found for any URL no route matches,
 * signed in or not, so it renders the marketing shell (whose header offers Open app to a signed-in user) with links to
 * Home and the docs. A `notFound()` inside the app keeps the in-shell view ((app)/not-found.tsx).
 */
export default function NotFound() {
  return (
    <MarketingShell>
      <MarketingNotFoundPage />
    </MarketingShell>
  )
}
