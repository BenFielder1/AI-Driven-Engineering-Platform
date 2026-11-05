import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /self-host. */
export const metadata = marketingMetadata('/self-host')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
