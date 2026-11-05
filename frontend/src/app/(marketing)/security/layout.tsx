import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /security. */
export const metadata = marketingMetadata('/security')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
