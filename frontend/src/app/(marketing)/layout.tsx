import type { ReactNode } from 'react'

import { MarketingShell } from '../../features/marketing/marketing-shell'

/**
 * FR-210, D-V6-9: the marketing route group. A server layout (no data) around the client shell; each route below has its
 * own server `layout.tsx` for metadata (FR-221) and a client `page.tsx` (D-V3-2).
 */
export default function MarketingLayout({ children }: { children: ReactNode }) {
  return <MarketingShell>{children}</MarketingShell>
}
