import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /. */
export const metadata = marketingMetadata('/')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
