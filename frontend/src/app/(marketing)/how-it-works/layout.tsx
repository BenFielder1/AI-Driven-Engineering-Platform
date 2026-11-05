import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /how-it-works. */
export const metadata = marketingMetadata('/how-it-works')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
