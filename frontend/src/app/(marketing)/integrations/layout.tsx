import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /integrations. */
export const metadata = marketingMetadata('/integrations')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
