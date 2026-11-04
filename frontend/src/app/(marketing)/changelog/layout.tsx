import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /changelog. */
export const metadata = marketingMetadata('/changelog')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
