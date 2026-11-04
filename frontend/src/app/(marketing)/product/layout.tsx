import type { ReactNode } from 'react'

import { marketingMetadata } from '../../../features/marketing/metadata'

/** FR-221: metadata for /product. */
export const metadata = marketingMetadata('/product')

export default function Layout({ children }: { children: ReactNode }) {
  return children
}
