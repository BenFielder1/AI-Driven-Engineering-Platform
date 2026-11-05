import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /faq. */
export const metadata = marketingMetadata('/faq')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
